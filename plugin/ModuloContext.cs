using System.Reflection;
using System.Runtime.Loader;

namespace LirouHub;

/// <summary>Espaço separado onde um plugin é carregado. Peças do Dalamud vêm do hospedeiro.</summary>
internal sealed class ModuloContext : AssemblyLoadContext
{
    private readonly string pasta;
    private readonly AssemblyLoadContext hospedeiro;

    public ModuloContext(string nome, string pasta) : base("LirouHub." + nome, true)
    {
        this.pasta = pasta;
        hospedeiro = GetLoadContext(typeof(ModuloContext).Assembly)!;
    }

    // Bibliotecas que o Dalamud fornece. Se o zip do plugin trouxer cópia própria, ela é ignorada:
    // uma segunda cópia do Dalamud faria o plugin implementar uma IDalamudPlugin "de outro Dalamud".
    private static readonly string[] Compartilhadas =
    {
        "FFXIVClientStructs", "InteropGenerator.Runtime", "Lumina", "Lumina.Excel",
        "Newtonsoft.Json", "Serilog", "CheapLoc", "ImGuiScene",
    };

    internal static bool Compartilhada(string? nome)
        => !string.IsNullOrEmpty(nome)
           && (nome.StartsWith("Dalamud", StringComparison.OrdinalIgnoreCase)
               || nome.StartsWith("SharpDX", StringComparison.OrdinalIgnoreCase)
               || Compartilhadas.Contains(nome, StringComparer.OrdinalIgnoreCase));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1) O que o hospedeiro já tem é compartilhado.
        foreach (var a in hospedeiro.Assemblies)
        {
            if (string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        // 1b) Bibliotecas do Dalamud: sempre as do Dalamud, nunca a cópia do zip.
        if (Compartilhada(assemblyName.Name))
        {
            try { return hospedeiro.LoadFromAssemblyName(assemblyName); }
            catch { /* o Dalamud não tem esta: usa a do plugin */ }
        }

        // 2) O que veio dentro do zip do plugin.
        var caminho = Path.Combine(pasta, assemblyName.Name + ".dll");
        if (File.Exists(caminho)) return LoadFromAssemblyPath(caminho);

        // 3) Último recurso: pedir ao hospedeiro (que repassa ao Dalamud).
        try { return hospedeiro.LoadFromAssemblyName(assemblyName); }
        catch { return null; }
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var nome in new[] { unmanagedDllName, unmanagedDllName + ".dll" })
        {
            var direto = Path.Combine(pasta, nome);
            if (File.Exists(direto)) return LoadUnmanagedDllFromPath(direto);

            try
            {
                var achado = Directory.GetFiles(pasta, nome, SearchOption.AllDirectories).FirstOrDefault();
                if (achado != null) return LoadUnmanagedDllFromPath(achado);
            }
            catch { /* ignora */ }
        }
        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
