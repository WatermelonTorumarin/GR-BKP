using System.Runtime.CompilerServices;
using System.Text.Json;
using GRBKP.Core.Adapters;
using GRBKP.Core.Restore;

namespace GRBKP.Core.Modules.Chromium;

/// <summary>
/// Migra a pasta "User Data" inteira de um navegador Chromium (Chrome/Edge) como uma arvore de
/// aplicativo (substitui, nao mescla). Diferente do Thunderbird/Firefox, o Chromium nao tem um
/// mecanismo de Install ID por instalacao — a pasta inteira pode ser trocada diretamente, sem
/// nenhuma reescrita de configuracao. Por decisao explicita (Q47), senhas/cookies/sessoes podem
/// ser perdidos sem impedir a restauracao — favoritos, historico e configuracoes sao o que
/// realmente precisa sobreviver.
/// </summary>
public sealed class ChromiumMigrationModule(
    IFileSystemAdapter fileSystem,
    IProcessAdapter processAdapter,
    ChromiumBrowserDescriptor descriptor) : IMigrationModule
{
    private const string LocalStateFileName = "Local State";

    /// <summary>
    /// Subpastas conhecidas de cache do Chromium — nunca fazem parte do backup. Nao sao dados do
    /// usuario, apenas conteudo reconstruivel, e podem ser proporcionalmente enormes comparadas
    /// ao que realmente importa (favoritos, senhas, historico, configuracoes).
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Cache2", "Code Cache", "GPUCache", "Service Worker", "CacheStorage",
        "Crashpad", "GrShaderCache", "ShaderCache", "blob_storage", "Media Cache",
        "component_crx_cache", "extensions_crx_cache"
    };

    public string Id => descriptor.ModuleId;

    public async Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userDataRoot = ResolveUserDataRoot(context.SourceRoot);
        var localStatePath = Path.Combine(userDataRoot, LocalStateFileName);
        var info = await context.FileSystem.GetInfoAsync(localStatePath, ct).ConfigureAwait(false);
        var facts = new Dictionary<string, string?> { ["userDataRoot"] = userDataRoot };
        return new ModuleDiscovery(Id, info is not null, facts);
    }

    public async IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
        ModuleDiscovery discovery,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (!discovery.IsApplicable ||
            !discovery.Facts.TryGetValue("userDataRoot", out var userDataRoot) ||
            userDataRoot is null)
        {
            yield break;
        }

        var sequence = 0L;
        await foreach (var entry in fileSystem
            .EnumerateFilesAsync(userDataRoot, ct)
            .ConfigureAwait(false))
        {
            var relative = Path.GetRelativePath(userDataRoot, entry.FullPath).Replace('\\', '/');
            if (relative.Split('/').Any(segment => ExcludedDirectoryNames.Contains(segment)))
            {
                continue;
            }

            sequence++;
            yield return new TransferItem(
                sequence,
                Id,
                entry.FullPath,
                $"modules/{descriptor.ModuleId}/root/{relative}",
                TransferClassification.ModuleData,
                SourceRelativePath: relative,
                Metadata: new FileMetadata(entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Attributes),
                Length: entry.Length);
        }
    }

    public async Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await processAdapter
            .TerminateProcessTreesAsync([descriptor.ProcessName], ct)
            .ConfigureAwait(false);

        var destinationRoot = ResolveUserDataRoot(context.DestinationProfileRoot);
        var packageModulePath = Path.Combine(context.PackageRoot, "modules", descriptor.ModuleId, "root");

        var packageInfo = await context.FileSystem.GetInfoAsync(packageModulePath, ct)
            .ConfigureAwait(false);
        if (packageInfo is null)
        {
            return new ModuleResult(Id, ModuleOutcome.NotApplicable, []);
        }

        var stagingRoot = destinationRoot + $".staging.{context.OperationId:N}";
        await CopyTreeAsync(context.FileSystem, packageModulePath, stagingRoot, ct).ConfigureAwait(false);

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

        var localStatePath = Path.Combine(context.DestinationPath, LocalStateFileName);
        var info = await context.FileSystem.GetInfoAsync(localStatePath, ct).ConfigureAwait(false);
        if (info is null || info.Length == 0)
        {
            return Invalid(
                "local-state-missing",
                "\"Local State\" ausente ou vazio após a restauração.");
        }

        var localStateContent = await ReadAllTextAsync(context.FileSystem, localStatePath, ct)
            .ConfigureAwait(false);
        var profileDirectoryNames = TryGetProfileDirectoryNames(localStateContent);
        if (profileDirectoryNames is null)
        {
            return Invalid(
                "local-state-corrupted",
                "\"Local State\" não pôde ser interpretado como JSON válido.");
        }

        // "Local State" existir nao basta: ele referencia perfis em profile.info_cache, e cada
        // um precisa realmente existir com uma Preferences legivel — do contrario e evidencia de
        // backup parcial/corrompido, nao de um User Data valido.
        var activeProfiles = new List<string>();
        foreach (var profileDirectoryName in profileDirectoryNames)
        {
            var preferencesPath = Path.Combine(
                context.DestinationPath,
                profileDirectoryName,
                "Preferences");
            var preferencesInfo = await context.FileSystem.GetInfoAsync(preferencesPath, ct)
                .ConfigureAwait(false);
            if (preferencesInfo is null || preferencesInfo.Length == 0)
            {
                return Invalid(
                    "profile-incomplete",
                    $"Preferences ausente ou vazio para o perfil \"{profileDirectoryName}\" " +
                    "referenciado em \"Local State\".");
            }

            activeProfiles.Add(profileDirectoryName);
        }

        if (activeProfiles.Count == 0)
        {
            return Invalid(
                "no-profiles-referenced",
                "\"Local State\" não referencia nenhum perfil em profile.info_cache.");
        }

        return new ValidationResult(
            true,
            [new MigrationEvidence(
                $"{descriptor.ModuleId}-user-data-active",
                $"Perfis ativos após a restauração: {string.Join(", ", activeProfiles)}. " +
                "Senhas, cookies e sessões podem não ter sido preservados (aceito por decisão " +
                "do responsável) — favoritos, histórico e configurações foram.",
                EvidenceSeverity.Information)]);
    }

    private static ValidationResult Invalid(string code, string message) => new(
        false,
        [new MigrationEvidence(code, message, EvidenceSeverity.Error)],
        code);

    /// <summary>
    /// Le as chaves de <c>profile.info_cache</c> de um "Local State" do Chromium — cada chave e
    /// o nome da subpasta do perfil dentro de User Data (ex.: "Default", "Profile 1"). Retorna
    /// null se o conteudo nao for JSON valido (backup corrompido), lista vazia se for valido mas
    /// nao referenciar nenhum perfil.
    /// </summary>
    private static List<string>? TryGetProfileDirectoryNames(string localStateContent)
    {
        try
        {
            using var document = JsonDocument.Parse(localStateContent);
            if (!document.RootElement.TryGetProperty("profile", out var profileElement) ||
                !profileElement.TryGetProperty("info_cache", out var infoCacheElement) ||
                infoCacheElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return infoCacheElement.EnumerateObject().Select(property => property.Name).ToList();
        }
        catch (JsonException)
        {
            return null;
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

    private string ResolveUserDataRoot(string profileRoot) =>
        Path.Combine([profileRoot, .. descriptor.AppDataRelativeSegments]);

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
}
