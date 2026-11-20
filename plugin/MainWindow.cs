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
        Size = new Vector2(680, 460);
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
        var totalOk = 0;
        var totalTodos = 0;
        var erros = 0;
        long bytes = 0;
        var repoErro = false;
        var repoPendente = false;

        foreach (var r in host.Repos)
        {
            if (r.Estado == Situacao.Erro) repoErro = true;
            if (r.Estado != Situacao.Ok && r.Estado != Situacao.Erro) repoPendente = true;
            foreach (var i in r.Itens())
            {
                totalTodos++;
                if (i.Estado == Situacao.Ok || i.Estado == Situacao.Pulado) totalOk++;
                if (i.Estado == Situacao.Erro) erros++;
                bytes += i.Bytes;
            }
        }

        if (host.Ocupado || repoPendente)
            ImGui.TextColored(Amarelo, $"Verificando... {totalOk} de {totalTodos} plugins conferidos.");
        else if (repoErro || erros > 0)
            ImGui.TextColored(Vermelho, $"ERRO: {erros} plugins com problema. Use Copiar relatório.");
        else
            ImGui.TextColored(Verde, $"OK: {totalOk} plugins baixados e conferidos ({bytes / 1024 / 1024} MB). Nada foi executado.");

        ImGui.Separator();

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

            ImGui.TextColored(Cinza, $"{r.Grupo}");
            if (r.Msg.Length > 0) ImGui.TextWrapped(r.Msg);
            foreach (var i in itens)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Cor(i.Estado));
                ImGui.TextWrapped($"{i.Estado}  {i.Nome} {i.Versao}  {i.Msg}");
                ImGui.PopStyleColor();
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Copiar relatório")) ImGui.SetClipboardText(host.Relatorio());
        ImGui.SameLine();
        if (host.Ocupado) ImGui.BeginDisabled();
        if (ImGui.Button("Verificar de novo")) host.Recarregar();
        if (host.Ocupado) ImGui.EndDisabled();
    }
}
