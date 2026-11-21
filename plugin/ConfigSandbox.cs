namespace LirouHub;

/// <summary>
/// Isola a configuração de cada plugin ligado por dentro.
/// Todos recebem a mesma interface do Dalamud, então escreveriam nos mesmos arquivos (ex.: DefaultConfig.json).
/// Solução: antes de ligar cada plugin, a pasta de configuração "viva" é limpa e recebe só os arquivos
/// DESSE plugin (guardados em modulo-configs\NOME). Logo depois que ele liga, o que ele escreveu é guardado de volta.
/// Assim cada plugin sempre começa com a sua própria configuração, nunca com a de outro.
/// Mudanças feitas dentro do plugin depois que ele já ligou não são guardadas (o Hub é quem vai configurá-los).
/// </summary>
internal sealed class ConfigSandbox
{
    // Pastas do próprio Lirou Hub dentro da pasta de configuração: nunca são tocadas.
    private static readonly HashSet<string> Protegidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "arquivos", "modulo-configs", "modulos", "configs",
    };

    private const string ArquivoEspecial = "__ConfigFile.json";

    private readonly string vivo;
    private readonly string arquivoVivo;
    private readonly string guarda;

    public ConfigSandbox(string pastaConfig, string arquivoConfig)
    {
        vivo = pastaConfig;
        arquivoVivo = arquivoConfig;
        guarda = Path.Combine(vivo, "modulo-configs");
    }

    /// <summary>Cópia de segurança de tudo que estava na pasta viva antes do primeiro plugin ligar.</summary>
    public void Backup(Action<string> log)
    {
        try
        {
            var destino = Path.Combine(guarda, "_backup");
            Apagar(destino);
            Directory.CreateDirectory(destino);
            CopiarVivoPara(destino, log);
            if (File.Exists(arquivoVivo)) File.Copy(arquivoVivo, Path.Combine(destino, ArquivoEspecial), true);
        }
        catch (Exception ex) { log("Backup das configurações falhou: " + ex.Message); }
    }

    public void PrepararPara(string modulo, Action<string> log)
    {
        LimparVivo(log);

        var origem = Path.Combine(guarda, ModuleHost.Segura(modulo));
        if (!Directory.Exists(origem))
        {
            log("Configuração isolada: começando do zero (primeira vez).");
            return;
        }

        foreach (var arq in Directory.GetFiles(origem))
        {
            var nome = Path.GetFileName(arq);
            try
            {
                if (nome == ArquivoEspecial) File.Copy(arq, arquivoVivo, true);
                else File.Copy(arq, Path.Combine(vivo, nome), true);
            }
            catch (Exception ex) { log($"Não consegui restaurar {nome}: {ex.Message}"); }
        }
        foreach (var dir in Directory.GetDirectories(origem))
        {
            try { CopiarPasta(dir, Path.Combine(vivo, Path.GetFileName(dir))); }
            catch (Exception ex) { log($"Não consegui restaurar a pasta {Path.GetFileName(dir)}: {ex.Message}"); }
        }
        log("Configuração isolada: restaurada a deste plugin.");
    }

    public void GuardarDe(string modulo, Action<string> log)
    {
        try
        {
            var destino = Path.Combine(guarda, ModuleHost.Segura(modulo));
            Apagar(destino);
            Directory.CreateDirectory(destino);
            CopiarVivoPara(destino, log);
            if (File.Exists(arquivoVivo)) File.Copy(arquivoVivo, Path.Combine(destino, ArquivoEspecial), true);
            log("Configuração isolada: guardada.");
        }
        catch (Exception ex) { log("Não consegui guardar a configuração: " + ex.Message); }
    }

    private void LimparVivo(Action<string> log)
    {
        foreach (var arq in Directory.GetFiles(vivo))
        {
            try { File.Delete(arq); }
            catch (Exception ex) { log($"Não consegui limpar {Path.GetFileName(arq)}: {ex.Message}"); }
        }
        foreach (var dir in Directory.GetDirectories(vivo))
        {
            if (Protegidos.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, true); }
            catch (Exception ex) { log($"Não consegui limpar a pasta {Path.GetFileName(dir)}: {ex.Message}"); }
        }
        try { if (File.Exists(arquivoVivo)) File.Delete(arquivoVivo); }
        catch (Exception ex) { log("Não consegui limpar o arquivo de configuração principal: " + ex.Message); }
    }

    private void CopiarVivoPara(string destino, Action<string> log)
    {
        foreach (var arq in Directory.GetFiles(vivo))
        {
            try { File.Copy(arq, Path.Combine(destino, Path.GetFileName(arq)), true); }
            catch (Exception ex) { log($"Não consegui copiar {Path.GetFileName(arq)}: {ex.Message}"); }
        }
        foreach (var dir in Directory.GetDirectories(vivo))
        {
            if (Protegidos.Contains(Path.GetFileName(dir))) continue;
            try { CopiarPasta(dir, Path.Combine(destino, Path.GetFileName(dir))); }
            catch (Exception ex) { log($"Não consegui copiar a pasta {Path.GetFileName(dir)}: {ex.Message}"); }
        }
    }

    private static void CopiarPasta(string origem, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (var arq in Directory.GetFiles(origem))
            File.Copy(arq, Path.Combine(destino, Path.GetFileName(arq)), true);
        foreach (var dir in Directory.GetDirectories(origem))
            CopiarPasta(dir, Path.Combine(destino, Path.GetFileName(dir)));
    }

    private static void Apagar(string pasta)
    {
        if (Directory.Exists(pasta)) Directory.Delete(pasta, true);
    }
}
