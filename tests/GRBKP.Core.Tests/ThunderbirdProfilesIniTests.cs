using GRBKP.Core.Modules.Thunderbird;

namespace GRBKP.Core.Tests;

public sealed class ThunderbirdProfilesIniTests
{
    private const string LegacyProfilesIni = """
        [General]
        StartWithLastProfile=1
        Version=2

        [Profile0]
        Name=default
        IsRelative=1
        Path=Profiles/78ypv6h6.default-release
        Default=1

        [Install4A1B2C3D4E5F6789]
        Default=Profiles/78ypv6h6.default-release
        Locked=1
        """;

    private const string LegacyProfilesIniWithoutInstallSection = """
        [General]
        StartWithLastProfile=1
        Version=2

        [Profile0]
        Name=default
        IsRelative=1
        Path=Profiles/78ypv6h6.default-release
        Default=1
        """;

    [Fact]
    public void ResolveDefaultProfilePath_PrefersInstallScopedDefault_OverLegacyProfileFlag()
    {
        var ini = ThunderbirdProfilesIni.Parse(LegacyProfilesIni);

        Assert.Equal("Profiles/78ypv6h6.default-release", ini.ResolveDefaultProfilePath());
        Assert.Equal("Install4A1B2C3D4E5F6789", ini.FindInstallSectionName());
    }

    [Fact]
    public void ResolveDefaultProfilePath_FallsBackToLegacyProfileFlag_WhenNoInstallSectionExists()
    {
        var ini = ThunderbirdProfilesIni.Parse(LegacyProfilesIniWithoutInstallSection);

        Assert.Equal("Profiles/78ypv6h6.default-release", ini.ResolveDefaultProfilePath());
        Assert.Null(ini.FindInstallSectionName());
    }

    [Fact]
    public void WithInstallDefault_RebindsExistingInstallSection_ToRecoveredProfile_PreservingProfileSections()
    {
        // O bug original: o profiles.ini recuperado do SSD antigo aponta para o Install ID da
        // instalacao ANTIGA do Thunderbird. A instalacao NOVA (destino) tem seu proprio Install ID
        // e ignora esse Default antigo, abrindo um perfil vazio. A correcao precisa reescrever a
        // secao Install usando o ID que a instalacao NOVA ja usa, apontando para o perfil recuperado.
        var recovered = ThunderbirdProfilesIni.Parse(LegacyProfilesIni);
        var newInstallId = "Install9F8E7D6C5B4A3210";

        var rebound = recovered.WithInstallDefault(newInstallId, "Profiles/78ypv6h6.default-release");

        Assert.Equal("Profiles/78ypv6h6.default-release", rebound.ResolveDefaultProfilePath());
        Assert.Equal(newInstallId, rebound.FindInstallSectionName());

        var reparsed = ThunderbirdProfilesIni.Parse(rebound.Serialize());
        Assert.Equal("Profiles/78ypv6h6.default-release", reparsed.ResolveDefaultProfilePath());
        Assert.Equal(newInstallId, reparsed.FindInstallSectionName());
        Assert.Contains("Profile0", rebound.Serialize());
        Assert.DoesNotContain("Install4A1B2C3D4E5F6789", rebound.Serialize());
    }
}
