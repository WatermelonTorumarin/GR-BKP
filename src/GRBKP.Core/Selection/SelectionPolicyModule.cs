using System.Runtime.CompilerServices;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;

namespace GRBKP.Core.Selection;

public sealed class SelectionPolicyModule(
    IFileSystemAdapter fileSystem,
    IReadOnlyList<string>? excludedAppDataRelativePaths = null)
{
    public const int PicturesThreshold = 21;

    private readonly IReadOnlyList<string> excludedAppDataRelativePaths =
        excludedAppDataRelativePaths ?? [];

    public SelectionRun Select(ProfileSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var summary = new SelectionSummary();
        return new SelectionRun(EnumerateAsync(selection, summary), summary);
    }

    private async IAsyncEnumerable<TransferItem> EnumerateAsync(
        ProfileSelection selection,
        SelectionSummary summary,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var picturesRoot = Path.Combine(selection.ProfileRoot, "Pictures");
        var observed = new List<FileSystemEntry>(PicturesThreshold);
        var picturesIncluded = false;
        long sequence = 0;
        var localFileCount = 0;

        foreach (var folder in StandardPersonalFolders)
        {
            var root = Path.Combine(selection.ProfileRoot, folder.PhysicalName);
            var count = 0;
            await foreach (var entry in fileSystem
                .EnumerateFilesAsync(root, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                if (!IsEligibleLocalFile(entry))
                {
                    continue;
                }

                count++;
                yield return CreateItem(
                    ++sequence,
                    "user-files",
                    entry,
                    root,
                    $"data/Users/{selection.ProfileName}/{folder.PackageName}",
                    TransferClassification.PersonalFile);
            }

            summary.Record(new SelectionDecision(
                folder.LogicalFolder,
                count > 0 ? SelectionDecisionKind.Included : SelectionDecisionKind.Missing,
                count,
                0,
                count > 0 ? "folder-included" : "folder-missing"));
        }

        if (selection.PublicRoot is not null)
        {
            foreach (var folder in StandardPublicFolders)
            {
                var root = Path.Combine(selection.PublicRoot, folder.PhysicalName);
                var count = 0;
                await foreach (var entry in fileSystem
                    .EnumerateFilesAsync(root, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!IsEligibleLocalFile(entry))
                    {
                        continue;
                    }

                    count++;
                    yield return CreateItem(
                        ++sequence,
                        "public-files",
                        entry,
                        root,
                        $"data/Users/Public/{folder.PackageName}",
                        TransferClassification.PublicFile);
                }

                summary.Record(new SelectionDecision(
                    folder.LogicalFolder,
                    count > 0 ? SelectionDecisionKind.Included : SelectionDecisionKind.Missing,
                    count,
                    0,
                    count > 0 ? "public-folder-included" : "public-folder-missing"));
            }
        }

        var appDataCount = 0;
        foreach (var appDataPart in AppDataFolders)
        {
            var root = Path.Combine(selection.ProfileRoot, "AppData", appDataPart);
            await foreach (var entry in fileSystem
                .EnumerateFilesAsync(root, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                if (!IsEligibleLocalFile(entry))
                {
                    continue;
                }

                var relativeFromProfile =
                    $"AppData/{appDataPart}/{Path.GetRelativePath(root, entry.FullPath).Replace('\\', '/')}";
                if (IsWithinExcludedAppDataPath(relativeFromProfile) ||
                    IsEphemeralAppDataPath(relativeFromProfile))
                {
                    continue;
                }

                appDataCount++;
                yield return CreateItem(
                    ++sequence,
                    "unclassified-appdata",
                    entry,
                    root,
                    $"data/Users/{selection.ProfileName}/AppData/{appDataPart}",
                    TransferClassification.UnclassifiedApplicationData);
            }
        }

        summary.Record(new SelectionDecision(
            LogicalFolder.AppData,
            appDataCount > 0 ? SelectionDecisionKind.Included : SelectionDecisionKind.Missing,
            appDataCount,
            0,
            appDataCount > 0 ? "appdata-captured" : "appdata-missing"));

        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(picturesRoot, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!IsEligibleLocalFile(entry))
            {
                continue;
            }

            localFileCount++;
            if (!picturesIncluded)
            {
                observed.Add(entry);
                if (observed.Count < PicturesThreshold)
                {
                    continue;
                }

                picturesIncluded = true;
                foreach (var retained in observed)
                {
                    yield return CreatePicturesItem(
                        ++sequence,
                        selection,
                        picturesRoot,
                        retained);
                }

                observed.Clear();
                continue;
            }

            yield return CreatePicturesItem(++sequence, selection, picturesRoot, entry);
        }

        summary.Record(picturesIncluded
            ? new SelectionDecision(
                LogicalFolder.Pictures,
                SelectionDecisionKind.Included,
                localFileCount,
                PicturesThreshold,
                "pictures-included")
            : new SelectionDecision(
                LogicalFolder.Pictures,
                SelectionDecisionKind.IgnoredBelowThreshold,
                localFileCount,
                PicturesThreshold,
                "pictures-below-threshold"));
    }

    private static TransferItem CreatePicturesItem(
        long sequence,
        ProfileSelection selection,
        string picturesRoot,
        FileSystemEntry entry)
    {
        return CreateItem(
            sequence,
            "user-files",
            entry,
            picturesRoot,
            $"data/Users/{selection.ProfileName}/Pictures",
            TransferClassification.PersonalFile);
    }

    private static TransferItem CreateItem(
        long sequence,
        string moduleId,
        FileSystemEntry entry,
        string sourceRoot,
        string packageRoot,
        TransferClassification classification)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, entry.FullPath)
            .Replace('\\', '/');
        return new TransferItem(
            sequence,
            moduleId,
            entry.FullPath,
            $"{packageRoot}/{relativePath}",
            classification,
            RequiresSha256: IsCriticalFile(packageRoot, relativePath),
            SourceRelativePath: relativePath,
            Metadata: new FileMetadata(
                entry.CreationTimeUtc,
                entry.LastWriteTimeUtc,
                entry.Attributes));
    }

    /// <summary>
    /// Quais arquivos ganham verificação por hash SHA-256 durante a cópia. Não é todo mundo de
    /// propósito: o hash é calculado no mesmo buffer da cópia (sem I/O extra), mas custa CPU —
    /// medido a 281 MB/s na máquina de referência, o que pesaria num PST de vários GB.
    ///
    /// O critério é "o que ninguém percebe se corromper até precisar, com a origem já
    /// formatada": documentos do funcionário e as bases do Outlook. Um instalador em Downloads
    /// ou um cache de aplicativo não justificam o custo — são recuperáveis de outro jeito.
    /// </summary>
    private static bool IsCriticalFile(string packageRoot, string relativePath) =>
        packageRoot.EndsWith("/Documents", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".ost", StringComparison.OrdinalIgnoreCase);

    private bool IsWithinExcludedAppDataPath(string relativeFromProfile)
    {
        foreach (var excluded in excludedAppDataRelativePaths)
        {
            if (relativeFromProfile.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                relativeFromProfile.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pastas de AppData cujo conteúdo o próprio Windows/aplicativo recria sozinho: copiá-las só
    /// aumenta o tempo do backup e ocupa o SSD (medido num perfil real: centenas de MB entre
    /// Temp, dumps de falha e caches de miniatura). A lista é deliberadamente CURTA e explícita —
    /// só entra aqui o que é inequivocamente descartável. Nada de regra genérica por nome (como
    /// excluir qualquer pasta chamada "Cache"), porque aplicativos guardam dado real em pastas
    /// com esses nomes e perder dado do usuário é muito pior do que copiar lixo.
    /// </summary>
    private static readonly string[] EphemeralAppDataPaths =
    [
        "AppData/Local/Temp",
        "AppData/Local/CrashDumps",
        "AppData/Local/Microsoft/Windows/Explorer",
        "AppData/Local/Microsoft/Windows/INetCache",
        "AppData/Local/Microsoft/Windows/WebCache",
        "AppData/Local/Microsoft/Windows/Temporary Internet Files",
        "AppData/Local/Microsoft/Terminal Server Client/Cache",
        "AppData/Local/Microsoft/Windows/WER"
    ];

    private static bool IsEphemeralAppDataPath(string relativeFromProfile)
    {
        foreach (var ephemeral in EphemeralAppDataPaths)
        {
            if (relativeFromProfile.StartsWith(ephemeral + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEligibleLocalFile(FileSystemEntry entry) =>
        !entry.IsCloudPlaceholder && !entry.IsReparsePoint;

    private static readonly FolderRule[] StandardPersonalFolders =
    [
        new("Desktop", "Desktop", LogicalFolder.Desktop),
        new("Documents", "Documents", LogicalFolder.Documents),
        new("Downloads", "Downloads", LogicalFolder.Downloads)
    ];

    private static readonly FolderRule[] StandardPublicFolders =
    [
        new("Desktop", "Desktop", LogicalFolder.Desktop),
        new("Documents", "Documents", LogicalFolder.Documents),
        new("Downloads", "Downloads", LogicalFolder.Downloads)
    ];

    private static readonly string[] AppDataFolders = ["Local", "LocalLow", "Roaming"];

    private sealed record FolderRule(
        string PhysicalName,
        string PackageName,
        LogicalFolder LogicalFolder);
}

public sealed record ProfileSelection(string ProfileName, string ProfileRoot, string? PublicRoot);

public sealed record SelectionRun(
    IAsyncEnumerable<TransferItem> Items,
    SelectionSummary Summary);

public sealed class SelectionSummary
{
    private readonly List<SelectionDecision> decisions = [];

    public IReadOnlyList<SelectionDecision> Decisions => decisions;

    internal void Record(SelectionDecision decision) => decisions.Add(decision);
}

public sealed record SelectionDecision(
    LogicalFolder LogicalFolder,
    SelectionDecisionKind Kind,
    int LocalFileCount,
    int RequiredFileCount,
    string ReasonCode);

public enum LogicalFolder
{
    AppData,
    Desktop,
    Documents,
    Downloads,
    Pictures
}

public enum SelectionDecisionKind
{
    Included,
    IgnoredBelowThreshold,
    Missing
}
