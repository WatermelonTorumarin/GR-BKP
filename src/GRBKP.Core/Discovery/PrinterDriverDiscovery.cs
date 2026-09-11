using GRBKP.Core.Adapters;

namespace GRBKP.Core.Discovery;

public enum PrinterDriverVendor
{
    Brother,
    Zebra,
    Other
}

public sealed record DiscoveredPrinterDriver(
    string PackageFolderName,
    string PackagePath,
    string InfFileName,
    PrinterDriverVendor Vendor);

/// <summary>
/// Descobre pacotes de driver Brother/Zebra ja instalados no Driver Store de uma instalacao
/// Windows (online ou offline, montada em outra letra) — nunca em <c>Program Files</c>, que so
/// contem utilitarios, nao o pacote real do driver (ver docs/research-printer-drivers-brother-zebra.md).
/// O local-base e sempre relativo ao volume selecionado, nunca fixo em C:, porque o SSD antigo
/// pode aparecer com qualquer letra.
/// </summary>
public sealed class PrinterDriverDiscovery(IFileSystemAdapter fileSystem)
{
    private static readonly string[] DriverStoreSegments =
        ["Windows", "System32", "DriverStore", "FileRepository"];

    public async Task<IReadOnlyList<DiscoveredPrinterDriver>> DiscoverAsync(
        string sourceVolumeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVolumeRoot);

        var fileRepository = Path.Combine([sourceVolumeRoot, .. DriverStoreSegments]);
        var results = new List<DiscoveredPrinterDriver>();

        await foreach (var packageDirectory in fileSystem
            .EnumerateDirectoriesAsync(fileRepository, cancellationToken)
            .ConfigureAwait(false))
        {
            var infFile = await FindInfFileAsync(packageDirectory.FullPath, cancellationToken)
                .ConfigureAwait(false);
            if (infFile is null)
            {
                continue;
            }

            var infContent = await ReadTextAsync(infFile, cancellationToken).ConfigureAwait(false);
            var vendor = ClassifyVendor(infContent);
            if (vendor == PrinterDriverVendor.Other)
            {
                continue;
            }

            results.Add(new DiscoveredPrinterDriver(
                Path.GetFileName(packageDirectory.FullPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                packageDirectory.FullPath,
                Path.GetFileName(infFile),
                vendor));
        }

        return results;
    }

    private async Task<string?> FindInfFileAsync(string packagePath, CancellationToken cancellationToken)
    {
        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(packagePath, cancellationToken)
            .ConfigureAwait(false))
        {
            if (entry.FullPath.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            {
                return entry.FullPath;
            }
        }

        return null;
    }

    private async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PrinterDriverVendor ClassifyVendor(string infContent)
    {
        if (infContent.Contains("Brother", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterDriverVendor.Brother;
        }

        if (infContent.Contains("Zebra", StringComparison.OrdinalIgnoreCase) ||
            infContent.Contains("ZDesigner", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterDriverVendor.Zebra;
        }

        return PrinterDriverVendor.Other;
    }
}
