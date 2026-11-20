using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LirouHub;

public enum EstadoModulo { Pendente, Trabalhando, Ok, Erro }

/// <summary>Um plugin que o hospedeiro baixa e liga por dentro.</summary>
public sealed class Modulo
{
    private readonly List<string> linhas = new();

    public Modulo(string nome, string repoJsonUrl, string dllEsperada)
    {
        Nome = nome;
        RepoJsonUrl = repoJsonUrl;
        DllEsperada = dllEsperada;
    }

    public string Nome { get; }
    public string RepoJsonUrl { get; }
    public string DllEsperada { get; }

    public EstadoModulo Estado { get; set; } = EstadoModulo.Pendente;
    public string Resumo { get; set; } = "";
    public string Versao { get; set; } = "";

    // Para o futuro: daqui dá para chegar nas informações do módulo.
    public Assembly? Assembly { get; internal set; }
    public IDalamudPlugin? Instancia { get; internal set; }
    internal ModuloContext? Contexto { get; set; }

    public void Add(string texto)
    {
        lock (linhas) linhas.Add($"{DateTime.Now:HH:mm:ss}  {texto}");
    }

    public string[] Linhas()
    {
        lock (linhas) return linhas.ToArray();
    }

    public void Limpar()
    {
        lock (linhas) linhas.Clear();
        Estado = EstadoModulo.Pendente;
        Resumo = "";
        Versao = "";
    }
}

/// <summary>Espaço separado onde o módulo é carregado. Peças do Dalamud vêm do hospedeiro.</summary>
internal sealed class ModuloContext : AssemblyLoadContext
{
    private readonly string pasta;
    private readonly AssemblyLoadContext hospedeiro;

    public ModuloContext(string nome, string pasta) : base("LirouHub." + nome, true)
    {
        this.pasta = pasta;
        hospedeiro = GetLoadContext(typeof(ModuloContext).Assembly)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1) O que o hospedeiro já tem (Dalamud, ImGui, Newtonsoft...) é compartilhado.
        foreach (var a in hospedeiro.Assemblies)
        {
            if (string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        // 2) O que veio dentro do zip do módulo.
        var caminho = Path.Combine(pasta, assemblyName.Name + ".dll");
        if (File.Exists(caminho)) return LoadFromAssemblyPath(caminho);

        // 3) Último recurso: pedir ao hospedeiro (que repassa ao Dalamud).
        try { return hospedeiro.LoadFromAssemblyName(assemblyName); }
        catch { return null; }
    }
}

public sealed class ModuleHost : IDisposable
{
    private static readonly HttpClient Http = CriarHttp();

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<Type, object> servicos;
    private readonly string raiz;
    private CancellationTokenSource cts = new();
    private volatile bool ocupado;

    public ModuleHost(IDalamudPluginInterface pi, IFramework framework, IPluginLog log, Dictionary<Type, object> servicos)
    {
        this.pi = pi;
        this.framework = framework;
        this.log = log;
        this.servicos = servicos;
        raiz = pi.ConfigDirectory.FullName;

        Modulos = new[]
        {
            new Modulo("FFBL",
                "https://raw.githubusercontent.com/yurammaruy/FFBL/main/repo.json",
                "FFBL.dll"),
            new Modulo("Twitch World",
                "https://raw.githubusercontent.com/yurammaruy/twitch-world-ff14/main/repo.json",
                "TwitchWorld.dll"),
        };
    }

    public IReadOnlyList<Modulo> Modulos { get; }
    public bool Ocupado => ocupado;

    private static HttpClient CriarHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LirouHub/0.0.0.2");
        return c;
    }

