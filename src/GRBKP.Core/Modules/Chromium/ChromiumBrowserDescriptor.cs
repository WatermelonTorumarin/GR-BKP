namespace GRBKP.Core.Modules.Chromium;

/// <summary>
/// Parametriza <see cref="ChromiumMigrationModule"/> para um navegador especifico baseado em
/// Chromium. Chrome e Edge compartilham a mesma estrutura de "User Data" — o que muda e apenas
/// o caminho do AppData e o nome do processo.
/// </summary>
public sealed record ChromiumBrowserDescriptor(
    string ModuleId,
    IReadOnlyList<string> AppDataRelativeSegments,
    string ProcessName)
{
    public static ChromiumBrowserDescriptor Chrome { get; } = new(
        "chrome",
        ["AppData", "Local", "Google", "Chrome", "User Data"],
        "chrome");

    public static ChromiumBrowserDescriptor Edge { get; } = new(
        "edge",
        ["AppData", "Local", "Microsoft", "Edge", "User Data"],
        "msedge");
}
