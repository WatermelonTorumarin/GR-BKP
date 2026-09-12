using System.Security.AccessControl;
using System.Security.Principal;
using GRBKP.Core.Discovery;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class WindowsProfileDiscoveryTests
{
    /// <summary>
    /// Mesma classe do bug que já apareceu em produção (a junction "Meus Vídeos" derrubava o
    /// backup inteiro): uma única pasta inacessível dentro de Users não pode impedir o técnico de
    /// enxergar os demais perfis. Aqui a pasta problemática tem leitura NEGADA por ACL, que é o
    /// caso real de um SSD antigo cujas permissões apontam para o SID de outra máquina.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_StillListsOtherProfiles_WhenOneFolderInsideUsersIsUnreadable()
    {
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var volumeRoot = fixture.PathFor("F");
        fixture.CreateProfile(volumeRoot, "auxatc", withAppData: true);
        fixture.CreateProfile(volumeRoot, "protegido", withAppData: true);
        fixture.CreateProfile(volumeRoot, "lucas.ferreira", withAppData: true);

        var protectedProfile = Path.Combine(volumeRoot, "Users", "protegido");
        DenyReadAccess(protectedProfile);
        try
        {
            var discovery = new WindowsProfileDiscovery(fileSystem);

            var profiles = await discovery.DiscoverAsync(volumeRoot, CancellationToken.None);

            Assert.Contains(profiles, profile => profile.Name == "auxatc");
            Assert.Contains(profiles, profile => profile.Name == "lucas.ferreira");
        }
        finally
        {
            RestoreAccess(protectedProfile);
        }
    }

    private static void DenyReadAccess(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.NoPropagateInherit,
            AccessControlType.Deny));
        directory.SetAccessControl(security);
    }

    private static void RestoreAccess(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        var security = directory.GetAccessControl();
        security.RemoveAccessRuleAll(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.NoPropagateInherit,
            AccessControlType.Deny));
        directory.SetAccessControl(security);
    }

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
    public async Task DiscoverAsync_ClassifiesPortugueseLocalizedSystemFolders_AsTemplateOrSystem()
    {
        // Reproduz um C:\Users real de Windows em pt-BR: essas juncoes localizadas nao estavam
        // na lista original (so inglesas), entao caiam incorretamente em "Suspicious".
        using var fixture = new TemporaryDirectory();
        var fileSystem = new WindowsFileSystemAdapter();
        var volumeRoot = fixture.PathFor("C");

        fixture.CreateProfile(volumeRoot, "Pasta Oficina", withAppData: true);
        fixture.CreateProfile(volumeRoot, "Todos os Usuários", withAppData: false);
        fixture.CreateProfile(volumeRoot, "Usuário Padrão", withAppData: false);

        var discovery = new WindowsProfileDiscovery(fileSystem);

        var profiles = await discovery.DiscoverAsync(volumeRoot, CancellationToken.None);
        var byName = profiles.ToDictionary(p => p.Name);

        Assert.Equal(ProfileClassification.RealProfile, byName["Pasta Oficina"].Classification);
        Assert.Equal(ProfileClassification.TemplateOrSystem, byName["Todos os Usuários"].Classification);
        Assert.Equal(ProfileClassification.TemplateOrSystem, byName["Usuário Padrão"].Classification);
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
