using GRBKP.Core.Discovery;

namespace GRBKP.Windows.Registry;

/// <summary>
/// Le o SID de cada perfil do registro AO VIVO da maquina atual — usado para o perfil de
/// DESTINO, que ja esta no Windows em execucao (diferente do SSD antigo, que precisa do
/// <see cref="OfflineRegistryHiveReader"/>). Sem risco: e uma leitura comum de
/// <c>HKLM\SOFTWARE\...\ProfileList</c>, ja montado, sem precisar de privilegio especial.
/// </summary>
public sealed class LiveProfileSidReader
{
    private const string ProfileListKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public IReadOnlyList<ProfileSidInfo> ResolveAll()
    {
        using var profileListKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ProfileListKeyPath);
        if (profileListKey is null)
        {
            return [];
        }

        var results = new List<ProfileSidInfo>();
        foreach (var sidName in profileListKey.GetSubKeyNames())
        {
            using var subKey = profileListKey.OpenSubKey(sidName);
            if (subKey?.GetValue("ProfileImagePath") is string profileImagePath &&
                !string.IsNullOrWhiteSpace(profileImagePath))
            {
                results.Add(new ProfileSidInfo(sidName, profileImagePath));
            }
        }

        return results;
    }
}
