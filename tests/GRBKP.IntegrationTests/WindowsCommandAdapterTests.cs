using GRBKP.Core.Adapters;
using GRBKP.Windows.Commands;

namespace GRBKP.IntegrationTests;

public sealed class WindowsCommandAdapterTests
{
    [Fact]
    public async Task ExecuteApprovedAsync_RunsPnpUtilEnumDrivers_ReadOnlyCommand()
    {
        // Comando somente-leitura, sempre disponivel em qualquer Windows, nao muda nada no
        // sistema — seguro de rodar em qualquer ambiente, inclusive este.
        var adapter = new WindowsCommandAdapter();

        var result = await adapter.ExecuteApprovedAsync(new WindowsCommandRequest(
            ApprovedWindowsCommand.PnpUtil,
            ["/enum-drivers", "/class", "Printer"]));

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task ExecuteApprovedAsync_RunsDismGetDrivers_OrFailsWithElevationRequired()
    {
        // DISM /Online sempre exige elevacao de administrador — o app real ja roda elevado
        // (app.manifest), mas o processo de teste nao. 740 = ERROR_ELEVATION_REQUIRED: prova
        // que o adaptador de fato invocou o Dism.exe real (nao uma excecao/crash do adaptador
        // em si), sem precisar elevar este processo de teste so pra validar isso.
        var adapter = new WindowsCommandAdapter();

        var result = await adapter.ExecuteApprovedAsync(new WindowsCommandRequest(
            ApprovedWindowsCommand.Dism,
            ["/Online", "/Get-Drivers", "/Format:List"]));

        Assert.True(
            result.ExitCode == 0 || result.ExitCode == 740,
            $"Esperava sucesso ou 'elevação necessária' (740), recebeu {result.ExitCode}: " +
            $"{result.StandardError}");
    }
}
