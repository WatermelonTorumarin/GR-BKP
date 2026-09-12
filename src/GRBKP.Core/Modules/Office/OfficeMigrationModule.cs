using System.Runtime.CompilerServices;
using GRBKP.Core.Adapters;
using GRBKP.Core.Restore;

namespace GRBKP.Core.Modules.Office;

/// <summary>
/// Migra dados do Office/Outlook como varias arvores INDEPENDENTES — nao um unico root como
/// Thunderbird/Chromium, porque assinaturas, modelos, dicionario e dados do Outlook ficam em
/// pastas separadas do AppData. Cada subarvore e capturada/restaurada por conta propria: se uma
/// nao existir na origem, o que ja estiver no destino permanece intocado (nunca apaga dados so
/// porque o Module nao encontrou aquele item especifico na origem).
///
/// Escopo desta versao (decisao do responsavel): Outlook (PST/OST + assinaturas), modelos do
/// Word/Excel/PowerPoint e dicionario/autocorrecao. Nao inclui arquivos recentes (lista MRU do
/// Office fica no registro, nao em arquivo — fora de escopo por enquanto) nem reconfiguracao de
/// conta do Outlook (o perfil/conta em si fica no registro, nao neste Module).
/// </summary>
public sealed class OfficeMigrationModule(
    IFileSystemAdapter fileSystem,
    IProcessAdapter processAdapter) : IMigrationModule
{
    public static readonly string[] ProcessNames = ["OUTLOOK", "WINWORD", "EXCEL", "POWERPNT"];

    private static readonly IReadOnlyDictionary<string, string> KnownRelativeTrees =
        new Dictionary<string, string>
        {
            ["signatures"] = "AppData/Roaming/Microsoft/Signatures",
            ["templates"] = "AppData/Roaming/Microsoft/Templates",
            ["uproof"] = "AppData/Roaming/Microsoft/UProof",
            ["outlook-data"] = "AppData/Local/Microsoft/Outlook"
        };

    /// <summary>
    /// Caminhos relativos (a raiz do perfil) das arvores que este Module ja captura por conta
    /// propria — usado pela politica de selecao generica para nao empacota-las duas vezes.
    /// </summary>
    public static IReadOnlyList<string> AppDataRelativeTrees { get; } =
        [.. KnownRelativeTrees.Values];

    public string Id => "office";

    public async Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var facts = new Dictionary<string, string?>();
        var anyFound = false;
        foreach (var (key, relativeTree) in KnownRelativeTrees)
        {
            var absolutePath = ResolveAbsolutePath(context.SourceRoot, relativeTree);
            var info = await context.FileSystem.GetInfoAsync(absolutePath, ct).ConfigureAwait(false);
            if (info?.Kind == PathEntryKind.Directory)
            {
                facts[key] = absolutePath;
                anyFound = true;
            }
        }

        return new ModuleDiscovery(Id, anyFound, facts);
    }

    public async IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
        ModuleDiscovery discovery,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (!discovery.IsApplicable)
        {
            yield break;
        }

        var sequence = 0L;
        foreach (var key in KnownRelativeTrees.Keys)
        {
            if (!discovery.Facts.TryGetValue(key, out var absoluteRoot) || absoluteRoot is null)
            {
                continue;
            }

            await foreach (var entry in fileSystem
                .EnumerateFilesAsync(absoluteRoot, ct)
                .ConfigureAwait(false))
            {
                var relative = Path.GetRelativePath(absoluteRoot, entry.FullPath).Replace('\\', '/');
                sequence++;
                yield return new TransferItem(
                    sequence,
                    Id,
                    entry.FullPath,
                    $"modules/office/{key}/{relative}",
                    TransferClassification.ModuleData,
                    // Os dados do Outlook são o item insubstituível de um perfil de trabalho:
                    // se o PST/OST corromper na cópia, ninguém percebe até precisar dele — com o
                    // SSD de origem já formatado. Vale o custo do hash (ver SelectionPolicyModule).
                    RequiresSha256: key == "outlook-data",
                    SourceRelativePath: relative,
                    Metadata: new FileMetadata(
                        entry.CreationTimeUtc, entry.LastWriteTimeUtc, entry.Attributes));
            }
        }
    }

    public async Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await processAdapter.TerminateProcessTreesAsync(ProcessNames, ct).ConfigureAwait(false);

        var evidence = new List<MigrationEvidence>();
        var appliedTrees = new List<string>();

        foreach (var (key, relativeTree) in KnownRelativeTrees)
        {
            var packageTreePath = Path.Combine(context.PackageRoot, "modules", "office", key);
            var packageInfo = await context.FileSystem.GetInfoAsync(packageTreePath, ct)
                .ConfigureAwait(false);
            if (packageInfo is null)
            {
                // Esta subarvore nao foi capturada (nao existia — ou estava vazia — na origem).
                // O que ja estiver no destino para este item continua intocado.
                continue;
            }

            var destinationPath = ResolveAbsolutePath(context.DestinationProfileRoot, relativeTree);
            var stagingPath = destinationPath + $".staging.{context.OperationId:N}";
            await CopyTreeAsync(context.FileSystem, packageTreePath, stagingPath, ct)
                .ConfigureAwait(false);

            // Subarvores independentes podem viver sob pais que ainda nao existem no perfil de
            // destino (ex.: "AppData/Local/Microsoft" quando so "AppData/Roaming/Microsoft" foi
            // tocado ate agora) — Directory.Move exige que o pai do destino ja exista.
            var destinationParent = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("Árvore de destino sem diretório pai.");
            await context.FileSystem.CreateDirectoryAsync(destinationParent, ct).ConfigureAwait(false);

            var subModuleId = $"office-{key}";
            var restorer = new ApplicationTreeRestorer(context.FileSystem);
            var swapResult = await restorer.RestoreAsync(
                new ModuleTreeRestoreRequest(context.OperationId, subModuleId, stagingPath, destinationPath),
                new SubtreeValidationModule(subModuleId),
                ct).ConfigureAwait(false);

            evidence.AddRange(swapResult.ModuleResult.Evidence);
            if (swapResult.ModuleResult.Outcome == ModuleOutcome.Failed)
            {
                return new ModuleResult(Id, ModuleOutcome.Failed, evidence, swapResult.ModuleResult.DiagnosticPath);
            }

            appliedTrees.Add(key);
        }

        return new ModuleResult(
            Id,
            appliedTrees.Count > 0 ? ModuleOutcome.Applied : ModuleOutcome.NotApplicable,
            evidence);
    }

    public Task<ValidationResult> ValidateAsync(ValidationContext context, CancellationToken ct) =>
        // Nunca chamado para o Module "office" em si — cada subarvore usa seu proprio
        // SubtreeValidationModule (Id "office-<chave>") dentro de RestoreAsync.
        Task.FromResult(new ValidationResult(true, []));

    private static string ResolveAbsolutePath(string profileRoot, string relativeTree) =>
        Path.Combine([profileRoot, .. relativeTree.Split('/')]);

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

    /// <summary>
    /// Valida uma unica subarvore apos a troca: precisa ter ao menos um arquivo com conteudo
    /// real (Length > 0) — a pasta so ser capturada com um arquivo de 0 bytes e evidencia de
    /// copia truncada/corrompida, nao de dado valido. Verifica por tamanho em bytes (nao
    /// conteudo de texto) porque estas pastas misturam texto (assinaturas HTML) e binario
    /// (PST/OST do Outlook).
    /// </summary>
    private sealed class SubtreeValidationModule(string id) : IMigrationModule
    {
        public string Id { get; } = id;

        public Task<ModuleDiscovery> DiscoverAsync(MigrationContext context, CancellationToken ct) =>
            throw new NotSupportedException("Usado apenas internamente para validar uma troca de árvore.");

        public IAsyncEnumerable<TransferItem> EnumerateBackupAsync(
            ModuleDiscovery discovery, CancellationToken ct) =>
            throw new NotSupportedException("Usado apenas internamente para validar uma troca de árvore.");

        public Task<ModuleResult> RestoreAsync(RestoreContext context, CancellationToken ct) =>
            throw new NotSupportedException("Usado apenas internamente para validar uma troca de árvore.");

        public async Task<ValidationResult> ValidateAsync(ValidationContext context, CancellationToken ct)
        {
            await foreach (var entry in context.FileSystem
                .EnumerateFilesAsync(context.DestinationPath, ct)
                .ConfigureAwait(false))
            {
                if (entry.Length > 0)
                {
                    return new ValidationResult(
                        true,
                        [new MigrationEvidence(
                            $"{Id}-restored", "Arquivo com conteúdo confirmado.", EvidenceSeverity.Information)]);
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
    }
}
