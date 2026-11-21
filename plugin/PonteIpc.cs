using Dalamud.Plugin;

namespace LirouHub;

/// <summary>
/// Plugins que usam o ECommons (TextAdvance, Lifestream) registram os nomes de IPC com o prefixo do
/// "nome interno" do plugin dono da interface. Dentro do Hub esse nome é "LirouHub", então eles aparecem
/// como "LirouHub.IsBusy" em vez de "Lifestream.IsBusy", e o Questionable não os encontra.
/// Esta ponte republica esses nomes com o prefixo certo, repassando cada chamada.
/// </summary>
public sealed class PonteIpc : IDisposable
{
    private const string Dono = "LirouHub";
    private readonly IDalamudPluginInterface pi;
    private readonly List<Action> desfazer = new();
    public int Registrados { get; private set; }
    public string Erro { get; private set; } = "";

    public PonteIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        try
        {
            // TextAdvance
            F0<bool>("TextAdvance", "IsInExternalControl");
            F0<bool>("TextAdvance", "IsEnabled");
            F0<bool>("TextAdvance", "IsPaused");
            F1<string, bool>("TextAdvance", "DisableExternalControl");
            F2<string, object, bool>("TextAdvance", "EnableExternalControl");

            // Lifestream (o "IsBusy" do Lifestream é o que vale quando há nome repetido)
            F0<bool>("Lifestream", "IsBusy");
            F0<bool>("Lifestream", "AethernetTeleportToFirmament");
            F1<string, bool>("Lifestream", "AethernetTeleport");
            F1<uint, bool>("Lifestream", "AethernetTeleportById");
            F1<uint, bool>("Lifestream", "AethernetTeleportByPlaceNameId");
        }
        catch (Exception ex)
        {
            Erro = ex.Message;
        }
    }

    private string De(string nome) => $"{Dono}.{nome}";

    private void F0<R>(string alvo, string nome)
    {
        var p = pi.GetIpcProvider<R>($"{alvo}.{nome}");
        p.RegisterFunc(() => pi.GetIpcSubscriber<R>(De(nome)).InvokeFunc());
        desfazer.Add(() => p.UnregisterFunc());
        Registrados++;
    }

    private void F1<A, R>(string alvo, string nome)
    {
        var p = pi.GetIpcProvider<A, R>($"{alvo}.{nome}");
        p.RegisterFunc(a => pi.GetIpcSubscriber<A, R>(De(nome)).InvokeFunc(a));
        desfazer.Add(() => p.UnregisterFunc());
        Registrados++;
    }

    private void F2<A, B, R>(string alvo, string nome)
    {
        var p = pi.GetIpcProvider<A, B, R>($"{alvo}.{nome}");
        p.RegisterFunc((a, b) => pi.GetIpcSubscriber<A, B, R>(De(nome)).InvokeFunc(a, b));
        desfazer.Add(() => p.UnregisterFunc());
        Registrados++;
    }

    public void Dispose()
    {
        foreach (var d in desfazer)
        {
            try { d(); } catch { }
        }
        desfazer.Clear();
    }
}
