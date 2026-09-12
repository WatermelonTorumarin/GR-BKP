using GRBKP.Core.Adapters;
using Registry;

namespace GRBKP.Windows.Registry;

/// <summary>
/// Implementacao real de <see cref="IOfflineRegistryHiveReader"/> usando a biblioteca
/// EricZimmerman/Registry (parsing binario puro do arquivo do hive — nunca RegLoadKey/RegUnLoadKey,
/// nunca toca no registro ao vivo do Windows). Usa <c>RegistryHiveOnDemand</c> especificamente
/// porque navega direto ate a chave pedida sem parsear o hive inteiro primeiro.
/// </summary>
public sealed class OfflineRegistryHiveReader : IOfflineRegistryHiveReader
{
    public Task<IReadOnlyList<OfflineRegistrySubKey>> EnumerateSubKeysAsync(
        string hiveFilePath,
        string keyPath,
        IReadOnlyCollection<string> valueNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hiveFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        var hive = new RegistryHiveOnDemand(hiveFilePath);
        var key = hive.GetKey(keyPath);
        if (key is null)
        {
            return Task.FromResult<IReadOnlyList<OfflineRegistrySubKey>>([]);
        }

        var results = new List<OfflineRegistrySubKey>();
        foreach (var subKey in key.SubKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var values = new Dictionary<string, string?>();
            foreach (var valueName in valueNames)
            {
                var value = subKey.Values.FirstOrDefault(v =>
                    string.Equals(v.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
                values[valueName] = value?.ValueData;
            }

            results.Add(new OfflineRegistrySubKey(subKey.KeyName, values));
        }

        return Task.FromResult<IReadOnlyList<OfflineRegistrySubKey>>(results);
    }
}
