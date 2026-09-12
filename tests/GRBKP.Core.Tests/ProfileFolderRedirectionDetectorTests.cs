using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;

namespace GRBKP.Core.Tests;

public sealed class ProfileFolderRedirectionDetectorTests
{
    [Fact]
    public async Task DetectAsync_ReportsNothing_WhenEveryFolderIsInItsDefaultPlace()
    {
        var reader = FakeReaderWith(new Dictionary<string, string?>
        {
            ["Desktop"] = @"%USERPROFILE%\Desktop",
            ["Personal"] = @"%USERPROFILE%\Documents",
            ["{374DE290-123F-4565-9164-39C4925E467B}"] = @"%USERPROFILE%\Downloads",
            ["My Pictures"] = @"%USERPROFILE%\Pictures"
        });
        var detector = new ProfileFolderRedirectionDetector(reader);

        var redirected = await detector.DetectAsync(@"F:\Users\auxatc", CancellationToken.None);

        Assert.Empty(redirected);
    }

    [Fact]
    public async Task DetectAsync_ReportsFoldersMovedIntoOneDrive()
    {
        // Caso real e perigoso: com "Backup de pastas" do OneDrive ligado, Documentos e Área de
        // Trabalho passam a morar dentro da pasta do OneDrive. A varredura padrão do app olha
        // <perfil>\Documents, que fica vazia — e o backup sairia SEM os documentos, calado.
        var reader = FakeReaderWith(new Dictionary<string, string?>
        {
            ["Desktop"] = @"C:\Users\auxatc\OneDrive - Ravel\Área de Trabalho",
            ["Personal"] = @"C:\Users\auxatc\OneDrive - Ravel\Documentos",
            ["{374DE290-123F-4565-9164-39C4925E467B}"] = @"%USERPROFILE%\Downloads",
            ["My Pictures"] = @"%USERPROFILE%\Pictures"
        });
        var detector = new ProfileFolderRedirectionDetector(reader);

        var redirected = await detector.DetectAsync(@"F:\Users\auxatc", CancellationToken.None);

        Assert.Equal(2, redirected.Count);
        Assert.Contains(redirected, folder => folder.FolderName == "Documentos");
        Assert.Contains(redirected, folder => folder.FolderName == "Área de Trabalho");
        Assert.Contains(redirected, folder =>
            folder.ConfiguredPath.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ReportsFoldersRedirectedToANetworkShare()
    {
        var reader = FakeReaderWith(new Dictionary<string, string?>
        {
            ["Desktop"] = @"%USERPROFILE%\Desktop",
            ["Personal"] = @"\\servidor\perfis\auxatc\Documentos",
            ["{374DE290-123F-4565-9164-39C4925E467B}"] = @"%USERPROFILE%\Downloads",
            ["My Pictures"] = @"%USERPROFILE%\Pictures"
        });
        var detector = new ProfileFolderRedirectionDetector(reader);

        var redirected = await detector.DetectAsync(@"F:\Users\auxatc", CancellationToken.None);

        var folder = Assert.Single(redirected);
        Assert.Equal("Documentos", folder.FolderName);
        Assert.StartsWith(@"\\servidor", folder.ConfiguredPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectAsync_ReportsNothing_WhenTheHiveCannotBeRead()
    {
        // Nunca pode impedir um backup só porque a detecção falhou (hive travado por ser o perfil
        // logado agora, arquivo corrompido, formato inesperado).
        var detector = new ProfileFolderRedirectionDetector(new ThrowingReader());

        var redirected = await detector.DetectAsync(@"F:\Users\auxatc", CancellationToken.None);

        Assert.Empty(redirected);
    }

    [Fact]
    public async Task DetectAsync_ReportsNothing_WhenTheKeyIsAbsent()
    {
        var detector = new ProfileFolderRedirectionDetector(new EmptyReader());

        var redirected = await detector.DetectAsync(@"F:\Users\auxatc", CancellationToken.None);

        Assert.Empty(redirected);
    }

    private static IOfflineRegistryHiveReader FakeReaderWith(Dictionary<string, string?> values) =>
        new FakeReader(values);

    private sealed class FakeReader(Dictionary<string, string?> values) : IOfflineRegistryHiveReader
    {
        public Task<IReadOnlyList<OfflineRegistrySubKey>> EnumerateSubKeysAsync(
            string hiveFilePath,
            string keyPath,
            IReadOnlyCollection<string> valueNames,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OfflineRegistrySubKey>>(
            [
                new OfflineRegistrySubKey("Advanced", new Dictionary<string, string?>()),
                new OfflineRegistrySubKey("User Shell Folders", values)
            ]);
    }

    private sealed class ThrowingReader : IOfflineRegistryHiveReader
    {
        public Task<IReadOnlyList<OfflineRegistrySubKey>> EnumerateSubKeysAsync(
            string hiveFilePath,
            string keyPath,
            IReadOnlyCollection<string> valueNames,
            CancellationToken cancellationToken = default) =>
            throw new IOException("O hive está sendo usado por outro processo.");
    }

    private sealed class EmptyReader : IOfflineRegistryHiveReader
    {
        public Task<IReadOnlyList<OfflineRegistrySubKey>> EnumerateSubKeysAsync(
            string hiveFilePath,
            string keyPath,
            IReadOnlyCollection<string> valueNames,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OfflineRegistrySubKey>>([]);
    }
}
