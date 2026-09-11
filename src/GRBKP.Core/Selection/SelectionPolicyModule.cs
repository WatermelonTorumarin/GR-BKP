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
                if (IsWithinExcludedAppDataPath(relativeFromProfile))
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
            RequiresSha256: false,
            SourceRelativePath: relativePath,
            Metadata: new FileMetadata(
                entry.CreationTimeUtc,
                entry.LastWriteTimeUtc,
                entry.Attributes));
    }

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
