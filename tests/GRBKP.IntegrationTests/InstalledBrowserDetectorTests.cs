using GRBKP.Core.Discovery;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

/// <summary>
/// Prova a detecção de navegador que dispara o aviso de senhas no backup, contra o sistema de
/// arquivos real e com as mesmas constantes de caminho que os Modules de migração usam.
/// </summary>
public sealed class InstalledBrowserDetectorTests
{
    [Fact]
    public async Task DetectAsync_FindsEachBrowserByItsDataFolder()
    {
        using var fixture = new TemporaryDirectory();
        var perfil = fixture.CreateTree("Users/marcelo");
        fixture.CreateTree("Users/marcelo/AppData/Local/Microsoft/Edge/User Data/Default");
        fixture.CreateTree("Users/marcelo/AppData/Local/Google/Chrome/User Data/Default");

        var detector = new InstalledBrowserDetector(new WindowsFileSystemAdapter());
        var encontrados = await detector.DetectAsync(perfil, CancellationToken.None);

        Assert.Contains(encontrados, browser => browser.ModuleId == "edge");
        Assert.Contains(encontrados, browser => browser.ModuleId == "chrome");
        Assert.DoesNotContain(encontrados, browser => browser.ModuleId == "firefox");
    }

    [Fact]
    public async Task DetectAsync_FindsFirefox_ByItsRoamingFolder()
    {
        using var fixture = new TemporaryDirectory();
        var perfil = fixture.CreateTree("Users/marcelo");
        fixture.CreateTree("Users/marcelo/AppData/Roaming/Mozilla/Firefox/Profiles");

        var detector = new InstalledBrowserDetector(new WindowsFileSystemAdapter());
        var encontrados = await detector.DetectAsync(perfil, CancellationToken.None);

        Assert.Equal("firefox", Assert.Single(encontrados).ModuleId);
    }

    [Fact]
    public async Task DetectAsync_FindsNothing_InAProfileWithoutBrowsers()
    {
        using var fixture = new TemporaryDirectory();
        var perfil = fixture.CreateTree("Users/semNavegador");
        fixture.CreateTree("Users/semNavegador/Documents");

        var detector = new InstalledBrowserDetector(new WindowsFileSystemAdapter());
        var encontrados = await detector.DetectAsync(perfil, CancellationToken.None);

        Assert.Empty(encontrados);
    }

    [Fact]
    public async Task DetectAsync_DoesNotMistakeAFileForTheBrowserFolder()
    {
        // Robustez: se existir um ARQUIVO exatamente no caminho esperado da pasta do navegador,
        // isso não é uma instalação — a detecção só conta diretório.
        using var fixture = new TemporaryDirectory();
        var perfil = fixture.CreateTree("Users/ana");
        fixture.CreateTree("Users/ana/AppData/Local/Microsoft/Edge");
        fixture.WriteFile("Users/ana/AppData/Local/Microsoft/Edge/User Data", "não sou pasta");

        var detector = new InstalledBrowserDetector(new WindowsFileSystemAdapter());
        var encontrados = await detector.DetectAsync(perfil, CancellationToken.None);

        Assert.Empty(encontrados);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-navegador", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string CreateTree(string relative)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            return path;
        }

        public void WriteFile(string relative, string content)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // limpeza de melhor esforço
            }
        }
    }
}
