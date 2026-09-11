using System.Runtime.CompilerServices;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules.Mozilla;
using GRBKP.Core.Restore;

namespace GRBKP.Core.Modules.Firefox;

/// <summary>
/// Migra o perfil do Firefox como uma arvore de aplicativo (substitui, nao mescla), usando o
/// mesmo mecanismo do Thunderbird: os dois compartilham o gerenciador de perfis e o formato de
/// <c>profiles.ini</c> da Mozilla (ver <see cref="MozillaProfilesIni"/>), incluindo o mesmo
/// problema de Install ID por instalacao que motivou a correcao original.
/// </summary>
public sealed class FirefoxMigrationModule(
    IFileSystemAdapter fileSystem,
    IProcessAdapter processAdapter) : IMigrationModule
{
    private const string ProfilesIniFileName = "profiles.ini";
    private const string PackageModuleSubpath = "modules/firefox/root";
    private static readonly string[] ProcessNames = ["firefox"];

    /// <summary>
    /// Caminho do AppData deste app, relativo a raiz do perfil — publico para que a politica de
    /// selecao generica (SelectionPolicyModule) saiba excluir esta subarvore da varredura de
    /// AppData nao classificado, evitando capturar os mesmos arquivos duas vezes.
    /// </summary>
    public static readonly IReadOnlyList<string> AppDataRelativeSegments =
        ["AppData", "Roaming", "Mozilla", "Firefox"];

    public string Id => "firefox";

    public async Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var firefoxRoot = Path.Combine([context.SourceRoot, .. AppDataRelativeSegments]);
        var profilesIniPath = Path.Combine(firefoxRoot, ProfilesIniFileName);
        var info = await context.FileSystem.GetInfoAsync(profilesIniPath, ct).ConfigureAwait(false);
        if (info is null)
        {
            return new ModuleDiscovery(Id, false, new Dictionary<string, string?>());
        }

        var ini = MozillaProfilesIni.Parse(
            await ReadAllTextAsync(context.FileSystem, profilesIniPath, ct).ConfigureAwait(false));
        var defaultProfilePath = ini.ResolveDefaultProfilePath();

        var facts = new Dictionary<string, string?>
        {
            ["firefoxRoot"] = firefoxRoot,
            ["defaultProfileRelativePath"] = defaultProfilePath
        };
        return new ModuleDiscovery(Id, defaultProfilePath is not null, facts);
    }

    public async IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
        ModuleDiscovery discovery,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (!discovery.IsApplicable ||
            !discovery.Facts.TryGetValue("firefoxRoot", out var firefoxRoot) ||
            firefoxRoot is null)
        {
            yield break;
        }

        var sequence = 0L;
        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(firefoxRoot, ct)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(firefoxRoot, entry.FullPath).Replace('\\', '/');
            sequence++;
            yield return new TransferItem(
                sequence,
                Id,
                entry.FullPath,
                $"{PackageModuleSubpath}/{relative}",
                TransferClassification.ModuleData,
                SourceRelativePath: relative,
                Metadata: new FileMetadata(entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Attributes));
        }
    }

    public async Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Decisao Q64, aplicada da mesma forma que no Thunderbird: encerrar a forca ja no
        // primeiro passo, antes de qualquer outra coisa.
        await processAdapter.TerminateProcessTreesAsync(ProcessNames, ct).ConfigureAwait(false);

        var destinationRoot = Path.Combine(
            [context.DestinationProfileRoot, .. AppDataRelativeSegments]);
        var packageModulePath = Path.Combine(
            context.PackageRoot,
            "modules", "firefox", "root");

        var packageInfo = await context.FileSystem.GetInfoAsync(packageModulePath, ct)
            .ConfigureAwait(false);
        if (packageInfo is null)
        {
            return new ModuleResult(Id, ModuleOutcome.NotApplicable, []);
        }

        var destinationInstallId = await ReadInstallSectionNameAsync(
            context.FileSystem,
            Path.Combine(destinationRoot, ProfilesIniFileName),
            ct).ConfigureAwait(false);

        var stagingRoot = destinationRoot + $".staging.{context.OperationId:N}";
        await CopyTreeAsync(context.FileSystem, packageModulePath, stagingRoot, ct).ConfigureAwait(false);
        if (destinationInstallId is not null)
        {
            await RebindDefaultProfileAsync(
                context.FileSystem,
                stagingRoot,
                destinationInstallId,
                ct).ConfigureAwait(false);
        }

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

        var profilesIniPath = Path.Combine(context.DestinationPath, ProfilesIniFileName);
        var info = await context.FileSystem.GetInfoAsync(profilesIniPath, ct).ConfigureAwait(false);
        if (info is null)
        {
            return Invalid("profiles-ini-missing", "profiles.ini ausente após a restauração.");
        }

        var ini = MozillaProfilesIni.Parse(
            await ReadAllTextAsync(context.FileSystem, profilesIniPath, ct).ConfigureAwait(false));
        var defaultProfilePath = ini.ResolveDefaultProfilePath();
        if (defaultProfilePath is null)
        {
            return Invalid(
                "default-profile-unresolved",
                "Nenhum perfil padrão pôde ser resolvido em profiles.ini.");
        }

        var prefsPath = Path.Combine(
            context.DestinationPath,
            defaultProfilePath.Replace('/', Path.DirectorySeparatorChar),
            "prefs.js");
        var prefsInfo = await context.FileSystem.GetInfoAsync(prefsPath, ct).ConfigureAwait(false);
        if (prefsInfo is null)
        {
            return Invalid(
                "default-profile-incomplete",
                $"prefs.js ausente no perfil padrão restaurado ({defaultProfilePath}).");
        }

        var prefsContent = await ReadAllTextAsync(context.FileSystem, prefsPath, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prefsContent))
        {
            return Invalid(
                "default-profile-corrupted",
                $"prefs.js do perfil padrão restaurado está vazio ({defaultProfilePath}).");
        }

        return new ValidationResult(
            true,
            [new MigrationEvidence(
                "firefox-profile-active",
                $"Perfil padrão ativo após a restauração: {defaultProfilePath}.",
                EvidenceSeverity.Information)]);
    }

    private static ValidationResult Invalid(string code, string message) => new(
        false,
        [new MigrationEvidence(code, message, EvidenceSeverity.Error)],
        code);

    private static async Task<string?> ReadInstallSectionNameAsync(
        IFileSystemAdapter targetFileSystem,
        string profilesIniPath,
        CancellationToken ct)
    {
        var info = await targetFileSystem.GetInfoAsync(profilesIniPath, ct).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        var ini = MozillaProfilesIni.Parse(
            await ReadAllTextAsync(targetFileSystem, profilesIniPath, ct).ConfigureAwait(false));
        return ini.FindInstallSectionName();
    }

    private static async Task RebindDefaultProfileAsync(
        IFileSystemAdapter targetFileSystem,
        string stagingRoot,
        string destinationInstallId,
        CancellationToken ct)
    {
        var profilesIniPath = Path.Combine(stagingRoot, ProfilesIniFileName);
        var info = await targetFileSystem.GetInfoAsync(profilesIniPath, ct).ConfigureAwait(false);
        if (info is null)
        {
            return;
        }

        var recovered = MozillaProfilesIni.Parse(
            await ReadAllTextAsync(targetFileSystem, profilesIniPath, ct).ConfigureAwait(false));
        var defaultProfilePath = recovered.ResolveDefaultProfilePath();
        if (defaultProfilePath is null)
        {
            return;
        }

        var rebound = recovered.WithInstallDefault(destinationInstallId, defaultProfilePath);
        await WriteAllTextAsync(targetFileSystem, profilesIniPath, rebound.Serialize(), ct)
            .ConfigureAwait(false);
    }

    private static async Task CopyTreeAsync(
        IFileSystemAdapter targetFileSystem,
        string sourceRoot,
        string destinationRoot,
        CancellationToken ct)
    {
        await targetFileSystem.CreateDirectoryAsync(destinationRoot, ct).ConfigureAwait(false);
        await foreach (var entry in targetFileSystem
            .EnumerateFilesAsync(sourceRoot, ct)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(sourceRoot, entry.FullPath);
            var destinationPath = Path.Combine(destinationRoot, relative);
            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("Destino sem diretório pai.");
            await targetFileSystem.CreateDirectoryAsync(destinationDirectory, ct).ConfigureAwait(false);

            await using var source = await targetFileSystem.OpenReadAsync(entry.FullPath, ct)
                .ConfigureAwait(false);
            await using var destination = await targetFileSystem.OpenWriteAsync(
                destinationPath,
                FileWriteMode.CreateOrOverwrite,
                ct).ConfigureAwait(false);
            await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAllTextAsync(
        IFileSystemAdapter targetFileSystem,
        string path,
        CancellationToken ct)
    {
        await using var stream = await targetFileSystem.OpenReadAsync(path, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteAllTextAsync(
        IFileSystemAdapter targetFileSystem,
        string path,
        string content,
        CancellationToken ct)
    {
        await using var stream = await targetFileSystem.OpenWriteAsync(
            path,
            FileWriteMode.CreateOrOverwrite,
            ct).ConfigureAwait(false);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
