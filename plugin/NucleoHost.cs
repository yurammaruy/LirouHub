using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LirouHub;

public enum Nivel { Ok, Aviso, Erro }

public sealed record Achado(Nivel Nivel, string Texto);

/// <summary>Um plugin que o núcleo liga por dentro, a partir dos arquivos já baixados.</summary>
public sealed class NucleoItem
{
    private readonly List<string> linhas = new();
    private readonly List<Achado> achados = new();

    public NucleoItem(string nome, string repo, bool opcional = false)
    {
        Nome = nome;
        Repo = repo;
        Opcional = opcional;
    }

    /// <summary>Se der problema, os outros continuam ligando (usado nos plugins de combate).</summary>
    public bool Opcional { get; }

    public string Nome { get; }
    public string Repo { get; }
    public Situacao Estado { get; set; } = Situacao.Pendente;
    public string Msg { get; set; } = "";
    public string[] Ipcs { get; set; } = Array.Empty<string>();
    public List<string> RecursosCopiados { get; } = new();

    public Assembly? Assembly { get; internal set; }
    public object? Instancia { get; internal set; }
    internal ModuloContext? Contexto { get; set; }

    public void Add(string t) { lock (linhas) linhas.Add($"{DateTime.Now:HH:mm:ss}  {t}"); }
    public string[] Linhas() { lock (linhas) return linhas.ToArray(); }

    public void AddAchado(Nivel n, string t) { lock (achados) achados.Add(new Achado(n, t)); }
    public Achado[] Achados() { lock (achados) return achados.ToArray(); }
    public void LimparAchados() { lock (achados) achados.Clear(); }

    public bool TemErro => Achados().Any(a => a.Nivel == Nivel.Erro);
    public bool TemAviso => Achados().Any(a => a.Nivel == Nivel.Aviso);

    public void Limpar()
    {
        lock (linhas) linhas.Clear();
        Estado = Situacao.Pendente;
        Msg = "";
        Ipcs = Array.Empty<string>();
        RecursosCopiados.Clear();
    }
}

/// <summary>
/// Liga, por dentro do Lirou Hub, os plugins de que o bot depende (na ordem), usando os arquivos
/// que a verificação já baixou. Antes de ligar, confere tudo; só liga quando o usuário aperta o botão.
/// </summary>
public sealed class NucleoHost : IDisposable
{
    // Nível de API do Dalamud para o qual o Lirou Hub é compilado (igual ao do release.yml).
    private const int NivelApiHub = 15;

    private static readonly AssemblyLoadContext HostCtx =
        AssemblyLoadContext.GetLoadContext(typeof(NucleoHost).Assembly)!;

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<Type, object> servicos;
    private readonly string raizArquivos;
    private readonly ConfigSandbox sandbox;
    private volatile bool ocupado;

    // Janelas dos plugins internos (para escondê-las: a única interface visível é a do Lirou Hub).
    private readonly object travaJanelas = new();
    private readonly HashSet<WindowSystem> sistemas = new();
    private readonly Stopwatch relogioJanelas = Stopwatch.StartNew();
    private long ultimaBusca = -10000;

    public NucleoHost(IDalamudPluginInterface pi, IFramework framework, IPluginLog log,
        Dictionary<Type, object> servicos, string raizArquivos)
    {
        this.pi = pi;
        this.framework = framework;
        this.log = log;
        this.servicos = servicos;
        this.raizArquivos = raizArquivos;
        sandbox = new ConfigSandbox(pi.ConfigDirectory.FullName, pi.ConfigFile.FullName);

        // Ordem de ligação: quem os outros usam vem primeiro.
        Itens = new[]
        {
            new NucleoItem("vnavmesh", "Puni.sh (veyn)"),
            new NucleoItem("TextAdvance", "NightmareXIV"),
            new NucleoItem("Lifestream", "NightmareXIV"),
            new NucleoItem("BossMod", "Puni.sh (veyn)", opcional: true),
            new NucleoItem("RotationSolver", "Combat Reborn", opcional: true),
            new NucleoItem("WrathCombo", "Puni.sh (ment)", opcional: true),
            new NucleoItem("Questionable", "Puni.sh (ment)"),
        };
    }

    public IReadOnlyList<NucleoItem> Itens { get; }
    public bool Ocupado => ocupado;
    public bool Ligado { get; private set; }
    public bool VerificacaoFeita { get; private set; }
    public bool PodeLigar => VerificacaoFeita && !Itens.Any(i => !i.Opcional && i.TemErro);

