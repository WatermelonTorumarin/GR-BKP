using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;

namespace GRBKP.Core.Tests;

public sealed class OfflineProfileSidResolverTests
{
    [Fact]
    public async Task ResolveSidsByProfileRootAsync_MatchesProfiles_IgnoringDriveLetterDifference()
    {
        // O SOFTWARE hive do SSD antigo registra o caminho com a letra que ele tinha QUANDO
        // instalado (C:\), mas agora esta montado como origem em outra letra (F:\) — o
        // casamento so pode confiar na parte do caminho depois da unidade.
        var fakeReader = new FakeOfflineRegistryHiveReader(
        [
            new OfflineRegistrySubKey(
                "S-1-5-21-1111111111-2222222222-3333333333-1001",
                new Dictionary<string, string?> { ["ProfileImagePath"] = @"C:\Users\auxatc" }),
            new OfflineRegistrySubKey(
                "S-1-5-21-1111111111-2222222222-3333333333-1002",
                new Dictionary<string, string?> { ["ProfileImagePath"] = @"C:\Users\lucas.ferreira" }),
            new OfflineRegistrySubKey(
                "S-1-5-18",
                new Dictionary<string, string?> { ["ProfileImagePath"] = @"C:\Windows\ServiceProfiles\LocalService" })
        ]);
        var resolver = new OfflineProfileSidResolver(fakeReader);
        var profiles = new List<DiscoveredProfile>
        {
            new("auxatc", @"F:\Users\auxatc", ProfileClassification.RealProfile),
            new("lucas.ferreira", @"F:\Users\lucas.ferreira", ProfileClassification.RealProfile)
        };

        var sidsByProfileRoot = await resolver.ResolveSidsByProfileRootAsync(
            @"F:\", profiles, CancellationToken.None);

        Assert.Equal(
            "S-1-5-21-1111111111-2222222222-3333333333-1001",
            sidsByProfileRoot[@"F:\Users\auxatc"]);
        Assert.Equal(
            "S-1-5-21-1111111111-2222222222-3333333333-1002",
            sidsByProfileRoot[@"F:\Users\lucas.ferreira"]);
        Assert.Equal(2, sidsByProfileRoot.Count);
    }

    [Fact]
    public async Task ResolveSidsByProfileRootAsync_OmitsProfile_WhenNoMatchingSidFound()
    {
        var fakeReader = new FakeOfflineRegistryHiveReader(
        [
            new OfflineRegistrySubKey(
                "S-1-5-21-...-1001",
                new Dictionary<string, string?> { ["ProfileImagePath"] = @"C:\Users\auxatc" })
        ]);
        var resolver = new OfflineProfileSidResolver(fakeReader);
        var profiles = new List<DiscoveredProfile>
        {
            new("auxatc", @"F:\Users\auxatc", ProfileClassification.RealProfile),
            new("perfil-nao-registrado", @"F:\Users\perfil-nao-registrado", ProfileClassification.Suspicious)
        };

        var sidsByProfileRoot = await resolver.ResolveSidsByProfileRootAsync(
            @"F:\", profiles, CancellationToken.None);

        Assert.Single(sidsByProfileRoot);
        Assert.True(sidsByProfileRoot.ContainsKey(@"F:\Users\auxatc"));
    }

    private sealed class FakeOfflineRegistryHiveReader(
        IReadOnlyList<OfflineRegistrySubKey> subKeys) : IOfflineRegistryHiveReader
    {
        public Task<IReadOnlyList<OfflineRegistrySubKey>> EnumerateSubKeysAsync(
            string hiveFilePath,
            string keyPath,
            IReadOnlyCollection<string> valueNames,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(subKeys);
    }
}
