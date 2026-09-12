using GRBKP.Core.Adapters;

namespace GRBKP.Core.Discovery;

public sealed record ProfileSidInfo(string Sid, string ProfileImagePath);

/// <summary>
/// Le o SID de cada perfil diretamente do <c>SOFTWARE</c> hive OFFLINE do SSD antigo (via
/// <see cref="IOfflineRegistryHiveReader"/> — nunca monta nada no registro ao vivo) e casa cada
/// SID com os perfis ja descobertos por <see cref="WindowsProfileDiscovery"/>. O casamento ignora
/// a letra de unidade porque o <c>ProfileImagePath</c> gravado no hive reflete a letra que o SSD
/// tinha QUANDO estava instalado (normalmente C:\), nao a letra atual do SSD montado como origem
/// (ex.: F:\) — so o caminho relativo (<c>Users\nome</c>) e comparavel entre os dois.
/// </summary>
public sealed class OfflineProfileSidResolver(IOfflineRegistryHiveReader registryReader)
{
    private const string ProfileListKeyPath = @"Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string ProfileImagePathValueName = "ProfileImagePath";
    private static readonly string[] SoftwareHiveSegments =
        ["Windows", "System32", "config", "SOFTWARE"];

    public async Task<IReadOnlyList<ProfileSidInfo>> ResolveAllAsync(
        string sourceVolumeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVolumeRoot);

        var hivePath = Path.Combine([sourceVolumeRoot, .. SoftwareHiveSegments]);
        var subKeys = await registryReader.EnumerateSubKeysAsync(
            hivePath,
            ProfileListKeyPath,
            [ProfileImagePathValueName],
            cancellationToken).ConfigureAwait(false);

        var results = new List<ProfileSidInfo>();
        foreach (var subKey in subKeys)
        {
            if (subKey.Values.TryGetValue(ProfileImagePathValueName, out var profileImagePath) &&
                !string.IsNullOrWhiteSpace(profileImagePath))
            {
                results.Add(new ProfileSidInfo(subKey.Name, profileImagePath));
            }
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveSidsByProfileRootAsync(
        string sourceVolumeRoot,
        IReadOnlyList<DiscoveredProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var profileImagePathEntries = await ResolveAllAsync(sourceVolumeRoot, cancellationToken)
            .ConfigureAwait(false);
        return MatchByProfileRoot(profileImagePathEntries, profiles);
    }

    /// <summary>
    /// Casa SIDs já lidos com os perfis descobertos no disco. Separado da leitura de propósito:
    /// quando a origem é o disco da máquina EM USO, o hive SOFTWARE está travado pelo Windows e
    /// os SIDs precisam vir do registro ao vivo — mas o casamento com as pastas é idêntico.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MatchByProfileRoot(
        IReadOnlyList<ProfileSidInfo> profileSids,
        IReadOnlyList<DiscoveredProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profileSids);
        ArgumentNullException.ThrowIfNull(profiles);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            var match = profileSids.FirstOrDefault(entry =>
                PathsMatchIgnoringDriveLetter(entry.ProfileImagePath, profile.ProfileRoot));
            if (match is not null)
            {
                result[profile.ProfileRoot] = match.Sid;
            }
        }

        return result;
    }

    private static bool PathsMatchIgnoringDriveLetter(string profileImagePath, string profileRoot) =>
        string.Equals(
            StripDriveLetter(profileImagePath),
            StripDriveLetter(profileRoot),
            StringComparison.OrdinalIgnoreCase);

    private static string StripDriveLetter(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length > 2 && trimmed[1] == ':'
            ? trimmed[2..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : trimmed;
    }
}
