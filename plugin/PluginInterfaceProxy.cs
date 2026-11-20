using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace LirouHub;

/// <summary>
/// Cópia da "interface do plugin" entregue a cada módulo. Repassa tudo ao Dalamud de verdade,
/// exceto a configuração: cada módulo ganha pasta e arquivo próprios, para um não sobrescrever o outro.
/// </summary>
public class PluginInterfaceProxy : DispatchProxy
{
    private IDalamudPluginInterface real = null!;
    private string pasta = "";
    private AssemblyLoadContext ctx = null!;

    internal void Iniciar(IDalamudPluginInterface real, string pasta, AssemblyLoadContext ctx)
    {
        this.real = real;
        this.pasta = pasta;
        this.ctx = ctx;
        Directory.CreateDirectory(pasta);
    }

    private string Arquivo => Path.Combine(pasta, "config.json");

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) return null;
        try
        {
            switch (targetMethod.Name)
            {
                case "get_ConfigDirectory":
                    return new DirectoryInfo(pasta);
                case "get_ConfigFile":
                    return new FileInfo(Arquivo);
                case "GetPluginConfigDirectory":
                    return pasta;
                case "GetPluginConfig":
                    return LerConfig();
                case "SavePluginConfig":
                    if (args is { Length: > 0 } && args[0] is IPluginConfiguration c)
                        File.WriteAllText(Arquivo, JsonConvert.SerializeObject(c, Formatting.Indented, Ajustes()));
                    return null;
            }
            return targetMethod.Invoke(real, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private object? LerConfig()
    {
        try
        {
            if (!File.Exists(Arquivo)) return null;
            return JsonConvert.DeserializeObject<IPluginConfiguration>(File.ReadAllText(Arquivo), Ajustes());
        }
        catch
        {
            return null; // arquivo estragado: o módulo começa com a configuração padrão
        }
    }

    private JsonSerializerSettings Ajustes() => new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        SerializationBinder = new Binder(ctx),
    };

    private sealed class Binder : ISerializationBinder
    {
        private readonly AssemblyLoadContext ctx;
        public Binder(AssemblyLoadContext ctx) => this.ctx = ctx;

        public Type BindToType(string? assemblyName, string typeName)
        {
            var alvo = string.IsNullOrEmpty(assemblyName) ? "" : new AssemblyName(assemblyName).Name;
            foreach (var a in ctx.Assemblies)
            {
                if (a.GetName().Name == alvo)
                {
                    var t = a.GetType(typeName);
                    if (t != null) return t;
                }
            }
            return Type.GetType($"{typeName}, {assemblyName}")
                ?? throw new JsonSerializationException($"Tipo não encontrado: {typeName}");
        }

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            assemblyName = serializedType.Assembly.GetName().Name;
            typeName = serializedType.FullName;
        }
    }
}
