using GRBKP.Core.Discovery;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class WindowsProfileDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_ClassifiesRealProfiles_TemplatesAndSuspiciousFoldersCorrectly()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var volumeRoot = fixture.PathFor("F");

        // Perfis reais: tem AppData, sao funcionarios de verdade.
        fixture.CreateProfile(volumeRoot, "auxatc", withAppData: true);
        fixture.CreateProfile(volumeRoot, "lucas.ferreira", withAppData: true);

        // Perfis-molde/sistema: nomes que o Windows usa internamente, nunca sao funcionarios.
        fixture.CreateProfile(volumeRoot, "Default", withAppData: true);
        fixture.CreateProfile(volumeRoot, "Public", withAppData: false);
        fixture.CreateProfile(volumeRoot, "All Users", withAppData: false);

        // Pasta estranha dentro de Users sem AppData — nao parece um perfil real, precisa de
        // revisao manual do tecnico, nao deve ser tratada como um funcionario automaticamente.
        fixture.CreateProfile(volumeRoot, "backup_antigo", withAppData: false);

        var discovery = new WindowsProfileDiscovery(fileSystem);

        var profiles = await discovery.DiscoverAsync(volumeRoot, CancellationToken.None);

        var byName = profiles.ToDictionary(p => p.Name);
        Assert.Equal(6, profiles.Count);

        Assert.Equal(ProfileClassification.RealProfile, byName["auxatc"].Classification);
        Assert.Equal(ProfileClassification.RealProfile, byName["lucas.ferreira"].Classification);

        Assert.Equal(ProfileClassification.TemplateOrSystem, byName["Default"].Classification);
        Assert.Equal(ProfileClassification.TemplateOrSystem, byName["Public"].Classification);
        Assert.Equal(ProfileClassification.TemplateOrSystem, byName["All Users"].Classification);

        Assert.Equal(ProfileClassification.Suspicious, byName["backup_antigo"].Classification);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsEmpty_WhenUsersFolderDoesNotExist()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var discovery = new WindowsProfileDiscovery(fileSystem);

        var profiles = await discovery.DiscoverAsync(
            fixture.PathFor("volume-sem-users"), CancellationToken.None);

        Assert.Empty(profiles);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(),
            "grbkp-profile-discovery",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void CreateProfile(string volumeRoot, string name, bool withAppData)
        {
            var profilePath = System.IO.Path.Combine(volumeRoot, "Users", name);
            Directory.CreateDirectory(profilePath);
            if (withAppData)
            {
                Directory.CreateDirectory(System.IO.Path.Combine(profilePath, "AppData"));
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
