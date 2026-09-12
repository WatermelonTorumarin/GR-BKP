using GRBKP.Windows.Registry;

namespace GRBKP.IntegrationTests;

public sealed class OfflineRegistryHiveReaderTests
{
    // Prova contra bytes de hive REAIS, sem depender de privilegio nenhum: o NTUSER.DAT do
    // perfil "Default" e um arquivo comum, nao esta montado/travado pelo Windows em execucao
    // (diferente de HKLM\SOFTWARE, que exige SeBackupPrivilege pra exportar — nao disponivel
    // neste ambiente de teste). Copiar e ler esse arquivo prova que o mecanismo de parsing
    // funciona de verdade aqui, sem precisar de RegLoadKey nem elevacao.
    [Fact]
    public async Task EnumerateSubKeysAsync_ReadsRealHiveFile_WithoutAnyElevationOrRegistryMount()
    {
        var defaultHivePath = @"C:\Users\Default\NTUSER.DAT";
        if (!File.Exists(defaultHivePath))
        {
            return; // Ambiente sem essa pasta padrao do Windows — nada a provar aqui.
        }

        var testHivePath = Path.Combine(
            Path.GetTempPath(), $"grbkp-hive-test-{Guid.NewGuid():N}.dat");
        File.Copy(defaultHivePath, testHivePath);
        try
        {
            var reader = new OfflineRegistryHiveReader();

            var subKeys = await reader.EnumerateSubKeysAsync(
                testHivePath, "Software", [], CancellationToken.None);

            Assert.NotEmpty(subKeys);
        }
        finally
        {
            File.Delete(testHivePath);
        }
    }

    /// <summary>
    /// O teste acima só provava que as SUBCHAVES aparecem — nunca que os VALORES pedidos são
    /// realmente lidos, que é a única razão deste adaptador existir (ler ProfileImagePath de cada
    /// perfil em ProfileList para descobrir o SID). Este teste fecha essa lacuna contra um hive
    /// real: "User Shell Folders" sempre tem o valor "Desktop" em qualquer NTUSER.DAT do Windows.
    /// </summary>
    [Fact]
    public async Task EnumerateSubKeysAsync_ActuallyReadsTheRequestedValues_NotJustTheSubKeyNames()
    {
        var defaultHivePath = @"C:\Users\Default\NTUSER.DAT";
        if (!File.Exists(defaultHivePath))
        {
            return;
        }

        var testHivePath = Path.Combine(
            Path.GetTempPath(), $"grbkp-hive-test-{Guid.NewGuid():N}.dat");
        File.Copy(defaultHivePath, testHivePath);
        try
        {
            var reader = new OfflineRegistryHiveReader();

            var subKeys = await reader.EnumerateSubKeysAsync(
                testHivePath,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer",
                ["Desktop"],
                CancellationToken.None);

            var shellFolders = Assert.Single(subKeys, subKey =>
                string.Equals(subKey.Name, "User Shell Folders", StringComparison.OrdinalIgnoreCase));
            var desktop = shellFolders.Values["Desktop"];
            Assert.False(
                string.IsNullOrWhiteSpace(desktop),
                "O valor 'Desktop' precisa ser lido de verdade do hive, não vir nulo.");
            Assert.Contains("Desktop", desktop, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(testHivePath);
        }
    }

    [Fact]
    public async Task EnumerateSubKeysAsync_ReturnsEmpty_WhenKeyPathDoesNotExist()
    {
        var defaultHivePath = @"C:\Users\Default\NTUSER.DAT";
        if (!File.Exists(defaultHivePath))
        {
            return;
        }

        var testHivePath = Path.Combine(
            Path.GetTempPath(), $"grbkp-hive-test-{Guid.NewGuid():N}.dat");
        File.Copy(defaultHivePath, testHivePath);
        try
        {
            var reader = new OfflineRegistryHiveReader();

            var subKeys = await reader.EnumerateSubKeysAsync(
                testHivePath,
                $@"Software\ChaveQueNaoExiste-{Guid.NewGuid():N}",
                [],
                CancellationToken.None);

            Assert.Empty(subKeys);
        }
        finally
        {
            File.Delete(testHivePath);
        }
    }
}
