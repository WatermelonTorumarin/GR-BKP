using System.Text.Json;
using System.Text.Json.Serialization;
using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;

namespace GRBKP.Core.Modules.Printing;

public sealed record CapturedPrinterDriver(
    string PackageFolderName,
    string InfFileName,
    PrinterDriverVendor Vendor);

public sealed record PrinterDriverCaptureResult(IReadOnlyList<CapturedPrinterDriver> Drivers);

public sealed record PrinterDriverInstallOutcome(
    string PackageFolderName,
    PrinterDriverVendor Vendor,
    bool Succeeded,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Captura pacotes de driver Brother/Zebra encontrados por <see cref="PrinterDriverDiscovery"/>
/// e, na restauracao, instala exatamente o mesmo pacote capturado via <c>pnputil /add-driver</c>
/// — nunca busca, baixa ou substitui por outro driver (decisao confirmada no levantamento: sem
/// pesquisa de driver alternativo na internet, sem execucao de instalador do fabricante).
/// </summary>
public sealed class PrinterDriverMigrationService(
    IFileSystemAdapter fileSystem,
    IWindowsCommandAdapter commandAdapter,
    PrinterDriverDiscovery discovery)
{
    private const string ManifestFileName = "manifest.json";
    private static readonly string[] PackageSegments = ["modules", "printer-drivers"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<PrinterDriverCaptureResult> CaptureAsync(
        string sourceVolumeRoot,
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var discovered = await discovery.DiscoverAsync(sourceVolumeRoot, cancellationToken)
            .ConfigureAwait(false);
        var destinationBase = Path.Combine([packageRoot, .. PackageSegments]);
        var captured = new List<CapturedPrinterDriver>();

        foreach (var driver in discovered)
        {
            var destinationPackagePath = Path.Combine(destinationBase, driver.PackageFolderName);
            await CopyTreeAsync(driver.PackagePath, destinationPackagePath, cancellationToken)
                .ConfigureAwait(false);
            captured.Add(new CapturedPrinterDriver(
                driver.PackageFolderName, driver.InfFileName, driver.Vendor));
        }

        if (captured.Count > 0)
        {
            await WriteManifestAsync(
                Path.Combine(destinationBase, ManifestFileName), captured, cancellationToken)
                .ConfigureAwait(false);
        }

        return new PrinterDriverCaptureResult(captured);
    }

    public async Task<IReadOnlyList<PrinterDriverInstallOutcome>> RestoreAsync(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var destinationBase = Path.Combine([packageRoot, .. PackageSegments]);
        var manifestPath = Path.Combine(destinationBase, ManifestFileName);
        var manifestInfo = await fileSystem.GetInfoAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (manifestInfo is null)
        {
            return [];
        }

        var captured = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var results = new List<PrinterDriverInstallOutcome>();
        foreach (var driver in captured)
        {
            var infPath = Path.Combine(destinationBase, driver.PackageFolderName, driver.InfFileName);
            var commandResult = await commandAdapter.ExecuteApprovedAsync(
                new WindowsCommandRequest(
                    ApprovedWindowsCommand.PnpUtil,
                    ["/add-driver", infPath, "/install"]),
                cancellationToken).ConfigureAwait(false);
            results.Add(new PrinterDriverInstallOutcome(
                driver.PackageFolderName,
                driver.Vendor,
                commandResult.ExitCode == 0,
                commandResult.StandardOutput,
                commandResult.StandardError));
        }

        return results;
    }

    private async Task CopyTreeAsync(
        string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        await fileSystem.CreateDirectoryAsync(destinationRoot, cancellationToken).ConfigureAwait(false);
        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(sourceRoot, cancellationToken)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(sourceRoot, entry.FullPath);
            var destinationPath = Path.Combine(destinationRoot, relative);
            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("Destino sem diretório pai.");
            await fileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken)
                .ConfigureAwait(false);

            await using var source = await fileSystem.OpenReadAsync(entry.FullPath, cancellationToken)
                .ConfigureAwait(false);
            await using var destination = await fileSystem.OpenWriteAsync(
                destinationPath,
                FileWriteMode.CreateOrOverwrite,
                cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteManifestAsync(
        string path, IReadOnlyList<CapturedPrinterDriver> drivers, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenWriteAsync(
            path, FileWriteMode.CreateOrOverwrite, cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(stream, drivers, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CapturedPrinterDriver>> ReadManifestAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var drivers = await JsonSerializer.DeserializeAsync<List<CapturedPrinterDriver>>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return drivers ?? [];
    }
}
