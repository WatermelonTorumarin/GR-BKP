using GRBKP.Core.Discovery;

namespace GRBKP.Core.Tests;

public sealed class ProfileSidMatcherTests
{
    private static readonly IReadOnlyList<ProfileSidInfo> DestinationProfiles =
    [
        new("S-1-5-21-9999-8888-7777-1001", @"C:\Users\auxatc.DOMINIO"),
        new("S-1-5-21-9999-8888-7777-1002", @"C:\Users\lucas.ferreira")
    ];

    [Fact]
    public void Describe_ReturnsMatch_WhenSidsAreEqual()
    {
        var result = ProfileSidMatcher.Describe(
            "S-1-5-21-9999-8888-7777-1001", DestinationProfiles, @"C:\Users\auxatc.DOMINIO");

        Assert.Equal(SidMatchOutcome.Match, result.Outcome);
    }

    [Fact]
    public void Describe_ReturnsMismatch_WhenDestinationBelongsToADifferentSid()
    {
        // Cenario real que essa checagem existe pra pegar: tecnico digita o perfil errado
        // (ex.: de outro funcionario) por engano — mesmo com pastas parecidas, o SID nao bate.
        var result = ProfileSidMatcher.Describe(
            "S-1-5-21-9999-8888-7777-1001", DestinationProfiles, @"C:\Users\lucas.ferreira");

        Assert.Equal(SidMatchOutcome.Mismatch, result.Outcome);
    }

    [Fact]
    public void Describe_ReturnsPackageSidUnavailable_WhenPackageHasNoSid()
    {
        var result = ProfileSidMatcher.Describe(
            null, DestinationProfiles, @"C:\Users\auxatc.DOMINIO");

        Assert.Equal(SidMatchOutcome.PackageSidUnavailable, result.Outcome);
    }

    [Fact]
    public void Describe_ReturnsDestinationSidNotFound_WhenDestinationNeverLoggedIn()
    {
        var result = ProfileSidMatcher.Describe(
            "S-1-5-21-9999-8888-7777-1001",
            DestinationProfiles,
            @"C:\Users\funcionario.novo");

        Assert.Equal(SidMatchOutcome.DestinationSidNotFound, result.Outcome);
    }

    [Fact]
    public void Describe_IgnoresTrailingSeparator_WhenComparingDestinationPaths()
    {
        var result = ProfileSidMatcher.Describe(
            "S-1-5-21-9999-8888-7777-1001", DestinationProfiles, @"C:\Users\auxatc.DOMINIO\");

        Assert.Equal(SidMatchOutcome.Match, result.Outcome);
    }
}
