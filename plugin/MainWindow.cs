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
    private bool ocultarJanelas = true;

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
        if (ocultarJanelas) nucleo.OcultarJanelas();

        if (tracker.BotPronto)
        {
            var atual = tracker.QuestionableRodando
                ? $"fazendo a quest {(tracker.QuestIdAtual.Length > 0 ? tracker.QuestIdAtual : "...")}"
                : "parado";
            ImGui.TextColored(Verde, $"Bot: Questionable ligado por dentro do Hub, {atual}.");
        }
        else
        {
            ImGui.TextColored(Amarelo, "Bot: desligado. Abra o Núcleo e clique em Ligar núcleo para usar o Start.");
        }
        ImGui.Separator();

        DesenharQuests();

        ImGui.Separator();
        if (ImGui.Button("Copiar relatório"))
            ImGui.SetClipboardText(tracker.Relatorio() + "\n" + nucleo.Relatorio() + "\n" + host.Relatorio());
        ImGui.SameLine();
        ImGui.Checkbox("Mostrar detalhes das quests", ref mostrarDetalhes);

        if (ImGui.CollapsingHeader("Movimento (diagnóstico)"))
        {
            foreach (var l in tracker.Sonda()) ImGui.TextWrapped(l);
            ImGui.TextColored(Amarelo, "Mensagens do Questionable no chat:");
            foreach (var l in tracker.MensagensDoBot()) ImGui.TextWrapped(l);
        }

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

        var travarStart = tracker.EmExecucao != null || !tracker.BotPronto;
        if (travarStart) ImGui.BeginDisabled();
        if (ImGui.Button($"Start###start{q.Id}")) tracker.Iniciar(q);
        if (travarStart) ImGui.EndDisabled();

        ImGui.SameLine();

        if (!rodando) ImGui.BeginDisabled();
        if (ImGui.Button($"Stop###stop{q.Id}")) tracker.Parar();
        if (!rodando) ImGui.EndDisabled();
    }

    private static Vector4 CorNivel(Nivel n) => n switch
    {
        Nivel.Erro => Vermelho,
        Nivel.Aviso => Amarelo,
        _ => Verde,
    };

    private void DesenharNucleo()
    {
        ImGui.TextWrapped("Liga por dentro do Lirou Hub os plugins de que o bot precisa, usando os arquivos já baixados. " +
                          "Antes de ligar, o Hub confere tudo e mostra os problemas abaixo.");

        var prontos = !host.Ocupado && host.Repos.All(r => r.Estado == Situacao.Ok);

        // A conferência roda sozinha assim que os arquivos estiverem todos OK.
        if (prontos && !nucleo.VerificacaoFeita && !nucleo.Ocupado && !nucleo.Ligado)
            nucleo.Verificar();

        if (!nucleo.Ligado)
        {
            if (nucleo.Ocupado || !nucleo.PodeLigar) ImGui.BeginDisabled();
            if (ImGui.Button("Ligar núcleo###nucligar")) nucleo.Ligar();
            if (nucleo.Ocupado || !nucleo.PodeLigar) ImGui.EndDisabled();

            ImGui.SameLine();
            if (nucleo.Ocupado || !prontos) ImGui.BeginDisabled();
            if (ImGui.Button("Conferir de novo###nucconferir")) nucleo.Verificar();
            if (nucleo.Ocupado || !prontos) ImGui.EndDisabled();

            if (!prontos) ImGui.TextColored(Amarelo, "Espere a verificação dos arquivos terminar (tudo OK).");
            else if (nucleo.Ocupado) ImGui.TextColored(Amarelo, "Conferindo...");
            else if (nucleo.VerificacaoFeita && !nucleo.PodeLigar)
                ImGui.TextColored(Vermelho, "Há problemas abaixo. Corrija e clique em Conferir de novo.");
            else if (nucleo.PodeLigar) ImGui.TextColored(Verde, "Tudo conferido: pode ligar.");
        }
        else
        {
            if (nucleo.Ocupado) ImGui.BeginDisabled();
            if (ImGui.Button("Desligar núcleo###nucdesligar")) nucleo.Desligar();
            if (nucleo.Ocupado) ImGui.EndDisabled();
        }

        ImGui.Checkbox("Ocultar as janelas dos plugins internos###nucocultar", ref ocultarJanelas);
        if (nucleo.Ligado || nucleo.Itens.Any(i => i.Instancia != null))
            ImGui.TextColored(Cinza, $"Sistemas de janelas encontrados: {nucleo.SistemasDeJanelas}. Janelas fechadas até agora: {nucleo.JanelasOcultadas}.");

        var n = 0;
        foreach (var m in nucleo.Itens)
        {
            Vector4 cor;
            string texto;
            if (m.Estado == Situacao.Ok) { cor = Verde; texto = $"{m.Nome}: ligado"; }
            else if (m.Estado == Situacao.Erro) { cor = Vermelho; texto = $"{m.Nome}: ERRO ao ligar"; }
            else if (m.Estado == Situacao.Trabalhando) { cor = Amarelo; texto = $"{m.Nome}: ligando..."; }
            else if (!nucleo.VerificacaoFeita) { cor = Cinza; texto = $"{m.Nome}: aguardando conferência"; }
            else if (m.TemErro) { cor = Vermelho; texto = $"{m.Nome}: com problema"; }
            else if (m.TemAviso) { cor = Amarelo; texto = $"{m.Nome}: pronto (com avisos)"; }
            else { cor = Verde; texto = $"{m.Nome}: pronto"; }

            ImGui.PushStyleColor(ImGuiCol.Text, cor);
            var abrir = ImGui.CollapsingHeader($"{texto}###nuc{n}");
            ImGui.PopStyleColor();
            n++;
            if (!abrir) continue;

            foreach (var a in m.Achados())
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CorNivel(a.Nivel));
                ImGui.TextWrapped($"[{a.Nivel}] {a.Texto}");
                ImGui.PopStyleColor();
            }
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
        if (ImGui.Button("Verificar de novo###arqverificar")) host.Recarregar();
        if (host.Ocupado) ImGui.EndDisabled();
    }
}
