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
        foreach (var shallowSubKey in key.SubKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // "OnDemand" é literal: as subchaves devolvidas por key.SubKeys vêm RASAS — só com o
            // nome, sempre com Values vazio. Para ler os valores é obrigatório buscar cada chave
            // pelo caminho completo. Sem isto, todo valor pedido voltava nulo e a resolução de
            // SID (que lê ProfileImagePath de cada perfil em ProfileList) nunca encontrava nada.
            var fullKey = valueNames.Count > 0
                ? hive.GetKey($@"{keyPath}\{shallowSubKey.KeyName}")
                : null;

            var values = new Dictionary<string, string?>();
            foreach (var valueName in valueNames)
            {
                var value = fullKey?.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
                values[valueName] = value?.ValueData;
            }

            results.Add(new OfflineRegistrySubKey(shallowSubKey.KeyName, values));
        }

        return Task.FromResult<IReadOnlyList<OfflineRegistrySubKey>>(results);
    }
}
