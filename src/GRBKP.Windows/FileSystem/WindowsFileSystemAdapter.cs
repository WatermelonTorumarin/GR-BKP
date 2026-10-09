using System.Runtime.CompilerServices;
using GRBKP.Core.Adapters;

namespace GRBKP.Windows.FileSystem;

public sealed class WindowsFileSystemAdapter : IFileSystemAdapter
{
    public async IAsyncEnumerable<FileSystemEntry> EnumerateFilesAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        var yielded = 0;
        foreach (var path in EnumerateFilePathsRecursive(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            long length;
            DateTime creationUtc;
            DateTime lastWriteUtc;
            FileAttributes attributes;
            try
            {
                info = new FileInfo(path);
                length = info.Length;
                creationUtc = info.CreationTimeUtc;
                lastWriteUtc = info.LastWriteTimeUtc;
                attributes = info.Attributes;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Arquivo pode ter sido removido/bloqueado entre a listagem e a leitura de
                // metadados (ex.: antivirus, sincronizacao). Um arquivo nao deve derrubar todo
                // o backup — fica de fora desta enumeracao, sem interromper o restante.
                continue;
            }

            yield return new FileSystemEntry(
                info.FullName,
                length,
                creationUtc,
                lastWriteUtc,
                attributes,
                IsCloudPlaceholder: IsCloudPlaceholder(attributes),
                IsReparsePoint: attributes.HasFlag(FileAttributes.ReparsePoint));

            yielded++;
            if ((yielded & 255) == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Caminha a arvore manualmente (em vez de <c>Directory.EnumerateFiles(..., RecurseSubdirectories: true)</c>)
    /// por dois motivos observados num Windows real em pt-BR: (1) pastas como "Meus Vídeos" dentro
    /// de Documentos sao junctions/reparse points que derrubavam o backup inteiro com
    /// UnauthorizedAccessException ao serem varridas — nunca devem ser seguidas, so ignoradas;
    /// (2) uma unica subpasta sem permissao de leitura (ACL, antivirus) nao deve interromper a
    /// enumeracao do resto da arvore.
    /// </summary>
    private static IEnumerable<string> EnumerateFilePathsRecursive(string directory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        foreach (var file in files)
        {
            yield return file;
        }

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            subdirectories = [];
        }

        foreach (var subdirectory in subdirectories)
        {
            bool isReparsePoint;
            try
            {
                isReparsePoint = new DirectoryInfo(subdirectory).Attributes
                    .HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (isReparsePoint)
            {
                continue;
            }

            foreach (var file in EnumerateFilePathsRecursive(subdirectory))
            {
                yield return file;
            }
        }
    }

    public async IAsyncEnumerable<PathEntryInfo> EnumerateDirectoriesAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateDirectories(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(path);
            yield return new PathEntryInfo(
                info.FullName,
                PathEntryKind.Directory,
                0,
                info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        await Task.CompletedTask;
    }

    public ValueTask<Stream> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public ValueTask<Stream> OpenWriteAsync(
        string path,
        FileWriteMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileMode = mode switch
        {
            FileWriteMode.CreateNew => FileMode.CreateNew,
            FileWriteMode.CreateOrOverwrite => FileMode.Create,
            FileWriteMode.Append => FileMode.Append,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        Stream stream = new FileStream(
            path,
            fileMode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public ValueTask CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PathEntryInfo?> GetInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return ValueTask.FromResult<PathEntryInfo?>(new PathEntryInfo(
                info.FullName,
                PathEntryKind.File,
                info.Length,
                info.Attributes.HasFlag(FileAttributes.ReparsePoint)));
        }

        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            return ValueTask.FromResult<PathEntryInfo?>(new PathEntryInfo(
                info.FullName,
                PathEntryKind.Directory,
                0,
                info.Attributes.HasFlag(FileAttributes.ReparsePoint)));
        }

        return ValueTask.FromResult<PathEntryInfo?>(null);
    }

    public ValueTask MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(sourcePath))
        {
            if (overwrite)
            {
                // O Windows recusa sobrescrever um arquivo somente-leitura, mesmo elevado. Isso
                // acontece o tempo todo num perfil real (desktop.ini, documentos marcados como
                // somente-leitura) e principalmente ao repetir/retomar uma restauração, já que a
                // cópia anterior restaurou os atributos originais no destino.
                ClearReadOnly(destinationPath);
            }

            File.Move(sourcePath, destinationPath, overwrite);
            return ValueTask.CompletedTask;
        }

        if (Directory.Exists(sourcePath))
        {
            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException($"O destino já existe: {destinationPath}");
                }

                DeleteDirectoryTree(destinationPath, recursive: true);
            }

            Directory.Move(sourcePath, destinationPath);
            return ValueTask.CompletedTask;
        }

        throw new FileNotFoundException("Origem da movimentação não encontrada.", sourcePath);
    }

