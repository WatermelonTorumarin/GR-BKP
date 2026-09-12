using GRBKP.Core.Discovery;
using GRBKP.Windows.Registry;

namespace GRBKP.IntegrationTests;

public sealed class ProfileFolderRedirectionDetectorIntegrationTests
{
    /// <summary>
    /// Cadeia completa e real (leitor de hive de verdade + NTUSER.DAT de verdade): o perfil
    /// "Default" do Windows tem todas as pastas pessoais nos lugares padrão, então a detecção
    /// NÃO pode acusar redirecionamento nenhum. É a trava contra falso positivo — um aviso
    /// errado em toda restauração treinaria o técnico a ignorar o aviso justamente quando ele
    /// for verdadeiro.
    /// </summary>
    [Fact]
    public async Task DetectAsync_FindsNoRedirection_InARealUntouchedWindowsProfile()
    {
        var defaultHivePath = @"C:\Users\Default\NTUSER.DAT";
        if (!File.Exists(defaultHivePath))
        {
            return;
        }

        var profileRoot = Path.Combine(
            Path.GetTempPath(), $"grbkp-perfil-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileRoot);
        File.Copy(defaultHivePath, Path.Combine(profileRoot, "NTUSER.DAT"));
        try
        {
            var detector = new ProfileFolderRedirectionDetector(new OfflineRegistryHiveReader());

            var redirected = await detector.DetectAsync(profileRoot, CancellationToken.None);

            Assert.Empty(redirected);
        }
        finally
        {
            Directory.Delete(profileRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_ReturnsEmpty_WhenTheProfileHasNoHiveAtAll()
    {
        var profileRoot = Path.Combine(Path.GetTempPath(), $"grbkp-sem-hive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileRoot);
        try
        {
            var detector = new ProfileFolderRedirectionDetector(new OfflineRegistryHiveReader());

            var redirected = await detector.DetectAsync(profileRoot, CancellationToken.None);

            Assert.Empty(redirected);
        }
        finally
        {
            Directory.Delete(profileRoot, recursive: true);
        }
    }
}
