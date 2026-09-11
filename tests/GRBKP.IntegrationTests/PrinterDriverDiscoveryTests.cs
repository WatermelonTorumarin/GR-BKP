using GRBKP.Core.Discovery;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class PrinterDriverDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_FindsBrotherAndZebraPackages_IgnoringOtherVendors()
    {
        using var fixture = new TemporaryDirectory();
        var volumeRoot = fixture.PathFor("F");

        // Pacote Brother real observado num Windows de producao (ver docs/research-printer-drivers-brother-zebra.md).
        fixture.CreatePackage(
            volumeRoot,
            "brprm15a.inf_amd64_7036458cd876f71d",
            "oem23.inf",
            """
            [Version]
            Provider=%BR%
            [Strings]
            BR = "Brother Industries, Ltd."
            """);

        fixture.CreatePackage(
            volumeRoot,
            "zdesigner.inf_amd64_aa11bb22cc33dd44",
            "oem45.inf",
            """
            [Version]
            Provider=%ZebraTech%
            [Strings]
            ZebraTech = "Zebra Technologies"
            """);

        // Impressora de outro fabricante — nao deve entrar na lista, so nos interessam Brother/Zebra.
        fixture.CreatePackage(
            volumeRoot,
            "hpprint.inf_amd64_ffeeddccbbaa9988",
            "oem67.inf",
            """
            [Version]
            Provider=%HP%
            [Strings]
            HP = "HP Inc."
            """);

        var discovery = new PrinterDriverDiscovery(new WindowsFileSystemAdapter());

        var drivers = await discovery.DiscoverAsync(volumeRoot, CancellationToken.None);

        Assert.Equal(2, drivers.Count);
        var brother = Assert.Single(drivers, d => d.Vendor == PrinterDriverVendor.Brother);
        Assert.Equal("brprm15a.inf_amd64_7036458cd876f71d", brother.PackageFolderName);
        Assert.Equal("oem23.inf", brother.InfFileName);
        var zebra = Assert.Single(drivers, d => d.Vendor == PrinterDriverVendor.Zebra);
        Assert.Equal("zdesigner.inf_amd64_aa11bb22cc33dd44", zebra.PackageFolderName);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsEmpty_WhenDriverStoreDoesNotExist()
    {
        using var fixture = new TemporaryDirectory();
        var discovery = new PrinterDriverDiscovery(new WindowsFileSystemAdapter());

        var drivers = await discovery.DiscoverAsync(fixture.PathFor("SemDriverStore"), CancellationToken.None);

        Assert.Empty(drivers);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            System.IO.Path.GetTempPath(), "grbkp-printer-discovery", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string PathFor(string relative) =>
            System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void CreatePackage(
            string volumeRoot, string packageFolderName, string infFileName, string infContent)
        {
            var packagePath = System.IO.Path.Combine(
                volumeRoot, "Windows", "System32", "DriverStore", "FileRepository", packageFolderName);
            Directory.CreateDirectory(packagePath);
            File.WriteAllText(System.IO.Path.Combine(packagePath, infFileName), infContent);
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
