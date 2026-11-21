using System.Diagnostics;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using QuestSheet = Lumina.Excel.Sheets.Quest;

namespace LirouHub;

public sealed class QuestAtiva
{
    public ushort Id;
    public string Nome = "";
    public bool Msq;
    public uint IconeEvento;
    public uint Genero;
}

/// <summary>
/// Descobre quais quests o personagem tem ativas no diário e comanda o Questionable (que roda por dentro
/// do Lirou Hub) por IPC: Start, Stop e acompanhamento do que ele está fazendo.
/// </summary>
public sealed unsafe class QuestTracker : IDisposable
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pi;
    private readonly NucleoHost nucleo;
    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IObjectTable objetos;
    private readonly List<string> sonda = new();
    private readonly List<string> chatQst = new();
    private long ultimaSonda = -10000;
    private long ultimoPonto = -10000;
    private readonly List<string> historicoMalha = new();
    private System.Numerics.Vector3 posAnterior;
    private long moveuEm;
    private readonly List<(ushort Id, string Nome, bool Msq, uint Icone, uint Genero)> catalogo = new();
    private readonly List<string> eventos = new();
    private readonly Stopwatch relogio = Stopwatch.StartNew();
    private bool catalogoPronto;
    private long ultima = -10000;
    private long iniciouEm;

    public QuestTracker(IDataManager data, IPluginLog log, IDalamudPluginInterface pi, NucleoHost nucleo,
        IChatGui chat, IClientState clientState, IObjectTable objetos)
    {
        this.chat = chat;
        this.clientState = clientState;
        this.objetos = objetos;
        chat.ChatMessage += m => { if (!descartado) AoChat(m.Message.ToString()); };
        this.data = data;
        this.log = log;
        this.pi = pi;
        this.nucleo = nucleo;
    }

    public IReadOnlyList<QuestAtiva> Ativas { get; private set; } = Array.Empty<QuestAtiva>();
    public string Erro { get; private set; } = "";
    public ushort? EmExecucao { get; private set; }

    /// <summary>O Questionable está ligado por dentro do Hub e pronto para receber comandos.</summary>
    public bool BotPronto => nucleo.QuestionablePronto;
    public bool QuestionableRodando { get; private set; }
    public string QuestIdAtual { get; private set; } = "";

    private volatile bool descartado;
    public void Dispose() => descartado = true;

    private void AoChat(string t)
    {
        try
        {
            if (t.Contains("[QST") || t.Contains("Questionable"))
                lock (chatQst)
                {
                    chatQst.Add($"{DateTime.Now:HH:mm:ss}  {t}");
                    if (chatQst.Count > 25) chatQst.RemoveAt(0);
                }
        }
        catch { }
    }

    public string[] MensagensDoBot() { lock (chatQst) return chatQst.ToArray(); }
    public string[] Sonda() { lock (sonda) return sonda.ToArray(); }

    private string Pergunta<T>(string nome)
    {
        try { return pi.GetIpcSubscriber<T>(nome).InvokeFunc()?.ToString() ?? "(vazio)"; }
        catch (Exception ex) { return "sem resposta (" + ex.GetType().Name + ")"; }
    }

    /// <summary>Pergunta aos plugins internos como estão (só leitura) para vermos por que o bot anda ou não.</summary>
    private void FazerSonda()
    {
        if (relogio.ElapsedMilliseconds - ultimaSonda < 1000) return;
        ultimaSonda = relogio.ElapsedMilliseconds;
        var l = new List<string>();
        try
        {
            var jogador = objetos.LocalPlayer;
            if (jogador != null)
            {
                var pos = jogador.Position;
                if (System.Numerics.Vector3.Distance(pos, posAnterior) > 0.2f) moveuEm = relogio.ElapsedMilliseconds;
                posAnterior = pos;
                var parado = (relogio.ElapsedMilliseconds - moveuEm) / 1000;
                l.Add($"Personagem: zona {clientState.TerritoryType}, posição {pos.X:0.0}, {pos.Y:0.0}, {pos.Z:0.0}. Parado há {parado}s.");
            }
        }
        catch (Exception ex) { l.Add("Personagem: " + ex.Message); }

        try
        {
            var pr = pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress").InvokeFunc();
            if (relogio.ElapsedMilliseconds - ultimoPonto > 5000)
            {
                ultimoPonto = relogio.ElapsedMilliseconds;
                historicoMalha.Add($"{DateTime.Now:HH:mm:ss}={pr:0.00}");
                if (historicoMalha.Count > 8) historicoMalha.RemoveAt(0);
            }
        }
        catch { }
        l.Add("Malha (progresso a cada 5s): " + string.Join("  ", historicoMalha));
        l.Add("vnavmesh: pronto=" + Pergunta<bool>("vnavmesh.Nav.IsReady")
              + ", construindo=" + Pergunta<float>("vnavmesh.Nav.BuildProgress")
              + ", andando=" + Pergunta<bool>("vnavmesh.Path.IsRunning")
              + ", calculando=" + Pergunta<bool>("vnavmesh.SimpleMove.PathfindInProgress"));
        l.Add("TextAdvance: controle externo=" + Pergunta<bool>("TextAdvance.IsInExternalControl")
              + ", ocupado=" + Pergunta<bool>("TextAdvance.IsBusy"));
        l.Add("Lifestream: ocupado=" + Pergunta<bool>("Lifestream.IsBusy"));
        l.Add("Nomes com prefixo do Hub (ECommons): LirouHub.IsInExternalControl=" + Pergunta<bool>("LirouHub.IsInExternalControl")
              + ", LirouHub.IsBusy=" + Pergunta<bool>("LirouHub.IsBusy"));
        l.Add("Questionable: rodando=" + Pergunta<bool>("Questionable.IsRunning")
              + ", quest=" + Pergunta<string>("Questionable.GetCurrentQuestId"));
        try
        {
            var passo = pi.GetIpcSubscriber<object>("Questionable.GetCurrentStepData").InvokeFunc();
            l.Add("Passo atual: " + (passo == null ? "(nenhum)" : Newtonsoft.Json.JsonConvert.SerializeObject(passo)));
        }
        catch (Exception ex) { l.Add("Passo atual: sem resposta (" + ex.GetType().Name + ": " + ex.Message + ")"); }
        lock (sonda) { sonda.Clear(); sonda.AddRange(l); }
    }

    public string[] Eventos() { lock (eventos) return eventos.ToArray(); }

    private void Evento(string t)
    {
        lock (eventos)
        {
            eventos.Add($"{DateTime.Now:HH:mm:ss}  {t}");
            if (eventos.Count > 30) eventos.RemoveAt(0);
        }
    }

    /// <summary>Chamado a cada quadro de desenho (thread do jogo); só relê a cada 1 segundo.</summary>
    public void Atualizar()
    {
        if (relogio.ElapsedMilliseconds - ultima < 1000) return;
        ultima = relogio.ElapsedMilliseconds;

        try
        {
            if (!catalogoPronto) Montar();

            var qm = QuestManager.Instance();
            if (qm == null)
            {
                Erro = "O jogo ainda não liberou as quests (personagem carregando?).";
                Ativas = Array.Empty<QuestAtiva>();
                return;
            }

            var lista = new List<QuestAtiva>();
            foreach (var c in catalogo)
            {
                if (qm->IsQuestAccepted(c.Id))
                    lista.Add(new QuestAtiva { Id = c.Id, Nome = c.Nome, Msq = c.Msq, IconeEvento = c.Icone, Genero = c.Genero });
            }

            lista.Sort((a, b) =>
            {
                if (a.Msq != b.Msq) return a.Msq ? -1 : 1;
                return string.Compare(a.Nome, b.Nome, StringComparison.OrdinalIgnoreCase);
            });

            Ativas = lista;
            Erro = "";

            AtualizarBot(lista);
        }
        catch (Exception ex)
        {
            Erro = ex.Message;
            log.Error(ex, "[LirouHub] Falha ao ler as quests");
        }
    }

    private void AtualizarBot(List<QuestAtiva> lista)
    {
        if (!BotPronto)
        {
            QuestionableRodando = false;
            QuestIdAtual = "";
            return;
        }

        FazerSonda();

        try { QuestionableRodando = pi.GetIpcSubscriber<bool>("Questionable.IsRunning").InvokeFunc(); }
        catch { QuestionableRodando = false; }

        try { QuestIdAtual = pi.GetIpcSubscriber<string>("Questionable.GetCurrentQuestId").InvokeFunc() ?? ""; }
        catch { QuestIdAtual = ""; }

        if (EmExecucao is ushort rodando)
        {
            if (lista.All(q => q.Id != rodando))
            {
                Evento($"A quest {rodando} saiu do diário (concluída ou abandonada). Parado.");
                EmExecucao = null;
            }
            else if (!QuestionableRodando && relogio.ElapsedMilliseconds - iniciouEm > 8000)
            {
                Evento($"O Questionable parou (quest {rodando}).");
                EmExecucao = null;
            }
        }
    }

    private void Montar()
    {
        var sheet = data.GetExcelSheet<QuestSheet>();
        foreach (var row in sheet)
        {
            // Linhas de quest começam em 65536; o diário usa só o número menos 65536.
            if (row.RowId < 65536 || row.RowId > 131071) continue;
            var nome = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(nome)) continue;
            catalogo.Add(((ushort)(row.RowId - 65536), nome, row.EventIconType.RowId == 3,
                row.EventIconType.RowId, row.JournalGenre.RowId));
        }
        catalogoPronto = true;
        Evento($"Catálogo de quests carregado: {catalogo.Count} quests.");
    }

    /// <summary>Manda o Questionable fazer a quest. A Main Scenario segue a cadeia; as outras fazem só a quest.</summary>
    public void Iniciar(QuestAtiva q)
    {
        if (EmExecucao != null) return;
        if (!BotPronto)
        {
            Evento("Start ignorado: ligue o núcleo (o Questionable precisa estar ligado).");
            return;
        }

        var nome = q.Msq ? "Questionable.StartQuest" : "Questionable.StartSingleQuest";
        try
        {
            var ok = pi.GetIpcSubscriber<string, bool>(nome).InvokeFunc(q.Id.ToString());
            if (ok)
            {
                EmExecucao = q.Id;
                iniciouEm = relogio.ElapsedMilliseconds;
                Evento($"START: {q.Nome} (ID {q.Id}) via {nome}. O Questionable aceitou.");
            }
            else
            {
                Evento($"START recusado pelo Questionable: {q.Nome} (ID {q.Id}) via {nome}. (devolveu falso)");
            }
        }
        catch (Exception ex)
        {
            Evento($"START falhou ({nome}): {ex.Message}");
            log.Error(ex, "[LirouHub] Falha no Start");
        }
    }

    public void Parar()
    {
        if (BotPronto)
        {
            try
            {
                var ok = pi.GetIpcSubscriber<string, bool>("Questionable.Stop").InvokeFunc("Lirou Hub");
                Evento($"STOP enviado ao Questionable (resposta: {ok}).");
            }
            catch (Exception ex)
            {
                Evento("STOP falhou: " + ex.Message);
                log.Error(ex, "[LirouHub] Falha no Stop");
            }
        }
        EmExecucao = null;
    }

    public string Relatorio()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== Quests ativas");
        if (Erro.Length > 0) sb.AppendLine("Erro: " + Erro);
        sb.AppendLine($"Catálogo: {catalogo.Count} quests. Em execução: {(EmExecucao?.ToString() ?? "nenhuma")}");
        sb.AppendLine($"Bot pronto: {BotPronto}. Questionable rodando: {QuestionableRodando}. Quest atual no Questionable: '{QuestIdAtual}'");
        foreach (var q in Ativas)
            sb.AppendLine($"   - {q.Id} | {q.Nome} | MSQ={q.Msq} | icone={q.IconeEvento} | genero={q.Genero}");
        foreach (var e in Eventos()) sb.AppendLine("   " + e);
        sb.AppendLine("== Sonda (movimento)");
        foreach (var e in Sonda()) sb.AppendLine("   " + e);
        sb.AppendLine("== Mensagens do Questionable no chat");
        foreach (var e in MensagensDoBot()) sb.AppendLine("   " + e);
        return sb.ToString();
    }
}
