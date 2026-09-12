using System.Runtime.CompilerServices;
using GRBKP.Core.Adapters;
using GRBKP.Core.Restore;
using GRBKP.Core.Modules.Mozilla;

namespace GRBKP.Core.Modules.Thunderbird;

/// <summary>
/// Migra o perfil do Thunderbird como uma arvore de aplicativo (substitui, nao mescla — ver
/// especificacao secao 19, item 3). A parte que corrige o bug original (Thunderbird voltando
/// vazio apos a extracao) e <see cref="RebindDefaultProfileAsync"/>: o profiles.ini recuperado
/// e reescrito para que a secao Install da instalacao NOVA (destino) aponte para o perfil
/// recuperado, em vez de manter o Install ID da instalacao antiga (que a instalacao nova ignora).
/// </summary>
public sealed class ThunderbirdMigrationModule(
    IFileSystemAdapter fileSystem,
    IProcessAdapter processAdapter) : IMigrationModule
{
    private const string ProfilesIniFileName = "profiles.ini";
    private const string PackageModuleSubpath = "modules/thunderbird/root";
    public static readonly string[] ProcessNames = ["thunderbird"];

    /// <summary>
    /// Caminho do AppData deste app, relativo a raiz do perfil — publico para que a politica de
    /// selecao generica (SelectionPolicyModule) saiba excluir esta subarvore da varredura de
    /// AppData nao classificado, evitando capturar os mesmos arquivos duas vezes.
    /// </summary>
    public static readonly IReadOnlyList<string> AppDataRelativeSegments =
        ["AppData", "Roaming", "Thunderbird"];

    public string Id => "thunderbird";

    public async Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var thunderbirdRoot = Path.Combine([context.SourceRoot, .. AppDataRelativeSegments]);
        var profilesIniPath = Path.Combine(thunderbirdRoot, ProfilesIniFileName);
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
            ["thunderbirdRoot"] = thunderbirdRoot,
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
            !discovery.Facts.TryGetValue("thunderbirdRoot", out var thunderbirdRoot) ||
            thunderbirdRoot is null)
        {
            yield break;
        }

        var sequence = 0L;
        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(thunderbirdRoot, ct)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(thunderbirdRoot, entry.FullPath).Replace('\\', '/');
            sequence++;
            yield return new TransferItem(
                sequence,
                Id,
                entry.FullPath,
                $"{PackageModuleSubpath}/{relative}",
                TransferClassification.ModuleData,
                SourceRelativePath: relative,
                Metadata: new FileMetadata(entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Attributes),
                Length: entry.Length);
        }
    }

    public async Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Decisao Q64: encerrar a forca ja na primeira etapa, antes de qualquer outra coisa —
        // nao tentar fechamento gracioso primeiro. O Thunderbird pode reescrever prefs.js ou
        // manter arquivos de mensagens abertos ao sair, o que corromperia a troca de arvore
        // se ainda estivesse rodando durante a copia/staging.
        await processAdapter.TerminateProcessTreesAsync(ProcessNames, ct).ConfigureAwait(false);

        var destinationRoot = Path.Combine(
            [context.DestinationProfileRoot, .. AppDataRelativeSegments]);
        var packageModulePath = Path.Combine(
            context.PackageRoot,
            "modules", "thunderbird", "root");

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

        // Existir nao basta: um prefs.js vazio ou so com BOM passaria pela checagem de presenca
        // sozinho, mas e evidencia clara de copia truncada/corrompida, nao de um perfil valido.
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
                "thunderbird-profile-active",
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
