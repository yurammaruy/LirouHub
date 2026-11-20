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

    private readonly ModuleHost host;

    public MainWindow(ModuleHost host) : base("Lirou Hub###LirouHubMain")
    {
        this.host = host;
        Size = new Vector2(560, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("O Lirou Hub busca o FFBL e o Twitch World nos repositórios, liga os dois por dentro e mostra se deu certo.");
        ImGui.Separator();

        var haErro = false;
        foreach (var m in host.Modulos)
        {
            switch (m.Estado)
            {
                case EstadoModulo.Ok:
                    ImGui.TextColored(Verde, $"{m.Nome}: OK  {(m.Versao.Length > 0 ? "(versão " + m.Versao + ")" : "")}");
                    break;
                case EstadoModulo.Erro:
                    haErro = true;
                    ImGui.TextColored(Vermelho, $"{m.Nome}: ERRO");
                    ImGui.TextWrapped(m.Resumo);
                    break;
                case EstadoModulo.Trabalhando:
                    ImGui.TextColored(Amarelo, $"{m.Nome}: carregando...");
                    break;
                default:
                    ImGui.TextColored(Cinza, $"{m.Nome}: aguardando");
                    break;
            }
        }

        ImGui.Separator();

        if (haErro)
        {
            if (ImGui.Button("Copiar relatório de erro"))
                ImGui.SetClipboardText(host.Relatorio());
            ImGui.SameLine();
        }

        if (host.Ocupado) ImGui.BeginDisabled();
        if (ImGui.Button("Verificar de novo")) host.Recarregar();
        if (host.Ocupado) ImGui.EndDisabled();

        if (ImGui.CollapsingHeader("Detalhes"))
        {
            foreach (var m in host.Modulos)
            {
                ImGui.TextColored(Amarelo, m.Nome);
                foreach (var l in m.Linhas())
                    ImGui.TextWrapped(l);
            }
        }
    }
}
