using GRBKP.Windows.Registry;

namespace GRBKP.IntegrationTests;

public sealed class LiveProfileSidReaderTests
{
    [Fact]
    public void ResolveAll_ReturnsRealProfilesFromTheRunningMachine()
    {
        // Leitura comum de HKLM\SOFTWARE — sem privilegio especial, sem montar hive nenhum.
        // Confere que o resultado bate com o que sabemos que existe de verdade nesta maquina
        // (visto anteriormente em C:\Users: Admin, Pasta Oficina, kaleb.salvino).
        var reader = new LiveProfileSidReader();

        var profiles = reader.ResolveAll();

        Assert.NotEmpty(profiles);
        Assert.Contains(profiles, p => p.ProfileImagePath.EndsWith(
            @"\Pasta Oficina", StringComparison.OrdinalIgnoreCase));
        Assert.All(profiles, p => Assert.StartsWith("S-1-", p.Sid, StringComparison.Ordinal));
    }
}
