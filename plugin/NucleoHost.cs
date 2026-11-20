using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LirouHub;

/// <summary>Um plugin que o núcleo liga por dentro, a partir dos arquivos já baixados.</summary>
public sealed class NucleoItem
{
    private readonly List<string> linhas = new();

    public NucleoItem(string nome, string repo)
    {
        Nome = nome;
        Repo = repo;
    }

    public string Nome { get; }
    public string Repo { get; }
    public Situacao Estado { get; set; } = Situacao.Pendente;
    public string Msg { get; set; } = "";
    public string[] Ipcs { get; set; } = Array.Empty<string>();

    public Assembly? Assembly { get; internal set; }
    public IDalamudPlugin? Instancia { get; internal set; }
    internal ModuloContext? Contexto { get; set; }

    public void Add(string t) { lock (linhas) linhas.Add($"{DateTime.Now:HH:mm:ss}  {t}"); }
    public string[] Linhas() { lock (linhas) return linhas.ToArray(); }

    public void Limpar()
    {
        lock (linhas) linhas.Clear();
        Estado = Situacao.Pendente;
        Msg = "";
        Ipcs = Array.Empty<string>();
    }
}

/// <summary>
/// Liga, por dentro do Lirou Hub, os plugins de que o bot depende (na ordem), usando os arquivos
/// que a verificação já baixou. Só roda quando o usuário aperta o botão.
/// </summary>
public sealed class NucleoHost : IDisposable
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<Type, object> servicos;
    private readonly string raizArquivos;
    private readonly string raizConfig;
    private volatile bool ocupado;

    public NucleoHost(IDalamudPluginInterface pi, IFramework framework, IPluginLog log,
        Dictionary<Type, object> servicos, string raizArquivos)
    {
        this.pi = pi;
        this.framework = framework;
        this.log = log;
        this.servicos = servicos;
        this.raizArquivos = raizArquivos;
        raizConfig = pi.ConfigDirectory.FullName;

        // Ordem de ligação: quem os outros usam vem primeiro.
        Itens = new[]
        {
            new NucleoItem("vnavmesh", "Puni.sh (veyn)"),
            new NucleoItem("TextAdvance", "NightmareXIV"),
            new NucleoItem("Lifestream", "NightmareXIV"),
            new NucleoItem("Questionable", "Puni.sh (ment)"),
        };
    }

    public IReadOnlyList<NucleoItem> Itens { get; }
    public bool Ocupado => ocupado;
    public bool Ligado { get; private set; }

    public void Ligar()
    {
        if (ocupado || Ligado) return;
        ocupado = true;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var m in Itens) m.Limpar();

                var falhou = false;
                foreach (var m in Itens)
                {
                    if (falhou)
                    {
                        m.Msg = "Não tentado (um módulo anterior falhou).";
                        continue;
                    }
                    await LigarUmAsync(m);
                    if (m.Estado == Situacao.Erro) falhou = true;
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
                    await framework.RunOnFrameworkThread(() => DescarregarAgora(m));
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
            var pasta = Path.Combine(raizArquivos, ModuleHost.Segura(m.Repo), ModuleHost.Segura(m.Nome), "atual");
            if (!Directory.Exists(pasta))
                throw new DirectoryNotFoundException("Arquivos não encontrados (rode a verificação primeiro): " + pasta);

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

            var tipo = tipos.FirstOrDefault(t => t.IsClass && !t.IsAbstract && typeof(IDalamudPlugin).IsAssignableFrom(t))
                ?? throw new Exception("Não achei a classe de entrada (IDalamudPlugin) dentro da DLL.");
            m.Add("Classe de entrada: " + tipo.FullName);

            await framework.RunOnFrameworkThread(() => Instanciar(m, tipo, ctx));

            m.Estado = Situacao.Ok;
            m.Msg = "Ligado.";
            m.Add("OK: plugin iniciado.");
        }
        catch (Exception ex)
        {
            m.Estado = Situacao.Erro;
            m.Msg = ex.Message;
            m.Add("ERRO: " + ex);
            log.Error(ex, "[LirouHub] Falha ao ligar " + m.Nome);
            try { await framework.RunOnFrameworkThread(() => DescarregarAgora(m)); } catch { /* ignora */ }
        }
    }

    private void Instanciar(NucleoItem m, Type tipo, ModuloContext ctx)
    {
        var ctor = tipo.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new Exception("A classe de entrada não tem construtor público.");
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        var faltam = new List<string>();
        IDalamudPluginInterface? proxy = null;

        // Peças em campos estáticos marcados com [PluginService] (jeito antigo do Dalamud).
        const BindingFlags Todos = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = tipo; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var mem in t.GetMembers(Todos))
            {
                if (mem is not (PropertyInfo or FieldInfo)) continue;
                if (!mem.GetCustomAttributes(false).Any(a => a.GetType().Name == "PluginServiceAttribute")) continue;

                var mt = mem is PropertyInfo pr ? pr.PropertyType : ((FieldInfo)mem).FieldType;
                object? valor;
                if (mt == typeof(IDalamudPluginInterface))
                {
                    proxy ??= CriarProxy(m, ctx);
                    valor = proxy;
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
                proxy ??= CriarProxy(m, ctx);
                args[i] = proxy;
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
            m.Instancia = (IDalamudPlugin)ctor.Invoke(args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private IDalamudPluginInterface CriarProxy(NucleoItem m, ModuloContext ctx)
    {
        var p = DispatchProxy.Create<IDalamudPluginInterface, PluginInterfaceProxy>();
        ((PluginInterfaceProxy)(object)p).Iniciar(pi, Path.Combine(raizConfig, "configs", ModuleHost.Segura(m.Nome)), ctx);
        return p;
    }

    /// <summary>
    /// Procura, sem executar nada do plugin, os nomes de IPC escritos no código dele
    /// (constantes de texto que começam com o nome do plugin) e as classes com "Ipc" no nome.
    /// </summary>
    private static string[] ProcurarIpc(Type[] tipos, string nome)
    {
        var res = new List<string>();
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
                }

                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                    if (f.GetRawConstantValue() is string v && v.StartsWith(nome + ".", StringComparison.OrdinalIgnoreCase))
                        res.Add($"{t.Name}.{f.Name} = {v}");
                }
            }
            catch { /* tipo difícil de inspecionar: ignora */ }

            if (res.Count >= 120) break;
        }
        return res.ToArray();
    }

    private void DescarregarAgora(NucleoItem m)
    {
        try { m.Instancia?.Dispose(); }
        catch (Exception ex) { log.Error(ex, "[LirouHub] Erro ao desligar " + m.Nome); }
        m.Instancia = null;
        m.Assembly = null;
        try { m.Contexto?.Unload(); } catch { /* ignora */ }
        m.Contexto = null;
    }

    public string Relatorio()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== Núcleo (ligado: " + Ligado + ")");
        foreach (var m in Itens)
        {
            sb.AppendLine($"   {m.Nome} [{m.Repo}]: {m.Estado}  {m.Msg}");
            foreach (var l in m.Linhas()) sb.AppendLine("      " + l);
            foreach (var i in m.Ipcs) sb.AppendLine("      IPC: " + i);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var m in Itens.Reverse()) DescarregarAgora(m);
        Ligado = false;
    }
}
