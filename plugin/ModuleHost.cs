using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LirouHub;

public enum Situacao { Pendente, Trabalhando, Ok, Erro, Pulado }

/// <summary>Um plugin listado em um repositório.</summary>
public sealed class Item
{
    public string Nome = "";
    public string InternalName = "";
    public string Versao = "";
    public string Link = "";
    public Situacao Estado = Situacao.Pendente;
    public string Msg = "";
    public long Bytes;
}

/// <summary>Uma lista de plugins (um pluginmaster.json / repo.json).</summary>
public sealed class Repo
{
    private readonly object trava = new();
    private readonly List<Item> itens = new();
    private readonly List<string> linhas = new();

    public Repo(string nome, string url, string grupo)
    {
        Nome = nome;
        Url = url;
        Grupo = grupo;
    }

    public string Nome { get; }
    public string Url { get; }
    public string Grupo { get; }
    public Situacao Estado { get; set; } = Situacao.Pendente;
    public string Msg { get; set; } = "";

    public Item[] Itens() { lock (trava) return itens.ToArray(); }
    public void AddItem(Item i) { lock (trava) itens.Add(i); }
    public void Log(string t) { lock (trava) linhas.Add($"{DateTime.Now:HH:mm:ss}  {t}"); }
    public string[] Linhas() { lock (trava) return linhas.ToArray(); }

    public void Limpar()
    {
        lock (trava) { itens.Clear(); linhas.Clear(); }
        Estado = Situacao.Pendente;
        Msg = "";
    }
}

/// <summary>
/// Baixa os plugins de cada repositório e confere se os arquivos estão certos.
/// NÃO executa nenhum deles: só guarda os arquivos numa pasta e confirma que estão íntegros.
/// </summary>
public sealed class ModuleHost : IDisposable
{
    private static readonly HttpClient Http = CriarHttp();

    private readonly IPluginLog log;
    private readonly string raiz;
    private CancellationTokenSource cts = new();
    private volatile bool ocupado;

