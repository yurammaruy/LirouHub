using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace LirouHub;

/// <summary>
/// Tela de checagem do Hub: faz o papel da janela "Questionable Setup" (que fica escondida), mas olhando
/// onde os plugins realmente estão (dentro do Hub) e testando se cada um responde de verdade.
/// </summary>
public sealed class ChecagemWindow : Window
{
    private static readonly Vector4 Verde = new(0.35f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 Vermelho = new(0.95f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Amarelo = new(0.95f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 Cinza = new(0.70f, 0.70f, 0.70f, 1f);

    private readonly IDalamudPluginInterface pi;
    private readonly NucleoHost nucleo;
    private readonly Stopwatch relogio = Stopwatch.StartNew();
    private long ultima = -10000;
    private readonly Dictionary<string, (bool Responde, string Detalhe)> sondas = new();
    private float progressoMalha;
    private bool malhaPronta;
    private long tudoOkDesde = -1;

    public ChecagemWindow(IDalamudPluginInterface pi, NucleoHost nucleo) : base("Lirou Hub - Checagem###LirouHubChecagem")
    {
        this.pi = pi;
        this.nucleo = nucleo;
        Size = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private static (bool, string) Testa<T>(IDalamudPluginInterface pi, string nome)
    {
        try
        {
            var v = pi.GetIpcSubscriber<T>(nome).InvokeFunc();
            return (true, v?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name);
        }
    }

    private void Atualizar()
    {
        if (relogio.ElapsedMilliseconds - ultima < 1000) return;
        ultima = relogio.ElapsedMilliseconds;
        sondas.Clear();

        var nav = Testa<bool>(pi, "vnavmesh.Nav.IsReady");
        sondas["vnavmesh"] = nav;
        malhaPronta = nav.Item1 && nav.Item2 == "True";
        var prog = Testa<float>(pi, "vnavmesh.Nav.BuildProgress");
        if (prog.Item1 && float.TryParse(prog.Item2, out var p)) progressoMalha = p;

        sondas["TextAdvance"] = Testa<bool>(pi, "TextAdvance.IsInExternalControl");
        sondas["Lifestream"] = Testa<bool>(pi, "Lifestream.IsBusy");
        sondas["Questionable"] = Testa<bool>(pi, "Questionable.IsRunning");
        sondas["BossMod"] = Testa<string>(pi, "BossMod.Presets.GetActive");
    }

    private Situacao Estado(string nome) => nucleo.Itens.FirstOrDefault(i => i.Nome == nome)?.Estado ?? Situacao.Pendente;

    /// <summary>Uma linha: caixinha verde (ligado e respondendo), amarela (ligado, sem teste) ou vermelha.</summary>
    private bool Linha(string nome, string quem, bool testavel)
    {
        var est = Estado(nome);
        var ligado = est == Situacao.Ok;
        var resp = sondas.TryGetValue(nome, out var s) && s.Responde;
        if (!ligado)
        {
            ImGui.TextColored(est == Situacao.Erro ? Vermelho : Cinza, $"[ X ] {nome}");
            ImGui.SameLine();
            ImGui.TextColored(Cinza, est == Situacao.Erro ? "erro ao ligar (veja o Núcleo)" : est == Situacao.Pulado ? "não ligado" : "aguardando");
            return false;
        }
        if (testavel && !resp)
        {
            ImGui.TextColored(Amarelo, $"[ ! ] {nome}");
            ImGui.SameLine();
            ImGui.TextColored(Amarelo, "ligado, mas não responde por IPC ainda");
            return false;
        }
        ImGui.TextColored(Verde, $"[ OK ] {nome}");
        ImGui.SameLine();
        ImGui.TextColored(Cinza, quem + (testavel ? " (responde)" : " (ligado)"));
        return true;
    }

    public override void Draw()
    {
        Atualizar();

        ImGui.TextWrapped("Os plugins que o Questionable precisa rodam por dentro do Lirou Hub. Esta tela confere se todos estão ligados e respondendo.");
        ImGui.Separator();

        ImGui.TextColored(Amarelo, "Obrigatórios");
        var a = Linha("vnavmesh", "caminhos e movimento", true);
        var b = Linha("TextAdvance", "diálogos e cutscenes", true);
        var c = Linha("Lifestream", "teleportes", true);

        ImGui.Spacing();
        ImGui.TextColored(Amarelo, "Combate (basta um funcionar)");
        var d1 = Linha("BossMod", "lutas e instâncias", true);
        var d2 = Linha("RotationSolver", "rotação de habilidades", false);
        var d3 = Linha("WrathCombo", "rotação de habilidades", false);
        var combate = d1 || d2 || d3;

        ImGui.Spacing();
        ImGui.TextColored(Amarelo, "O bot");
        var e = Linha("Questionable", "faz as quests", true);

        ImGui.Spacing();
        ImGui.TextColored(Amarelo, "Malha de navegação (vnavmesh)");
        if (malhaPronta) ImGui.TextColored(Verde, "[ OK ] Malha do mapa pronta: o personagem já pode andar.");
        else
        {
            ImGui.TextColored(Amarelo, "[ ... ] Malha do mapa ainda não ficou pronta.");
            ImGui.ProgressBar(Math.Clamp(progressoMalha, 0f, 1f), new Vector2(-1, 0), $"{Math.Clamp(progressoMalha, 0f, 1f) * 100:0}%");
            if (ImGui.Button("Reconstruir malha###malha"))
            {
                try { pi.GetIpcSubscriber<bool>("vnavmesh.Nav.Rebuild").InvokeFunc(); }
                catch
                {
                    try { pi.GetIpcSubscriber<bool>("vnavmesh.Nav.Reload").InvokeFunc(); } catch { /* sem resposta */ }
                }
            }
        }

        var tudo = a && b && c && combate && e && malhaPronta;
        ImGui.Separator();
        if (tudo)
        {
            ImGui.TextColored(Verde, "TUDO PRONTO: todos os requisitos estão ligados e respondendo.");
            if (tudoOkDesde < 0) tudoOkDesde = relogio.ElapsedMilliseconds;
        }
        else
        {
            ImGui.TextColored(Amarelo, "Ainda faltam itens acima.");
            tudoOkDesde = -1;
        }
        if (ImGui.Button("Fechar###fechar")) IsOpen = false;
    }
}