    /// <summary>O Questionable está ligado por dentro do Hub.</summary>
    public bool QuestionablePronto => Itens.Any(i => i.Nome == "Questionable" && i.Estado == Situacao.Ok);

    public int JanelasOcultadas { get; private set; }
    public int SistemasDeJanelas { get { lock (travaJanelas) return sistemas.Count; } }

    // ---------------------------------------------------------------- conferência antes de ligar

    public void Verificar()
    {
        if (ocupado) return;
        ocupado = true;
        VerificacaoFeita = false;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var m in Itens) m.LimparAchados();

                List<string> ligados;
                try
                {
                    ligados = await framework.RunOnFrameworkThread(() =>
                        pi.InstalledPlugins.Where(p => p.IsLoaded).Select(p => p.InternalName).ToList());
                }
                catch (Exception ex)
                {
                    ligados = new List<string>();
                    log.Error(ex, "[LirouHub] Não consegui listar os plugins instalados");
                }

                var vistos = new Dictionary<string, (string Modulo, long Tam)>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in Itens) VerificarUm(m, ligados, vistos);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[LirouHub] Falha na conferência do núcleo");
                foreach (var m in Itens) m.AddAchado(Nivel.Erro, "Falha na conferência: " + ex.Message);
            }
            finally
            {
                VerificacaoFeita = true;
                ocupado = false;
            }
        });
    }

    private void VerificarUm(NucleoItem m, List<string> ligados, Dictionary<string, (string Modulo, long Tam)> vistos)
    {
        try
        {
            var pasta = ModuleHost.PastaAtual(Path.Combine(raizArquivos, ModuleHost.Segura(m.Repo), ModuleHost.Segura(m.Nome)));
            if (pasta == null)
            {
                m.AddAchado(Nivel.Erro, "Arquivos não encontrados. Deixe a verificação dos arquivos terminar primeiro.");
                return;
            }

            var dll = Directory.GetFiles(pasta, m.Nome + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            if (dll == null)
            {
                m.AddAchado(Nivel.Erro, $"Não achei {m.Nome}.dll nos arquivos baixados.");
                return;
            }
            m.AddAchado(Nivel.Ok, "Arquivos encontrados.");

            // 1) O original ligado duplicaria comandos e IPCs.
            if (ligados.Any(n => string.Equals(n, m.Nome, StringComparison.OrdinalIgnoreCase)))
                m.AddAchado(Nivel.Erro, $"O plugin original '{m.Nome}' está ligado em /xlplugins. Desative-o, senão comandos e IPCs duplicam.");
            else
                m.AddAchado(Nivel.Ok, "O original não está ligado.");

            // 2) Nível da API do Dalamud.
            var manifest = Directory.GetFiles(pasta, m.Nome + ".json", SearchOption.AllDirectories).FirstOrDefault();
            if (manifest != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("DalamudApiLevel", out var v) && v.TryGetInt32(out var nivel))
                    {
                        if (nivel != NivelApiHub)
                            m.AddAchado(Nivel.Aviso, $"Feito para a API {nivel} do Dalamud; o Lirou Hub usa a {NivelApiHub}.");
                        else
                            m.AddAchado(Nivel.Ok, $"API do Dalamud compatível ({nivel}).");
                    }
                }
                catch { /* manifest ilegível: ignora */ }
            }

            // 3) Inspeção do código, sem executar nada (em um espaço descartável).
            var pastaDll = Path.GetDirectoryName(dll)!;
            ChecarRecursos(m, pastaDll, vistos);

            var proprias = Directory.GetFiles(pastaDll, "*.dll")
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .Where(x => ModuloContext.Compartilhada(x))
                .ToList();
            if (proprias.Count > 0)
                m.AddAchado(Nivel.Aviso, "O zip traz cópias próprias de: " + string.Join(", ", proprias) +
                                         ". O Hub as ignora e usa as do Dalamud.");

            var ctx = new ModuloContext(m.Nome + ".inspecao", pastaDll);
            try
            {
                var asm = ctx.LoadFromAssemblyPath(dll);
                ChecarReferencias(m, asm, pastaDll);

                Type[] tipos;
                try
                {
                    tipos = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException rex)
                {
                    var motivos = rex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().Take(6);
                    m.AddAchado(Nivel.Erro, "Partes do código não carregam: " + string.Join(" | ", motivos));
                    return;
                }

                var tipo = tipos.FirstOrDefault(EhEntrada);
                if (tipo == null)
                {
                    var alt = LocalizarEntrada(ctx, dll, pastaDll);
                    if (alt != null)
                    {
                        tipo = alt.Tipo;
                        m.AddAchado(Nivel.Aviso, $"A classe de entrada está em {Path.GetFileName(alt.Dll)}, não em {m.Nome}.dll. O Hub vai usá-la.");
                    }
                    else
                    {
                        m.AddAchado(Nivel.Erro, "Não achei a classe de entrada (IDalamudPlugin) em nenhuma DLL da pasta. " +
                                                DiagnosticoEntrada(tipos, pastaDll, m.Nome));
                        return;
                    }
                }

                if (!typeof(IDalamudPlugin).IsAssignableFrom(tipo))
                    m.AddAchado(Nivel.Ok, "Plugin no formato novo (IAsyncDalamudPlugin): o Hub chama o LoadAsync dele ao ligar.");

                var faltam = PecasFaltando(tipo);
                if (faltam.Count == 0)
                    m.AddAchado(Nivel.Ok, "Todas as peças do jogo que ele pede estão disponíveis.");
                else
                    m.AddAchado(Nivel.Erro, "Faltam estas peças do jogo no Lirou Hub: " + string.Join(", ", faltam));
            }
            finally
            {
                try { ctx.Unload(); } catch { /* ignora */ }
            }
        }
        catch (Exception ex)
        {
            m.AddAchado(Nivel.Erro, "Falha ao inspecionar: " + ex.Message);
        }
    }

    private static void ChecarReferencias(NucleoItem m, Assembly asm, string pasta)
    {
        var faltando = new List<string>();
        var versoes = new List<string>();

        foreach (var r in asm.GetReferencedAssemblies())
        {
            var n = r.Name ?? "";
            if (n.Length == 0 || n is "mscorlib" or "netstandard" or "WindowsBase") continue;

            Assembly? doHost = null;
            try { doHost = HostCtx.LoadFromAssemblyName(new AssemblyName(n)); }
            catch { /* o Dalamud não tem essa biblioteca */ }

            if (doHost != null)
            {
                var vh = doHost.GetName().Version;
                var vr = r.Version;
                if (vh != null && vr != null && vh.Major < vr.Major)
                    versoes.Add($"{n}: o plugin pede {vr}, o Dalamud tem {vh}");
                continue;
            }

            if (!File.Exists(Path.Combine(pasta, n + ".dll"))) faltando.Add(n);
        }

        if (faltando.Count > 0)
            m.AddAchado(Nivel.Erro, "Faltam bibliotecas que o plugin usa: " + string.Join(", ", faltando));
        if (versoes.Count > 0)
            m.AddAchado(Nivel.Aviso, "Versões diferentes das bibliotecas: " + string.Join("; ", versoes));
        if (faltando.Count == 0 && versoes.Count == 0)
            m.AddAchado(Nivel.Ok, "Todas as bibliotecas necessárias estão disponíveis.");
    }

    /// <summary>Classe de entrada de um plugin: formato antigo (IDalamudPlugin) ou novo (IAsyncDalamudPlugin).</summary>
    private static bool EhEntrada(Type t)
    {
        if (!t.IsClass || t.IsAbstract) return false;
        try
        {
            return typeof(IDalamudPlugin).IsAssignableFrom(t)
                   || t.GetInterfaces().Any(i => i.Name == "IAsyncDalamudPlugin");
        }
        catch { return false; }
    }

    private sealed record Entrada(Assembly Asm, Type Tipo, string Dll);

    /// <summary>Procura a classe de entrada (IDalamudPlugin) nas outras DLLs da pasta do plugin.</summary>
    private static Entrada? LocalizarEntrada(ModuloContext ctx, string dllPrincipal, string pasta)
    {
        foreach (var f in Directory.GetFiles(pasta, "*.dll"))
        {
            if (string.Equals(f, dllPrincipal, StringComparison.OrdinalIgnoreCase)) continue;
            var nome = Path.GetFileNameWithoutExtension(f) ?? "";
            if (ModuloContext.Compartilhada(nome) || nome.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || nome.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var asm = ctx.LoadFromAssemblyPath(f);
                Type[] tipos;
                try { tipos = asm.GetTypes(); }
                catch (ReflectionTypeLoadException r) { tipos = r.Types.Where(t => t != null).ToArray()!; }

                var tipo = tipos.FirstOrDefault(EhEntrada);
                if (tipo != null) return new Entrada(asm, tipo, f);
            }
            catch { /* DLL nativa ou ilegível: ignora */ }
        }
        return null;
    }

    /// <summary>Explica por que nenhuma classe de entrada foi reconhecida.</summary>
    private static string DiagnosticoEntrada(Type[] tipos, string pastaDll, string nome)
    {
        var sb = new StringBuilder();
        try
        {
            var porNome = tipos
                .Where(t => t.IsClass && !t.IsAbstract && t.GetInterfaces().Any(i => i.Name == "IDalamudPlugin"))
                .ToList();
            if (porNome.Count > 0)
            {
                var onde = porNome[0].GetInterfaces().First(i => i.Name == "IDalamudPlugin").Assembly.Location;
                sb.Append($"Achei {string.Join(", ", porNome.Select(t => t.FullName))}, mas a IDalamudPlugin dele vem de outra cópia do Dalamud ({onde}).");
            }
            else
            {
                sb.Append($"Nenhuma classe de {nome}.dll implementa IDalamudPlugin ({tipos.Length} tipos). ");
                var cand = tipos
                    .Where(t => t.Name.Contains("Plugin", StringComparison.OrdinalIgnoreCase))
                    .Take(12)
                    .Select(t =>
                    {
                        string ifs;
                        try { ifs = string.Join("/", t.GetInterfaces().Select(i => i.Name).Take(8)); }
                        catch (Exception ex) { ifs = "?" + ex.GetType().Name; }
                        return $"{t.FullName} (base {t.BaseType?.Name}; interfaces {ifs})";
                    })
                    .ToList();
                sb.Append(cand.Count > 0
                    ? "Classes com 'Plugin' no nome: " + string.Join(" | ", cand) + ". "
                    : "Nenhuma classe tem 'Plugin' no nome. ");
                sb.Append("IDalamudPlugin do Hub vem de: " + typeof(IDalamudPlugin).Assembly.Location + ".");
            }
        }
        catch (Exception ex)
        {
            sb.Append("(não consegui detalhar: " + ex.Message + ")");
        }
        return sb.ToString();
    }

    private static bool TemPluginService(MemberInfo mem)
    {
        try { return mem.GetCustomAttributes(false).Any(a => a.GetType().Name == "PluginServiceAttribute"); }
        catch { return false; }
    }

    private List<string> PecasFaltando(Type tipo)
    {
        var faltam = new List<string>();

        var ctor = tipo.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor != null)
        {
            foreach (var p in ctor.GetParameters())
            {
                var pt = p.ParameterType;
                if (pt != typeof(IDalamudPluginInterface) && !servicos.ContainsKey(pt))
                    faltam.Add(pt.Name);
            }
        }

        const BindingFlags Todos = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = tipo; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var mem in t.GetMembers(Todos))
            {
                if (mem is not (PropertyInfo or FieldInfo)) continue;
                if (!TemPluginService(mem)) continue;
                var mt = mem is PropertyInfo pr ? pr.PropertyType : ((FieldInfo)mem).FieldType;
                if (mt != typeof(IDalamudPluginInterface) && !servicos.ContainsKey(mt))
                    faltam.Add(mt.Name);
            }
        }

        return faltam.Distinct().ToList();
    }

    // ---------------------------------------------------------------- ligar e desligar

    public void Ligar()
    {
        if (ocupado || Ligado || !PodeLigar) return;
        ocupado = true;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var m in Itens) m.Limpar();

                await framework.RunOnFrameworkThread(() => sandbox.Backup(t => Itens[0].Add(t)));

                var falhou = false;
                foreach (var m in Itens)
                {
                    if (falhou)
                    {
                        m.Msg = "Não tentado (um módulo anterior falhou).";
                        continue;
                    }
                    if (m.Opcional && m.TemErro)
                    {
                        m.Estado = Situacao.Pulado;
                        m.Msg = "Não ligado: a conferência achou problema (veja abaixo). O resto segue normalmente.";
                        continue;
                    }
                    await LigarUmAsync(m);
                    if (m.Estado == Situacao.Erro && !m.Opcional) falhou = true;
                }
                Ligado = Itens.Any(i => i.Estado == Situacao.Ok);
            }
            finally { ocupado = false; }
        });
    }

    public void Desligar()
    {
        if (ocupado) return;
        ocupado = true;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var m in Itens.Reverse())
                    await DescarregarAsync(m);
                lock (travaJanelas) sistemas.Clear();
                foreach (var m in Itens)
                {
                    m.Estado = Situacao.Pendente;
                    m.Msg = "Desligado.";
                }
                Ligado = false;
            }
            finally { ocupado = false; }
        });
    }

    private async Task LigarUmAsync(NucleoItem m)
    {
        m.Estado = Situacao.Trabalhando;
        try
        {
            var pasta = ModuleHost.PastaAtual(Path.Combine(raizArquivos, ModuleHost.Segura(m.Repo), ModuleHost.Segura(m.Nome)));
            if (pasta == null)
                throw new DirectoryNotFoundException("Arquivos não encontrados (rode a verificação dos arquivos primeiro).");

            var dll = Directory.GetFiles(pasta, m.Nome + ".dll", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException($"Não achei {m.Nome}.dll em {pasta}");
            m.Add("DLL: " + dll);

            var ctx = new ModuloContext(m.Nome, Path.GetDirectoryName(dll)!);
            m.Contexto = ctx;
            var asm = ctx.LoadFromAssemblyPath(dll);
            m.Assembly = asm;

            Type[] tipos;
            try
            {
                tipos = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rex)
            {
                foreach (var le in rex.LoaderExceptions)
                    m.Add("Peça que não carregou: " + le?.Message);
                throw new Exception("O plugin pede peças que o hospedeiro não tem (veja os detalhes).");
            }

            m.Ipcs = ProcurarIpc(tipos, m.Nome);
            m.Add($"IPCs encontradas no código: {m.Ipcs.Length}");

            var tipo = tipos.FirstOrDefault(EhEntrada);
            if (tipo == null)
            {
                var alt = LocalizarEntrada(ctx, dll, Path.GetDirectoryName(dll)!);
                if (alt == null)
                    throw new Exception("Não achei a classe de entrada (IDalamudPlugin) em nenhuma DLL da pasta. " +
                                        DiagnosticoEntrada(tipos, Path.GetDirectoryName(dll)!, m.Nome));
                m.Add($"A classe de entrada está em {Path.GetFileName(alt.Dll)}, não em {m.Nome}.dll.");
                m.Assembly = alt.Asm;
                tipo = alt.Tipo;
            }
            m.Add("Classe de entrada: " + tipo.FullName);

            CopiarRecursos(m, Path.GetDirectoryName(dll)!);

            await framework.RunOnFrameworkThread(() =>
            {
                sandbox.PrepararPara(m.Nome, m.Add);
                Instanciar(m, tipo);
            });

            // Formato novo: depois do construtor o Dalamud chama LoadAsync (fora da thread do jogo, até 60 s).
            if (m.Instancia is not IDalamudPlugin) await ChamarLoadAsync(m);

            await framework.RunOnFrameworkThread(() => sandbox.GuardarDe(m.Nome, m.Add));

            m.Estado = Situacao.Ok;
            m.Msg = "Ligado.";
            m.Add("OK: plugin iniciado.");
        }
        catch (Exception ex)
        {
            m.Estado = Situacao.Erro;
            m.Msg = ex.Message;
            m.Add("ERRO: " + ex);
            try
            {
                var pastaErro = ModuleHost.PastaAtual(Path.Combine(raizArquivos, ModuleHost.Segura(m.Repo), ModuleHost.Segura(m.Nome)));
                if (pastaErro != null && Directory.Exists(pastaErro))
                {
                    var arqs = Directory.EnumerateFileSystemEntries(pastaErro, "*", SearchOption.TopDirectoryOnly)
                        .Select(f => Path.GetFileName(f) + (Directory.Exists(f) ? "/" : ""))
                        .Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        .Take(60);
                    m.Add("Itens (não DLL) na pasta do plugin: " + string.Join(", ", arqs));
                }
            }
            catch { /* só diagnóstico */ }
            log.Error(ex, "[LirouHub] Falha ao ligar " + m.Nome);
            try { await DescarregarAsync(m); } catch { /* ignora */ }
        }
    }

    private void Instanciar(NucleoItem m, Type tipo)
    {
        var ctor = tipo.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new Exception("A classe de entrada não tem construtor público.");
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        var faltam = new List<string>();

        // Peças em campos estáticos marcados com [PluginService] (jeito antigo do Dalamud).
        const BindingFlags Todos = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = tipo; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var mem in t.GetMembers(Todos))
            {
                if (mem is not (PropertyInfo or FieldInfo)) continue;
                if (!TemPluginService(mem)) continue;

                var mt = mem is PropertyInfo pr ? pr.PropertyType : ((FieldInfo)mem).FieldType;
                object? valor;
                if (mt == typeof(IDalamudPluginInterface))
                {
                    valor = pi;
                }
                else if (!servicos.TryGetValue(mt, out valor))
                {
                    faltam.Add(mt.FullName ?? mt.Name);
                    continue;
                }

                try
                {
                    if (mem is PropertyInfo p2) p2.SetValue(null, valor);
                    else ((FieldInfo)mem).SetValue(null, valor);
                }
                catch (Exception ex)
                {
                    m.Add($"Não consegui preencher {mem.Name}: {ex.Message}");
                    faltam.Add(mt.FullName ?? mt.Name);
                }
            }
        }

        for (var i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            if (pt == typeof(IDalamudPluginInterface))
            {
                args[i] = pi;
            }
            else if (servicos.TryGetValue(pt, out var s))
            {
                args[i] = s;
            }
            else
            {
                faltam.Add(pt.FullName ?? pt.Name);
            }
        }

        if (faltam.Count > 0)
            throw new Exception("Faltou entregar estas peças ao plugin: " + string.Join(", ", faltam.Distinct()));

        try
        {
            m.Instancia = ctor.Invoke(args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    /// <summary>
    /// Procura, sem executar nada do plugin, os nomes de IPC: constantes de texto e textos escritos
    /// dentro do código (instruções ldstr) que começam com o nome do plugin, e classes com "Ipc" no nome.
    /// </summary>
    private static string[] ProcurarIpc(Type[] tipos, string nome)
    {
        var res = new List<string>();
        var textos = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tipos)
        {
            try
            {
                if (t.Name.Contains("Ipc", StringComparison.OrdinalIgnoreCase))
                {
                    var metodos = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .Where(x => !x.IsSpecialName)
                        .Select(x => x.Name)
                        .Take(30);
                    res.Add($"[classe] {t.FullName}: {string.Join(", ", metodos)}");

                    foreach (var campo in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                                      | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!campo.FieldType.Name.Contains("CallGate", StringComparison.OrdinalIgnoreCase)) continue;
                        res.Add($"[campo] {t.Name}.{campo.Name}: {NomeTipo(campo.FieldType)}");
                    }
                }

                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                    if (f.GetRawConstantValue() is string v && v.StartsWith(nome + ".", StringComparison.OrdinalIgnoreCase))
                        textos.Add(v);
                }
            }
            catch { /* tipo difícil de inspecionar: ignora */ }
        }

        // Primeiro nas classes de IPC; se nada aparecer, em todo o código.
        foreach (var t in tipos.Where(x => x.Name.Contains("Ipc", StringComparison.OrdinalIgnoreCase)))
            foreach (var s in TextosDoTipo(t, nome)) textos.Add(s);
        if (textos.Count == 0)
        {
            foreach (var t in tipos)
            {
                foreach (var s in TextosDoTipo(t, nome)) textos.Add(s);
                if (textos.Count >= 200) break;
            }
        }

        foreach (var s in textos.Take(200)) res.Add("[nome] " + s);
        return res.ToArray();
    }

    private static string NomeTipo(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var nome = t.Name;
        var i = nome.IndexOf('`');
        if (i > 0) nome = nome[..i];
        return nome + "<" + string.Join(", ", t.GetGenericArguments().Select(NomeTipo)) + ">";
    }

    private static List<string> TextosDoTipo(Type t, string nome)
    {
        var achados = new List<string>();
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                               | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var metodos = new List<MethodBase>();
        try
        {
            metodos.AddRange(t.GetMethods(F));
            metodos.AddRange(t.GetConstructors(F));
        }
        catch { return achados; }

        foreach (var mb in metodos)
        {
            byte[]? il;
            try { il = mb.GetMethodBody()?.GetILAsByteArray(); }
            catch { continue; }
            if (il == null) continue;

            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x72) continue; // ldstr
                var token = BitConverter.ToInt32(il, i + 1);
                if ((token >> 24) != 0x70) continue; // não é um texto
                try
                {
                    var s = mb.Module.ResolveString(token);
                    if (s.Length < 120 && s.StartsWith(nome + ".", StringComparison.OrdinalIgnoreCase))
                        achados.Add(s);
                    i += 4;
                }
                catch { /* não era um texto de verdade */ }
            }
        }
        return achados;
    }

    // ------------------------------------------------------------ janelas dos plugins internos

    /// <summary>
    /// Fecha as janelas dos plugins internos. Chamar a cada quadro, na thread do jogo.
    /// Descobre os sistemas de janelas (WindowSystem) pelos assinantes do Draw do Dalamud e pelos campos
    /// dos próprios plugins, e mantém fechadas todas as janelas que não são do Lirou Hub.
    /// </summary>
    public void OcultarJanelas()
    {
        if (!Itens.Any(i => i.Instancia != null)) return;

        lock (travaJanelas)
        {
            if (relogioJanelas.ElapsedMilliseconds - ultimaBusca > 2000)
            {
                ultimaBusca = relogioJanelas.ElapsedMilliseconds;
                DescobrirJanelas();
            }

            foreach (var ws in sistemas)
            {
                if (ws.Namespace == "LirouHub") continue;
                try
                {
                    foreach (var w in ws.Windows)
                    {
                        if (!w.IsOpen) continue;
                        w.IsOpen = false;
                        JanelasOcultadas++;
                    }
                }
                catch { /* janela em estado estranho: ignora */ }
            }
        }
    }

    private void DescobrirJanelas()
    {
        // 1) Quem assina o Draw do UiBuilder (os plugins internos usam o mesmo).
        try
        {
            var ui = pi.UiBuilder;
            foreach (var f in ui.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                if (f.GetValue(ui) is not Delegate d) continue;
                foreach (var h in d.GetInvocationList())
                    if (h.Target is WindowSystem ws) sistemas.Add(ws);
            }
        }
        catch { /* estrutura interna diferente: usa só o passo 2 */ }

        // 2) Campos dos plugins (WindowSystem direto ou dentro do provedor de serviços).
        foreach (var m in Itens)
        {
            var inst = m.Instancia;
            if (inst == null) continue;
            try
            {
                foreach (var f in inst.GetType().GetFields(BindingFlags.Instance | BindingFlags.Static
                                                           | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object? v;
                    try { v = f.GetValue(f.IsStatic ? null : inst); }
                    catch { continue; }

                    if (v is WindowSystem ws) sistemas.Add(ws);
                    else if (v is IServiceProvider sp)
                    {
                        try { if (sp.GetService(typeof(WindowSystem)) is WindowSystem w2) sistemas.Add(w2); }
                        catch { /* serviço não registrado */ }
                    }
                }
            }
            catch { /* ignora */ }
        }
    }

    // ------------------------------------------------------------ arquivos de recursos
    // Muitos plugins leem arquivos "ao lado da DLL" (ex.: Resources\I18N.xml) usando a pasta de instalação
    // que o Dalamud informa. Como eles rodam dentro do Lirou Hub, essa pasta é a do Hub: copiamos para lá.

    private static IEnumerable<(string Rel, string Full)> ListarRecursos(string pastaModulo)
    {
        var proibidas = new[] { ".dll", ".pdb", ".json", ".zip", ".deps" };
        foreach (var arq in Directory.GetFiles(pastaModulo))
        {
            if (proibidas.Contains(Path.GetExtension(arq), StringComparer.OrdinalIgnoreCase)) continue;
            yield return (Path.GetFileName(arq), arq);
        }
        foreach (var dir in Directory.GetDirectories(pastaModulo))
        {
            var nome = Path.GetFileName(dir);
            if (nome.Equals("runtimes", StringComparison.OrdinalIgnoreCase)
                || nome.Equals("images", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var arq in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(pastaModulo, arq), arq);
        }
    }

    private void ChecarRecursos(NucleoItem m, string pastaModulo, Dictionary<string, (string Modulo, long Tam)> vistos)
    {
        try
        {
            var arquivos = ListarRecursos(pastaModulo).ToList();
            if (arquivos.Count == 0) return;

            var conflitos = new List<string>();
            foreach (var (rel, full) in arquivos)
            {
                var tam = new FileInfo(full).Length;
                if (vistos.TryGetValue(rel, out var outro))
                {
                    if (outro.Tam != tam) conflitos.Add($"{rel} (também em {outro.Modulo})");
                }
                else
                {
                    vistos[rel] = (m.Nome, tam);
                }
            }

            m.AddAchado(Nivel.Ok, $"{arquivos.Count} arquivos de recursos serão copiados para a pasta do Hub (alguns plugins leem arquivos ao lado da DLL).");
            if (conflitos.Count > 0)
                m.AddAchado(Nivel.Aviso, "Recursos com o mesmo caminho de outro plugin e conteúdo diferente (vale o do primeiro a ligar): "
                                         + string.Join("; ", conflitos.Take(5)));

            var raiz = pi.AssemblyLocation.DirectoryName;
            if (string.IsNullOrEmpty(raiz))
            {
                m.AddAchado(Nivel.Erro, "Não achei a pasta de instalação do Lirou Hub para copiar os recursos.");
                return;
            }
            try
            {
                var teste = Path.Combine(raiz, ".lirouhub-teste");
                File.WriteAllText(teste, "x");
                File.Delete(teste);
            }
            catch (Exception ex)
            {
                m.AddAchado(Nivel.Erro, "A pasta de instalação do Lirou Hub não aceita gravação: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            m.AddAchado(Nivel.Aviso, "Não consegui listar os recursos: " + ex.Message);
        }
    }

    private void CopiarRecursos(NucleoItem m, string pastaModulo)
    {
        var raiz = pi.AssemblyLocation.DirectoryName;
        if (string.IsNullOrEmpty(raiz))
        {
            m.Add("Não achei a pasta de instalação do Hub: recursos não copiados.");
            return;
        }

        var copiados = 0;
        var iguais = 0;
        var conflitos = new List<string>();
        foreach (var (rel, full) in ListarRecursos(pastaModulo))
        {
            var destino = Path.Combine(raiz, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                if (File.Exists(destino))
                {
                    if (new FileInfo(destino).Length == new FileInfo(full).Length) iguais++;
                    else conflitos.Add(rel);
                    continue;
                }
                File.Copy(full, destino);
                m.RecursosCopiados.Add(destino);
                copiados++;
            }
            catch (Exception ex)
            {
                m.Add($"Não consegui copiar {rel}: {ex.Message}");
            }
        }

        m.Add($"Recursos na pasta do Hub: {copiados} copiados, {iguais} já existiam"
              + (conflitos.Count > 0 ? $", {conflitos.Count} em conflito (mantido o anterior): {string.Join(", ", conflitos.Take(3))}" : "") + ".");
    }

    private static void LimparRecursos(NucleoItem m)
    {
        foreach (var f in m.RecursosCopiados)
        {
            try { File.Delete(f); } catch { /* em uso: fica */ }
        }
        m.RecursosCopiados.Clear();
    }

    /// <summary>Chama o LoadAsync de um plugin no formato novo (IAsyncDalamudPlugin).</summary>
    private async Task ChamarLoadAsync(NucleoItem m)
    {
        var inst = m.Instancia;
        if (inst == null) return;

        var itf = inst.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IAsyncDalamudPlugin");
        var metodo = itf?.GetMethod("LoadAsync")
                     ?? inst.GetType().GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (metodo == null)
        {
            m.Add("Plugin no formato novo sem LoadAsync: nada a chamar.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var args = metodo.GetParameters()
            .Select(p => p.ParameterType == typeof(CancellationToken)
                ? (object?)cts.Token
                : (p.HasDefaultValue ? p.DefaultValue : null))
            .ToArray();

        m.Add("Chamando LoadAsync...");
        Task? tarefa;
        try
        {
            tarefa = metodo.Invoke(inst, args) as Task;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        if (tarefa != null) await tarefa.WaitAsync(TimeSpan.FromSeconds(65));
        m.Add("LoadAsync concluído.");
    }

    /// <summary>Desliga um plugin: Dispose na thread do jogo (formato antigo) ou DisposeAsync (formato novo).</summary>
    private async Task DescarregarAsync(NucleoItem m)
    {
        var inst = m.Instancia;
        m.Instancia = null;
        m.Assembly = null;
        try
        {
            if (inst is IDalamudPlugin p) await framework.RunOnFrameworkThread(() => p.Dispose());
            else if (inst is IAsyncDisposable ad) await ad.DisposeAsync();
        }
        catch (Exception ex) { log.Error(ex, "[LirouHub] Erro ao desligar " + m.Nome); }
        try { m.Contexto?.Unload(); } catch { /* ignora */ }
        m.Contexto = null;
        LimparRecursos(m);
    }

    /// <summary>Usado só quando o próprio Lirou Hub é descarregado (já está na thread do jogo).</summary>
    private void DescarregarSincrono(NucleoItem m)
    {
        var inst = m.Instancia;
        m.Instancia = null;
        m.Assembly = null;
        try
        {
            if (inst is IDalamudPlugin p) p.Dispose();
            else if (inst is IAsyncDisposable ad)
                Task.Run(async () => await ad.DisposeAsync()).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { log.Error(ex, "[LirouHub] Erro ao desligar " + m.Nome); }
        try { m.Contexto?.Unload(); } catch { /* ignora */ }
        m.Contexto = null;
        LimparRecursos(m);
    }

    public string Relatorio()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== Núcleo (ligado: {Ligado}, conferência feita: {VerificacaoFeita}, pode ligar: {PodeLigar})");
        foreach (var m in Itens)
        {
            sb.AppendLine($"   {m.Nome} [{m.Repo}]: {m.Estado}  {m.Msg}");
            foreach (var a in m.Achados()) sb.AppendLine($"      [{a.Nivel}] {a.Texto}");
            foreach (var l in m.Linhas()) sb.AppendLine("      " + l);
            foreach (var i in m.Ipcs) sb.AppendLine("      IPC: " + i);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var m in Itens.Reverse()) DescarregarSincrono(m);
        Ligado = false;
    }
}
