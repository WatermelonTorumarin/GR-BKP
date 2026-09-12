using System.Runtime.CompilerServices;
using GRBKP.Core.Adapters;
using GRBKP.Core.Restore;

namespace GRBKP.Core.Modules.Adobe;

/// <summary>
/// Migra as configurações básicas do Adobe Acrobat Reader (pasta "AppData/Roaming/Adobe/Acrobat"
/// inteira) — mesmo padrão de troca de árvore única usado pelo Chromium, sem nenhuma reescrita de
/// configuração. Escopo desta versão (decisão do responsável): apenas configurações básicas, sem
/// licenciamento/ativação (isso é conta Adobe/servidor, não arquivo local).
/// </summary>
public sealed class AdobeAcrobatMigrationModule(
    IFileSystemAdapter fileSystem,
    IProcessAdapter processAdapter) : IMigrationModule
{
    public static readonly IReadOnlyList<string> AppDataRelativeSegments =
        ["AppData", "Roaming", "Adobe", "Acrobat"];

    public static readonly string[] ProcessNames = ["AcroRd32", "Acrobat"];

    public string Id => "adobe-acrobat";

    public async Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var acrobatRoot = ResolveAcrobatRoot(context.SourceRoot);
        var info = await context.FileSystem.GetInfoAsync(acrobatRoot, ct).ConfigureAwait(false);
        var facts = new Dictionary<string, string?> { ["acrobatRoot"] = acrobatRoot };
        return new ModuleDiscovery(Id, info?.Kind == PathEntryKind.Directory, facts);
    }

    public async IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
        ModuleDiscovery discovery,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (!discovery.IsApplicable ||
            !discovery.Facts.TryGetValue("acrobatRoot", out var acrobatRoot) ||
            acrobatRoot is null)
        {
            yield break;
        }

        var sequence = 0L;
        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(acrobatRoot, ct)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(acrobatRoot, entry.FullPath).Replace('\\', '/');
            sequence++;
            yield return new TransferItem(
                sequence,
                Id,
                entry.FullPath,
                $"modules/{Id}/root/{relative}",
                TransferClassification.ModuleData,
                SourceRelativePath: relative,
                Metadata: new FileMetadata(entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Attributes),
                Length: entry.Length);
        }
    }

    public async Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await processAdapter.TerminateProcessTreesAsync(ProcessNames, ct).ConfigureAwait(false);

        var destinationRoot = ResolveAcrobatRoot(context.DestinationProfileRoot);
        var packageModulePath = Path.Combine(context.PackageRoot, "modules", Id, "root");

        var packageInfo = await context.FileSystem.GetInfoAsync(packageModulePath, ct)
            .ConfigureAwait(false);
        if (packageInfo is null)
        {
            return new ModuleResult(Id, ModuleOutcome.NotApplicable, []);
        }

        var stagingRoot = destinationRoot + $".staging.{context.OperationId:N}";
        await CopyTreeAsync(context.FileSystem, packageModulePath, stagingRoot, ct).ConfigureAwait(false);

        var destinationParent = Path.GetDirectoryName(destinationRoot)
            ?? throw new InvalidOperationException("Árvore de destino sem diretório pai.");
        await context.FileSystem.CreateDirectoryAsync(destinationParent, ct).ConfigureAwait(false);

        var restorer = new ApplicationTreeRestorer(context.FileSystem);
        var swapResult = await restorer.RestoreAsync(
            new ModuleTreeRestoreRequest(context.OperationId, Id, stagingRoot, destinationRoot),
            this,
            ct).ConfigureAwait(false);
        return swapResult.ModuleResult;
    }

    public async Task<ValidationResult> ValidateAsync(ValidationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await foreach (var entry in context.FileSystem
            .EnumerateFilesAsync(context.DestinationPath, ct)
            .ConfigureAwait(false))
        {
            if (entry.Length > 0)
            {
                return new ValidationResult(
                    true,
                    [new MigrationEvidence(
                        $"{Id}-restored",
                        "Configurações do Acrobat Reader restauradas com conteúdo confirmado.",
                        EvidenceSeverity.Information)]);
            }
        }

        return new ValidationResult(
            false,
            [new MigrationEvidence(
                $"{Id}-empty-or-corrupted",
                "Nenhum arquivo com conteúdo real encontrado após a restauração.",
                EvidenceSeverity.Error)],
            $"{Id}-empty-or-corrupted");
    }

    private static string ResolveAcrobatRoot(string profileRoot) =>
        Path.Combine([profileRoot, .. AppDataRelativeSegments]);

    private static async Task CopyTreeAsync(
        IFileSystemAdapter targetFileSystem,
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await targetFileSystem.CreateDirectoryAsync(destinationRoot, cancellationToken)
            .ConfigureAwait(false);
        await foreach (var entry in targetFileSystem
            .EnumerateFilesAsync(sourceRoot, cancellationToken)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(sourceRoot, entry.FullPath);
            var destinationPath = Path.Combine(destinationRoot, relative);
            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("Destino sem diretório pai.");
            await targetFileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken)
                .ConfigureAwait(false);

            await using var source = await targetFileSystem
                .OpenReadAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
            await using var destination = await targetFileSystem.OpenWriteAsync(
                destinationPath, FileWriteMode.CreateOrOverwrite, cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
