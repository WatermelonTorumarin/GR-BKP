using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;

namespace GRBKP.Core.Restore;

public sealed record ModuleTreeRestoreRequest(
    Guid OperationId,
    string ModuleId,
    string StagingPath,
    string DestinationPath);

public sealed record ModuleTreeRestoreResult(
    ModuleResult ModuleResult,
    string PreMigrationPath,
    string? FailedRestorePath);

public sealed class ApplicationTreeRestorer(IFileSystemAdapter fileSystem)
{
    public async Task<ModuleTreeRestoreResult> RestoreAsync(
        ModuleTreeRestoreRequest request,
        IMigrationModule module,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(module);
        if (!string.Equals(request.ModuleId, module.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("O Module não corresponde à solicitação.", nameof(module));
        }

        var stagingInfo = await fileSystem.GetInfoAsync(request.StagingPath, cancellationToken)
            .ConfigureAwait(false);
        if (stagingInfo?.Kind != PathEntryKind.Directory)
        {
            throw new DirectoryNotFoundException(
                $"A árvore de staging não existe: {request.StagingPath}");
        }

        var destinationParent = Path.GetDirectoryName(
            request.DestinationPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            ?? throw new InvalidOperationException("A árvore de destino não possui diretório pai.");
        var safeModuleId = EnsureSafeName(request.ModuleId);
        var operationSuffix = request.OperationId.ToString("N");
        var preMigrationPath = Path.Combine(
            destinationParent,
            $"{safeModuleId}.pre-migration.{operationSuffix}");
        var failedRestorePath = Path.Combine(
            destinationParent,
            $"{safeModuleId}.failed-restore.{operationSuffix}");

        if (await fileSystem.GetInfoAsync(preMigrationPath, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            throw new IOException($"Já existe uma árvore de proteção: {preMigrationPath}");
        }

        var destinationExisted = await fileSystem.GetInfoAsync(
            request.DestinationPath,
            cancellationToken).ConfigureAwait(false) is not null;
        if (destinationExisted)
        {
            await fileSystem.MoveAsync(
                request.DestinationPath,
                preMigrationPath,
                overwrite: false,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await fileSystem.MoveAsync(
                request.StagingPath,
                request.DestinationPath,
                overwrite: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (destinationExisted)
            {
                await fileSystem.MoveAsync(
                    preMigrationPath,
                    request.DestinationPath,
                    overwrite: false,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        ValidationResult validation;
        try
        {
            validation = await module.ValidateAsync(
                new ValidationContext(
                    request.OperationId,
                    request.DestinationPath,
                    fileSystem),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            validation = new ValidationResult(
                false,
                [new MigrationEvidence(
                    "module-validation-exception",
                    exception.Message,
                    EvidenceSeverity.Error)],
                "module-validation-exception");
        }

        if (validation.IsValid)
        {
            if (destinationExisted)
            {
                await fileSystem.DeleteAsync(
                    preMigrationPath,
                    recursive: true,
                    cancellationToken).ConfigureAwait(false);
            }

            return new ModuleTreeRestoreResult(
                new ModuleResult(module.Id, ModuleOutcome.Applied, validation.Evidence),
                preMigrationPath,
                null);
        }

        if (await fileSystem.GetInfoAsync(failedRestorePath, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            await fileSystem.DeleteAsync(
                failedRestorePath,
                recursive: true,
                cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(
            request.DestinationPath,
            failedRestorePath,
            overwrite: false,
            CancellationToken.None).ConfigureAwait(false);
        if (destinationExisted)
        {
            await fileSystem.MoveAsync(
                preMigrationPath,
                request.DestinationPath,
                overwrite: false,
                CancellationToken.None).ConfigureAwait(false);
        }

        return new ModuleTreeRestoreResult(
            new ModuleResult(
                module.Id,
                ModuleOutcome.Failed,
                validation.Evidence,
                failedRestorePath),
            preMigrationPath,
            failedRestorePath);
    }

    private static string EnsureSafeName(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId) ||
            moduleId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            moduleId is "." or "..")
        {
            throw new ArgumentException("Id de Module inválido.", nameof(moduleId));
        }

        return moduleId;
    }
}
