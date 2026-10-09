using System.Security.Cryptography;
using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;

namespace GRBKP.Core.Packages;

public sealed record PackageVerificationIssue(
    string PackageRelativePath,
    string Code,
    string Detail);

public sealed record PackageVerificationReport(
    bool IsIntact,
    PackageState PackageStatus,
    long VerifiedFiles,
    long VerifiedBytes,
    long HashCheckedFiles,
    bool ManifestHashMatches,
    IReadOnlyList<PackageVerificationIssue> Issues);

/// <summary>
/// Responde à pergunta mais importante do fluxo: "posso formatar o SSD antigo?".
///
/// Até aqui não havia nenhuma forma de conferir um pacote depois de gravado — e o incidente que
/// originou este projeto foi exatamente um backup que parecia completo e não estava. A conferência
/// relê o pacote inteiro e cruza tudo com o manifesto:
///
/// 1. O pacote foi realmente finalizado (status "complete")?
/// 2. O manifesto está íntegro? (SHA-256 do files.jsonl contra o gravado em package.json)
/// 3. Todo arquivo listado existe, com o tamanho registrado?
/// 4. Os arquivos críticos (documentos, PST/OST) ainda batem com o hash gravado na cópia?
///
/// O item 4 é o único que pega corrupção silenciosa — bytes trocados por setor ruim, com o
/// tamanho intacto. Por isso ler o conteúdo só desses arquivos, e não de todos: é o mesmo
/// equilíbrio entre custo e garantia adotado na hora de copiar.
/// </summary>
public sealed class BackupPackageVerifier(
    IFileSystemAdapter fileSystem,
    BackupPackageModule packageModule)
{
    private const int BufferSize = 1024 * 1024;

    public async Task<PackageVerificationReport> VerifyAsync(
        string packageRoot,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);

        var metadata = await packageModule.ReadMetadataAsync(packageRoot, cancellationToken)
            .ConfigureAwait(false);
        var issues = new List<PackageVerificationIssue>();

        if (metadata.Status != PackageState.Complete)
        {
            issues.Add(new PackageVerificationIssue(
                "package.json",
                "package-not-complete",
                $"O backup está com status \"{metadata.Status}\": não foi finalizado. " +
                "Refaça o backup deste perfil antes de apagar a origem."));
        }

        var manifestHashMatches = await ManifestHashMatchesAsync(
            packageRoot, metadata.FilesManifestSha256, cancellationToken).ConfigureAwait(false);
        if (!manifestHashMatches)
        {
            issues.Add(new PackageVerificationIssue(
                "files.jsonl",
                "manifest-hash-mismatch",
                "A lista de arquivos do pacote foi alterada depois da gravação."));
        }

        long verifiedFiles = 0;
        long verifiedBytes = 0;
        long hashCheckedFiles = 0;

        // Uma linha ilegível vira achado do relatório, não exceção: a conferência existe
        // justamente para dizer o que está errado no pacote, e morrer no meio seria o oposto.
        var unreadableLines = new List<long>();
        await foreach (var entry in packageModule
            .ReadFilesAsync(packageRoot, unreadableLines, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = PackagePath.ResolveInside(packageRoot, entry.PackageRelativePath);
            var info = await fileSystem.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                issues.Add(new PackageVerificationIssue(
                    entry.PackageRelativePath,
                    "file-missing",
                    "Está na lista do backup, mas não existe dentro do pacote."));
                continue;
            }

            if (info.Length != entry.Length)
            {
                issues.Add(new PackageVerificationIssue(
                    entry.PackageRelativePath,
                    "size-mismatch",
                    $"Deveria ter {entry.Length} bytes e tem {info.Length}."));
                continue;
            }

            if (entry.Sha256 is not null)
            {
                var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new PackageVerificationIssue(
                        entry.PackageRelativePath,
                        "content-mismatch",
                        "O conteúdo mudou desde a cópia (tamanho igual, bytes diferentes)."));
                    continue;
                }

                hashCheckedFiles++;
            }

            verifiedFiles++;
            verifiedBytes += info.Length;
            progress?.Report(verifiedFiles);
        }

        foreach (var linha in unreadableLines)
        {
            issues.Add(new PackageVerificationIssue(
                "files.jsonl",
                "manifest-line-unreadable",
                $"A linha {linha} da lista de arquivos está ilegível — provavelmente o backup foi " +
                "interrompido. O arquivo que ela descrevia não poderá ser restaurado; todo o " +
                "resto do pacote continua utilizável."));
        }

        return new PackageVerificationReport(
            issues.Count == 0,
            metadata.Status,
            verifiedFiles,
            verifiedBytes,
            hashCheckedFiles,
            manifestHashMatches,
            issues);
    }

    private async Task<bool> ManifestHashMatchesAsync(
        string packageRoot,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            // Pacote gravado por uma versão que ainda não registrava o hash do manifesto: não há
            // o que comparar, e isso por si só não torna o pacote inválido.
            return true;
        }

        var actual = await ComputeSha256Async(
            Path.Combine(packageRoot, "files.jsonl"), cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
