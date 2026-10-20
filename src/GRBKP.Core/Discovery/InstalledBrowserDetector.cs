using GRBKP.Core.Adapters;
using GRBKP.Core.Modules.Chromium;
using GRBKP.Core.Modules.Firefox;

namespace GRBKP.Core.Discovery;

public sealed record InstalledBrowser(string Name, string ModuleId);

/// <summary>
/// Descobre quais navegadores existem dentro de um perfil, pela presença da pasta de dados de
/// cada um. Serve ao aviso de senhas do backup: as senhas salvas no navegador não entram no
/// pacote (o Windows as cifra amarrado à conta de origem), e o técnico precisa ser lembrado do
/// passo à parte — mas só faz sentido avisar quando o navegador realmente está ali.
///
/// Usa exatamente os mesmos caminhos relativos que os Modules de migração (<see
/// cref="ChromiumBrowserDescriptor"/>, <see cref="FirefoxMigrationModule"/>): se um dia esses
/// caminhos mudarem, detecção e migração mudam juntas, sem divergir.
/// </summary>
public sealed class InstalledBrowserDetector(IFileSystemAdapter fileSystem)
{
    private static readonly (string Name, string ModuleId, IReadOnlyList<string> Segments)[] Known =
    [
        ("Microsoft Edge", "edge", ChromiumBrowserDescriptor.Edge.AppDataRelativeSegments),
        ("Google Chrome", "chrome", ChromiumBrowserDescriptor.Chrome.AppDataRelativeSegments),
        ("Firefox", "firefox", FirefoxMigrationModule.AppDataRelativeSegments)
    ];

    public async Task<IReadOnlyList<InstalledBrowser>> DetectAsync(
        string profileRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);

        var found = new List<InstalledBrowser>();
        foreach (var (name, moduleId, segments) in Known)
        {
            var path = Path.Combine([profileRoot, .. segments]);
            var info = await fileSystem.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info?.Kind == PathEntryKind.Directory)
            {
                found.Add(new InstalledBrowser(name, moduleId));
            }
        }

        return found;
    }
}
