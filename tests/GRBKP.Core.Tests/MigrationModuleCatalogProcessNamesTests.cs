using GRBKP.Core.Modules;

namespace GRBKP.Core.Tests;

public sealed class MigrationModuleCatalogProcessNamesTests
{
    [Fact]
    public void KnownProcessNames_CoversEveryAppTheModulesMigrate()
    {
        var names = MigrationModuleCatalog.KnownProcessNames();

        Assert.Contains("thunderbird", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("firefox", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("chrome", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("msedge", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("OUTLOOK", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("WINWORD", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AcroRd32", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnownProcessNames_HasNoDuplicates()
    {
        var names = MigrationModuleCatalog.KnownProcessNames();

        Assert.Equal(
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            names.Count);
    }

    /// <summary>
    /// Trava contra esquecimento: quem adicionar um Module novo com processo próprio precisa
    /// aparecer aqui, senão o app deixaria de avisar que aquele programa está aberto durante um
    /// backup do disco em uso.
    /// </summary>
    [Fact]
    public void KnownProcessNames_HasOneEntryPerRegisteredModuleFamily()
    {
        var names = MigrationModuleCatalog.KnownProcessNames();

        Assert.Equal(10, names.Count);
    }
}