    public void Iniciar()
    {
        if (ocupado) return;
        ocupado = true;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var m in Modulos)
                {
                    if (token.IsCancellationRequested) break;
                    await CarregarAsync(m, token);
                }
            }
            finally { ocupado = false; }
        });
    }

    public void Recarregar()
    {
        if (ocupado) return;
        Descarregar();
        Iniciar();
    }

    private async Task CarregarAsync(Modulo m, CancellationToken ct)
    {
        m.Limpar();
        m.Estado = EstadoModulo.Trabalhando;
        try
        {
            // 1) Ler o repo.json do projeto
            m.Add("Lendo " + m.RepoJsonUrl);
            var json = await Http.GetStringAsync(m.RepoJsonUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            if (el.ValueKind == JsonValueKind.Array)
            {
                if (el.GetArrayLength() == 0) throw new Exception("O repo.json está vazio.");
                el = el[0];
            }

            var internalName = Texto(el, "InternalName");
            var versao = Texto(el, "AssemblyVersion");
            var link = Texto(el, "DownloadLinkInstall");
            if (link.Length == 0) throw new Exception("O repo.json não tem o campo DownloadLinkInstall.");
            m.Versao = versao;
            m.Add($"Versão publicada: {versao}");

            // 2) Baixar o zip
            var pasta = Path.Combine(raiz, "modulos", Segura(m.Nome));
            Directory.CreateDirectory(pasta);
            var zip = Path.Combine(pasta, "ultimo.zip");
            try
            {
                m.Add("Baixando " + link);
                var bytes = await Http.GetByteArrayAsync(link, ct);
                await File.WriteAllBytesAsync(zip, bytes, ct);
                m.Add($"Baixado: {bytes.Length / 1024} KB");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(zip))
            {
                m.Add("Não consegui baixar (" + ex.Message + "). Uso a cópia guardada da última vez.");
            }

            // 3) Extrair em pasta nova (as antigas são apagadas se possível)
            foreach (var velha in Directory.GetDirectories(pasta, "v_*"))
            {
                try { Directory.Delete(velha, true); } catch { /* em uso, ignora */ }
            }
            var destino = Path.Combine(pasta, "v_" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            ZipFile.ExtractToDirectory(zip, destino, true);
            m.Add("Extraído em " + destino);

            // 4) Achar e carregar a DLL
            var dll = AcharDll(destino, m.DllEsperada, internalName);
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
                throw new Exception("O módulo pede peças que o hospedeiro não tem (veja os detalhes).");
            }

            var tipo = tipos.FirstOrDefault(t => t.IsClass && !t.IsAbstract && typeof(IDalamudPlugin).IsAssignableFrom(t))
                ?? throw new Exception("Não achei a classe de entrada (IDalamudPlugin) dentro da DLL.");
            m.Add("Classe de entrada: " + tipo.FullName);

            // 5) Ligar na thread do jogo
            await framework.RunOnFrameworkThread(() => Ligar(m, tipo, ctx));

            m.Estado = EstadoModulo.Ok;
            m.Resumo = "Lido e iniciado.";
            m.Add("OK: módulo iniciado.");
        }
        catch (OperationCanceledException)
        {
            m.Estado = EstadoModulo.Erro;
            m.Resumo = "Cancelado.";
        }
        catch (Exception ex)
        {
            m.Estado = EstadoModulo.Erro;
            m.Resumo = ex.Message;
            m.Add("ERRO: " + ex);
            log.Error(ex, "[LirouHub] Falha ao carregar " + m.Nome);
        }
    }

    private void Ligar(Modulo m, Type tipo, ModuloContext ctx)
    {
        var ctor = tipo.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new Exception("A classe de entrada não tem construtor público.");
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        var faltam = new List<string>();
        IDalamudPluginInterface? proxy = null;

        // Jeito antigo do Dalamud: peças em campos estáticos marcados com [PluginService].
        // O Dalamud só preenche isso para plugins instalados por ele, então preenchemos aqui.
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
                    m.Add($"Peça entregue (estática): {mt.Name} em {mem.Name}");
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
                m.Add("Peça entregue: IDalamudPluginInterface (própria do módulo)");
            }
            else if (servicos.TryGetValue(pt, out var s))
            {
                args[i] = s;
                m.Add("Peça entregue: " + pt.Name);
            }
            else
            {
                faltam.Add(pt.FullName ?? pt.Name);
            }
        }

        if (faltam.Count > 0)
            throw new Exception("Faltou entregar estas peças ao módulo: " + string.Join(", ", faltam));

        try
        {
            m.Instancia = (IDalamudPlugin)ctor.Invoke(args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private IDalamudPluginInterface CriarProxy(Modulo m, ModuloContext ctx)
    {
        var p = DispatchProxy.Create<IDalamudPluginInterface, PluginInterfaceProxy>();
        var px = (PluginInterfaceProxy)(object)p;
        px.Iniciar(pi, Path.Combine(raiz, "configs", Segura(m.Nome)), ctx);
        return p;
    }

    private static string AcharDll(string destino, string esperada, string internalName)
    {
        var nomes = new[] { esperada, internalName.Length > 0 ? internalName + ".dll" : "" }
            .Where(n => n.Length > 0).Distinct();
        foreach (var n in nomes)
        {
            var f = Directory.GetFiles(destino, n, SearchOption.AllDirectories).FirstOrDefault();
            if (f != null) return f;
        }
        var achadas = string.Join(", ", Directory.GetFiles(destino, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName));
        throw new FileNotFoundException($"Não achei {esperada} dentro do zip. DLLs encontradas: {achadas}");
    }

    private static string Texto(JsonElement el, string nome)
        => el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Segura(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    public void Descarregar()
    {
        foreach (var m in Modulos)
        {
            try { m.Instancia?.Dispose(); }
            catch (Exception ex) { log.Error(ex, "[LirouHub] Erro ao desligar " + m.Nome); }
            m.Instancia = null;
            m.Assembly = null;
            try { m.Contexto?.Unload(); } catch { /* ignora */ }
            m.Contexto = null;
            m.Limpar();
        }
    }

    public string Relatorio()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Lirou Hub {typeof(ModuleHost).Assembly.GetName().Version} - relatório de {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        foreach (var m in Modulos)
        {
            sb.AppendLine();
            sb.AppendLine($"== {m.Nome}: {m.Estado} {(m.Versao.Length > 0 ? "(versão " + m.Versao + ")" : "")}");
            if (m.Resumo.Length > 0) sb.AppendLine("Resumo: " + m.Resumo);
            foreach (var l in m.Linhas()) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { /* ignora */ }
        Descarregar();
    }
}