    public ValueTask DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            ClearReadOnly(path);
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            DeleteDirectoryTree(path, recursive);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Apaga uma árvore garantindo que arquivos somente-leitura não interrompam a remoção —
    /// <c>Directory.Delete(recursive: true)</c> falha com UnauthorizedAccessException no primeiro
    /// arquivo somente-leitura que encontra, o que derrubaria a limpeza da pasta de proteção
    /// pré-migração depois de uma troca de árvore bem-sucedida.
    /// </summary>
    private static void DeleteDirectoryTree(string path, bool recursive)
    {
        if (recursive)
        {
            ClearReadOnlyRecursive(path);
        }

        Directory.Delete(path, recursive);
    }

    private static void ClearReadOnlyRecursive(string directory)
    {
        foreach (var file in EnumerateFilePathsRecursive(directory))
        {
            ClearReadOnly(file);
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // O caminho pode não existir (caso comum: sobrescrever onde ainda não há nada) ou
            // estar inacessível; quem chamou trata a falha real da operação seguinte.
        }
    }

    public ValueTask<FileSystemCapabilities> GetCapabilitiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"Não foi possível determinar o volume de {path}.");
        var isRemote = new Uri(root).IsUnc;
        string format;
        try
        {
            format = new DriveInfo(root).DriveFormat;
        }
        catch (IOException) when (isRemote)
        {
            format = "SMB";
        }

        var supportsLargeFiles = !format.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(new FileSystemCapabilities(
            format,
            supportsLargeFiles,
            SupportsAtomicRename: true,
            IsRemote: isRemote));
    }

    /// <summary>
    /// Ajusta datas e atributos do arquivo copiado. Eram três chamadas que abriam e fechavam um
    /// handle cada — medido em 604 ms a cada 3.000 arquivos, num perfil com centenas de milhares
    /// deles. Agora as duas datas vão por um ÚNICO handle já aberto, e os atributos só são
    /// gravados quando há algo que valha preservar.
    /// </summary>
    public ValueTask SetMetadataAsync(
        string path,
        FileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using (var handle = File.OpenHandle(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            File.SetCreationTimeUtc(handle, metadata.CreationTimeUtc.UtcDateTime);
            File.SetLastWriteTimeUtc(handle, metadata.LastWriteTimeUtc.UtcDateTime);
        }

        // Um arquivo recém-criado já nasce com "Arquivo morto" (Archive). Só vale a chamada
        // quando o original tinha algo além disso — Oculto, Somente leitura, Sistema — que
        // precisa mesmo ser preservado.
        var desejados = metadata.Attributes & ~FileAttributes.ReparsePoint;
        if ((desejados & ~(FileAttributes.Archive | FileAttributes.Normal)) != 0)
        {
            File.SetAttributes(path, desejados);
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsCloudPlaceholder(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Offline) ||
        attributes.HasFlag(FileAttributes.ReparsePoint) &&
        attributes.HasFlag(FileAttributes.SparseFile);
}
