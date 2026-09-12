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
