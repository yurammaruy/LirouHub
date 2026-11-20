using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LirouHub;

public sealed class Plugin : IDalamudPlugin
{
    private const string Comando = "/lirouhub";

    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly WindowSystem windows = new("LirouHub");
    private readonly MainWindow janela;

    public ModuleHost Host { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IDataManager dataManager)
    {
        pi = pluginInterface;
        commands = commandManager;

        Host = new ModuleHost(pluginInterface, log);
        janela = new MainWindow(Host, new QuestTracker(dataManager, log));
        windows.AddWindow(janela);

        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += janela.Toggle;
        pi.UiBuilder.OpenConfigUi += janela.Toggle;

        commands.AddHandler(Comando, new CommandInfo(OnComando)
        {
            HelpMessage = "Abre ou fecha a janela do Lirou Hub.",
        });

        janela.IsOpen = true;
        Host.Iniciar();
    }

    private void OnComando(string command, string args) => janela.Toggle();

    public void Dispose()
    {
        commands.RemoveHandler(Comando);
        pi.UiBuilder.Draw -= windows.Draw;
        pi.UiBuilder.OpenMainUi -= janela.Toggle;
        pi.UiBuilder.OpenConfigUi -= janela.Toggle;
        windows.RemoveAllWindows();
        Host.Dispose();
    }
}
