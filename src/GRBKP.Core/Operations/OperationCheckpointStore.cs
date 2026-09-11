using System.Text.Json;
using GRBKP.Core.Adapters;
using GRBKP.Core.Packages;

namespace GRBKP.Core.Operations;

public sealed class OperationCheckpointStore(IFileSystemAdapter fileSystem, IClock clock)
{
    public async Task SaveAsync(
        string operationRoot,
        OperationCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        await fileSystem.CreateDirectoryAsync(operationRoot, cancellationToken).ConfigureAwait(false);
        var destinationPath = Path.Combine(operationRoot, "operation.json");
        var temporaryPath = destinationPath + $".grbkp.partial.{checkpoint.OperationId:N}";
        var updated = checkpoint with { UpdatedAtUtc = clock.UtcNow };

        await using (var stream = await fileSystem.OpenWriteAsync(
            temporaryPath,
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                updated,
                BackupPackageModule.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationCheckpoint> ReadAsync(
        string operationRoot,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await fileSystem.OpenReadAsync(
            Path.Combine(operationRoot, "operation.json"),
            cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<OperationCheckpoint>(
            stream,
            BackupPackageModule.JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Checkpoint de restauração vazio.");
    }
}
