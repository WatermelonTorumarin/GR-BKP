using GRBKP.Windows.Services;

namespace GRBKP.IntegrationTests;

public sealed class WindowsServiceAdapterTests
{
    // Nao testamos Stop/Start contra um servico real: criar um servico do Windows exige
    // elevacao (sc create), que este processo de teste nao tem (o app real roda elevado via
    // app.manifest). Este teste confirma que o adaptador realmente consulta o Service Control
    // Manager do Windows — um servico inexistente deve falhar de forma limpa, nao travar nem
    // lancar algo inesperado.
    [Fact]
    public async Task StopAsync_ThrowsCleanly_WhenServiceDoesNotExist()
    {
        var adapter = new WindowsServiceAdapter();
        var nonExistentServiceName = $"grbkp-nao-existe-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.StopAsync(nonExistentServiceName, CancellationToken.None));
    }
}
