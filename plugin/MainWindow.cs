using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace LirouHub;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Verde = new(0.35f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 Vermelho = new(0.95f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Amarelo = new(0.95f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 Cinza = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 Ouro = new(1.00f, 0.82f, 0.25f, 1f);

    private readonly ModuleHost host;
    private readonly QuestTracker tracker;
    private readonly NucleoHost nucleo;
    private bool mostrarDetalhes;

    public MainWindow(ModuleHost host, QuestTracker tracker, NucleoHost nucleo) : base("Lirou Hub###LirouHubMain")
    {
        this.host = host;
        this.tracker = tracker;
        this.nucleo = nucleo;
        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private static Vector4 Cor(Situacao s) => s switch
    {
        Situacao.Ok => Verde,
        Situacao.Pulado => Cinza,
        Situacao.Erro => Vermelho,
        Situacao.Trabalhando => Amarelo,
        _ => Cinza,
    };

    public override void Draw()
    {
        tracker.Atualizar();

        ImGui.TextColored(Cinza, "Bot: ainda não conectado (Start e Stop só registram o pedido).");
        ImGui.Separator();

        DesenharQuests();

        ImGui.Separator();
        if (ImGui.Button("Copiar relatório"))
            ImGui.SetClipboardText(tracker.Relatorio() + "\n" + nucleo.Relatorio() + "\n" + host.Relatorio());
        ImGui.SameLine();
        ImGui.Checkbox("Mostrar detalhes das quests", ref mostrarDetalhes);

        if (ImGui.CollapsingHeader("Registro"))
        {
            foreach (var e in tracker.Eventos()) ImGui.TextWrapped(e);
        }

        if (ImGui.CollapsingHeader("Núcleo (experimental)"))
            DesenharNucleo();

        if (ImGui.CollapsingHeader("Arquivos dos plugins (verificação)"))
            DesenharArquivos();
    }

    private void DesenharQuests()
    {
        if (tracker.Erro.Length > 0)
        {
            ImGui.TextColored(Vermelho, tracker.Erro);
            return;
        }

        var ativas = tracker.Ativas;
        if (ativas.Count == 0)
        {
            ImGui.TextColored(Cinza, "Nenhuma quest ativa no diário.");
            return;
        }

        var msq = ativas.Where(q => q.Msq).ToList();
        var outras = ativas.Where(q => !q.Msq).ToList();

        ImGui.TextColored(Ouro, "MAIN SCENARIO QUEST");
        if (msq.Count == 0)
        {
            ImGui.TextColored(Cinza, "Nenhuma identificada.");
        }
        foreach (var q in msq) DesenharQuest(q, true);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Cinza, $"Outras quests ({outras.Count})");
        foreach (var q in outras) DesenharQuest(q, false);
    }

    private void DesenharQuest(QuestAtiva q, bool destaque)
    {
        ImGui.Spacing();
        var rodando = tracker.EmExecucao == q.Id;

        ImGui.TextColored(destaque ? Ouro : Vector4.One, q.Nome);
        if (rodando)
        {
            ImGui.SameLine();
            ImGui.TextColored(Verde, "  [marcada para rodar]");
        }
        if (mostrarDetalhes)
            ImGui.TextColored(Cinza, $"ID {q.Id}  |  icone {q.IconeEvento}  |  genero {q.Genero}");

        if (tracker.EmExecucao != null) ImGui.BeginDisabled();
        if (ImGui.Button($"Start###start{q.Id}")) tracker.Iniciar(q);
        if (tracker.EmExecucao != null) ImGui.EndDisabled();

        ImGui.SameLine();

        if (!rodando) ImGui.BeginDisabled();
        if (ImGui.Button($"Stop###stop{q.Id}")) tracker.Parar();
        if (!rodando) ImGui.EndDisabled();
    }

    private void DesenharNucleo()
    {
        ImGui.TextWrapped("Liga por dentro do Lirou Hub os plugins de que o bot precisa, usando os arquivos já baixados. " +
                          "Antes, desative os originais (vnavmesh, TextAdvance, Lifestream, Questionable) em /xlplugins.");

        var prontos = !host.Ocupado && host.Repos.All(r => r.Estado == Situacao.Ok);

        if (!nucleo.Ligado)
        {
            if (nucleo.Ocupado || !prontos) ImGui.BeginDisabled();
            if (ImGui.Button("Ligar núcleo")) nucleo.Ligar();
            if (nucleo.Ocupado || !prontos) ImGui.EndDisabled();
            if (!prontos) ImGui.TextColored(Amarelo, "Espere a verificação dos arquivos terminar (tudo OK).");
        }
        else
        {
            if (nucleo.Ocupado) ImGui.BeginDisabled();
            if (ImGui.Button("Desligar núcleo")) nucleo.Desligar();
            if (nucleo.Ocupado) ImGui.EndDisabled();
        }

        var n = 0;
        foreach (var m in nucleo.Itens)
        {
            var texto = m.Estado switch
            {
                Situacao.Ok => $"{m.Nome}: OK",
                Situacao.Erro => $"{m.Nome}: ERRO",
                Situacao.Trabalhando => $"{m.Nome}: ligando...",
                _ => $"{m.Nome}: parado",
            };
            ImGui.PushStyleColor(ImGuiCol.Text, Cor(m.Estado));
            var abrir = ImGui.CollapsingHeader($"{texto}###nuc{n}");
            ImGui.PopStyleColor();
            n++;
            if (!abrir) continue;

            if (m.Msg.Length > 0) ImGui.TextWrapped(m.Msg);
            foreach (var l in m.Linhas()) ImGui.TextWrapped(l);
            foreach (var i in m.Ipcs) ImGui.TextColored(Cinza, i);
        }
    }

    private void DesenharArquivos()
    {
        var n = 0;
        foreach (var r in host.Repos)
        {
            var itens = r.Itens();
            var feitos = itens.Count(i => i.Estado == Situacao.Ok || i.Estado == Situacao.Pulado);
            var texto = r.Estado switch
            {
                Situacao.Ok => $"{r.Nome}: OK ({feitos} plugins)",
                Situacao.Erro => $"{r.Nome}: ERRO",
                Situacao.Trabalhando => $"{r.Nome}: baixando {feitos}/{itens.Length}...",
                _ => $"{r.Nome}: aguardando",
            };

            ImGui.PushStyleColor(ImGuiCol.Text, Cor(r.Estado));
            var abrir = ImGui.CollapsingHeader($"{texto}###repo{n}");
            ImGui.PopStyleColor();
            n++;
            if (!abrir) continue;

            ImGui.TextColored(Cinza, r.Grupo);
            if (r.Msg.Length > 0) ImGui.TextWrapped(r.Msg);
            foreach (var i in itens)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Cor(i.Estado));
                ImGui.TextWrapped($"{i.Estado}  {i.Nome} {i.Versao}  {i.Msg}");
                ImGui.PopStyleColor();
            }
        }

        if (host.Ocupado) ImGui.BeginDisabled();
        if (ImGui.Button("Verificar de novo")) host.Recarregar();
        if (host.Ocupado) ImGui.EndDisabled();
    }
}