    public ModuleHost(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        raiz = Path.Combine(pi.ConfigDirectory.FullName, "arquivos");

        Repos = new[]
        {
            new Repo("Puni.sh (ment)", "https://love.puni.sh/ment.json", "Obrigatório + Combate"),
            new Repo("Puni.sh (veyn)", "https://puni.sh/api/repository/veyn", "Obrigatório + Combate"),
            new Repo("NightmareXIV", "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json", "Obrigatório"),
            new Repo("Combat Reborn", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json", "Combate"),
            new Repo("Puni.sh (erdelf)", "https://puni.sh/api/repository/erdelf", "Recomendado"),
        };
    }

    public IReadOnlyList<Repo> Repos { get; }
    public bool Ocupado => ocupado;
    public string PastaArquivos => raiz;

    private static HttpClient CriarHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LirouHub/0.0.0.12");
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
                var vistos = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in Repos)
                {
                    if (token.IsCancellationRequested) break;
                    await ProcessarRepoAsync(r, vistos, token);
                }
            }
            finally { ocupado = false; }
        });
    }

    public void Recarregar()
    {
        if (ocupado) return;
        Iniciar();
    }

    private async Task ProcessarRepoAsync(Repo r, ConcurrentDictionary<string, string> vistos, CancellationToken ct)
    {
        r.Limpar();
        r.Estado = Situacao.Trabalhando;
        try
        {
            r.Log("Lendo " + r.Url);
            var json = await Http.GetStringAsync(r.Url, ct);

            var testes = 0;
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var lista = new List<JsonElement>();
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in root.EnumerateArray()) lista.Add(e);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    lista.Add(root);
                }
                else
                {
                    throw new Exception("Formato do JSON não reconhecido (esperava uma lista de plugins).");
                }

                foreach (var el in lista)
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    if (el.TryGetProperty("IsTestingExclusive", out var t) && t.ValueKind == JsonValueKind.True)
                    {
                        testes++;
                        continue;
                    }

                    var nome = Texto(el, "Name");
                    var interno = Texto(el, "InternalName");
                    if (interno.Length == 0) interno = nome;
                    var link = Texto(el, "DownloadLinkInstall");
                    if (link.Length == 0) link = Texto(el, "DownloadLinkUpdate");

                    r.AddItem(new Item
                    {
                        Nome = nome.Length > 0 ? nome : interno,
                        InternalName = interno,
                        Versao = Texto(el, "AssemblyVersion"),
                        Link = link,
                    });
                }
            }

            var itens = r.Itens();
            if (itens.Length == 0) throw new Exception("A lista não tem nenhum plugin.");
            r.Log($"{itens.Length} plugins na lista ({testes} só de teste ignorados).");

            // Plugins que aparecem em mais de uma lista só são baixados uma vez.
            foreach (var i in itens)
            {
                if (i.InternalName.Length == 0)
                {
                    i.Estado = Situacao.Erro;
                    i.Msg = "Plugin sem nome na lista.";
                    continue;
                }
                if (!vistos.TryAdd(i.InternalName, r.Nome))
                {
                    vistos.TryGetValue(i.InternalName, out var origem);
                    i.Estado = Situacao.Pulado;
                    i.Msg = $"Já baixado pela lista {origem}.";
                }
            }

            using var sem = new SemaphoreSlim(4);
            var tarefas = itens.Where(i => i.Estado == Situacao.Pendente).Select(async i =>
            {
                await sem.WaitAsync(ct);
                try { await ProcessarItemAsync(r, i, ct); }
                finally { sem.Release(); }
            }).ToArray();
            await Task.WhenAll(tarefas);

            var erros = itens.Count(i => i.Estado == Situacao.Erro);
            if (erros > 0)
            {
                r.Estado = Situacao.Erro;
                r.Msg = $"{erros} de {itens.Length} plugins com erro.";
            }
            else
            {
                r.Estado = Situacao.Ok;
                r.Msg = $"{itens.Length} plugins conferidos.";
            }
        }
        catch (OperationCanceledException)
        {
            r.Estado = Situacao.Erro;
            r.Msg = "Cancelado.";
        }
        catch (Exception ex)
        {
            r.Estado = Situacao.Erro;
            r.Msg = ex.Message;
            r.Log("ERRO: " + ex);
            log.Error(ex, "[LirouHub] Falha na lista " + r.Nome);
        }
    }

    private async Task ProcessarItemAsync(Repo r, Item item, CancellationToken ct)
    {
        item.Estado = Situacao.Trabalhando;
        try
        {
            if (item.Link.Length == 0) throw new Exception("A lista não traz o link de download deste plugin.");

            var pasta = Path.Combine(raiz, Segura(r.Nome), Segura(item.InternalName));
            Directory.CreateDirectory(pasta);
            var zip = Path.Combine(pasta, "ultimo.zip");
            var tmp = zip + ".baixando";
            var aviso = "";

            try
            {
                using var resp = await Http.GetAsync(item.Link, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(tmp))
                    await resp.Content.CopyToAsync(fs, ct);
                File.Move(tmp, zip, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(zip))
            {
                aviso = $" (download falhou: {ex.Message}; usei a cópia guardada)";
            }

            item.Bytes = new FileInfo(zip).Length;

            // Confere se o zip abre e tem a DLL do plugin dentro.
            int total;
            string? dllPrincipal;
            using (var za = ZipFile.OpenRead(zip))
            {
                total = za.Entries.Count;
                var dlls = za.Entries
                    .Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Name)
                    .ToList();
                if (dlls.Count == 0) throw new Exception("O zip não tem nenhuma DLL.");
                dllPrincipal = dlls.FirstOrDefault(n =>
                    string.Equals(n, item.InternalName + ".dll", StringComparison.OrdinalIgnoreCase));
                if (dllPrincipal == null)
                    aviso += $" (não achei {item.InternalName}.dll; DLLs: {string.Join(", ", dlls.Take(5))})";
            }

            // Guarda os arquivos numa pasta NOVA a cada verificação. Nunca apagamos nem sobrescrevemos a pasta
            // anterior antes de extrair: o Windows trava DLLs de plugins já carregados nesta sessão do jogo.
            var nova = Path.Combine(pasta, "v_" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
            ZipFile.ExtractToDirectory(zip, nova, true);
            if (Directory.GetFiles(nova, "*.dll", SearchOption.AllDirectories).Length == 0)
                throw new Exception("Extraí o zip, mas não encontrei DLLs na pasta.");

            // Limpeza das versões antigas: só o que o Windows deixar apagar; o resto fica para a próxima vez.
            foreach (var velha in Directory.GetDirectories(pasta).Where(d =>
                         d != nova && (Path.GetFileName(d).StartsWith("v_") || Path.GetFileName(d) == "atual")))
            {
                try { Directory.Delete(velha, true); }
                catch { /* em uso: ignora */ }
            }

            item.Estado = Situacao.Ok;
            item.Msg = $"{total} arquivos, {item.Bytes / 1024} KB{aviso}";
        }
        catch (OperationCanceledException)
        {
            item.Estado = Situacao.Erro;
            item.Msg = "Cancelado.";
        }
        catch (Exception ex)
        {
            item.Estado = Situacao.Erro;
            item.Msg = ex.Message;
            r.Log($"ERRO em {item.InternalName}: {ex}");
        }
    }

    private static string Texto(JsonElement el, string nome)
        => el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>A pasta de arquivos mais recente de um plugin (v_data-hora), ou a antiga "atual".</summary>
    internal static string? PastaAtual(string pastaDoPlugin)
    {
        if (!Directory.Exists(pastaDoPlugin)) return null;
        var nova = Directory.GetDirectories(pastaDoPlugin, "v_*")
            .OrderBy(d => d, StringComparer.Ordinal)
            .LastOrDefault();
        if (nova != null) return nova;
        var legado = Path.Combine(pastaDoPlugin, "atual");
        return Directory.Exists(legado) ? legado : null;
    }

    internal static string Segura(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    public string Relatorio()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Lirou Hub {typeof(ModuleHost).Assembly.GetName().Version} - relatório de {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine("Pasta dos arquivos: " + raiz);
        foreach (var r in Repos)
        {
            sb.AppendLine();
            sb.AppendLine($"== {r.Nome} [{r.Grupo}]: {r.Estado}  {r.Msg}");
            sb.AppendLine(r.Url);
            foreach (var l in r.Linhas()) sb.AppendLine("   " + l);
            foreach (var i in r.Itens())
                sb.AppendLine($"   - {i.InternalName} {i.Versao}: {i.Estado}  {i.Msg}");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { /* ignora */ }
    }
}
