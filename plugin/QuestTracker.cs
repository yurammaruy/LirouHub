using System.Diagnostics;
using System.Text;
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
/// Descobre quais quests o personagem tem ativas no diário e guarda qual está marcada para rodar.
/// Por enquanto Start/Stop só registram o pedido: a ligação com o bot vem na próxima etapa.
/// </summary>
public sealed unsafe class QuestTracker
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly List<(ushort Id, string Nome, bool Msq, uint Icone, uint Genero)> catalogo = new();
    private readonly List<string> eventos = new();
    private readonly Stopwatch relogio = Stopwatch.StartNew();
    private bool catalogoPronto;
    private long ultima = -10000;

    public QuestTracker(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    public IReadOnlyList<QuestAtiva> Ativas { get; private set; } = Array.Empty<QuestAtiva>();
    public string Erro { get; private set; } = "";
    public ushort? EmExecucao { get; private set; }

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

            if (EmExecucao is ushort rodando && lista.All(q => q.Id != rodando))
            {
                Evento($"A quest {rodando} saiu do diário (concluída ou abandonada). Parado.");
                EmExecucao = null;
            }
        }
        catch (Exception ex)
        {
            Erro = ex.Message;
            log.Error(ex, "[LirouHub] Falha ao ler as quests");
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

    public void Iniciar(QuestAtiva q)
    {
        if (EmExecucao != null) return;
        EmExecucao = q.Id;
        Evento($"START pedido: {q.Nome} (ID {q.Id}). Bot ainda não conectado, só registrei o pedido.");
    }

    public void Parar()
    {
        if (EmExecucao is ushort id)
            Evento($"STOP pedido (ID {id}).");
        EmExecucao = null;
    }

    public string Relatorio()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== Quests ativas");
        if (Erro.Length > 0) sb.AppendLine("Erro: " + Erro);
        sb.AppendLine($"Catálogo: {catalogo.Count} quests. Em execução: {(EmExecucao?.ToString() ?? "nenhuma")}");
        foreach (var q in Ativas)
            sb.AppendLine($"   - {q.Id} | {q.Nome} | MSQ={q.Msq} | icone={q.IconeEvento} | genero={q.Genero}");
        foreach (var e in Eventos()) sb.AppendLine("   " + e);
        return sb.ToString();
    }
}
