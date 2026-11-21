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
    public NucleoHost Nucleo { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework,
        IChatGui chat,
        IClientState clientState,
        IObjectTable objectTable,
        IDataManager dataManager,
        ICondition condition,
        IGameGui gameGui,
        ITextureProvider textureProvider,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPartyList partyList,
        ITargetManager targetManager,
        IToastGui toastGui,
        IAddonLifecycle addonLifecycle,
        IKeyState keyState,
        INotificationManager notifications,
        ITextureReadbackProvider textureReadback,
        IPlayerState playerState,
        IAddonEventManager addonEvents,
        IDtrBar dtrBar,
        IContextMenu contextMenu,
        IDutyState dutyState,
        IGameConfig gameConfig,
        IGameInventory gameInventory,
        IGamepadState gamepadState,
        IJobGauges jobGauges,
        IFateTable fateTable,
        IBuddyList buddyList,
        IAetheryteList aetheryteList,
        IFlyTextGui flyTextGui,
        ITitleScreenMenu titleMenu,
        INamePlateGui namePlateGui)
    {
        pi = pluginInterface;
        commands = commandManager;

        // Peças do jogo que o hospedeiro repassa aos plugins que ele liga por dentro.
        var servicos = new Dictionary<Type, object>();
        void Add<T>(T s) where T : class => servicos[typeof(T)] = s;
        Add(commandManager);
        Add(log);
        Add(framework);
        Add(chat);
        Add(clientState);
        Add(objectTable);
        Add(dataManager);
        Add(condition);
        Add(gameGui);
        Add(textureProvider);
        Add(interop);
        Add(sigScanner);
        Add(partyList);
        Add(targetManager);
        Add(toastGui);
        Add(addonLifecycle);
        Add(keyState);
        Add(notifications);
        Add(textureReadback);
        Add(playerState);
        Add(addonEvents);
        Add(dtrBar);
        Add(contextMenu);
        Add(dutyState);
        Add(gameConfig);
        Add(gameInventory);
        Add(gamepadState);
        Add(jobGauges);
        Add(fateTable);
        Add(buddyList);
        Add(aetheryteList);
        Add(flyTextGui);
        Add(titleMenu);
        Add(namePlateGui);

        Host = new ModuleHost(pluginInterface, log);
        Nucleo = new NucleoHost(pluginInterface, framework, log, servicos, Host.PastaArquivos);
        janela = new MainWindow(Host, new QuestTracker(dataManager, log, pluginInterface, Nucleo), Nucleo);
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
        Nucleo.Dispose();
        Host.Dispose();
    }
}
