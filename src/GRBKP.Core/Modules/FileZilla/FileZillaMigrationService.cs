using GRBKP.Core.Adapters;

namespace GRBKP.Core.Modules.FileZilla;

public sealed record FileZillaCaptureResult(bool Captured, string? PackageRelativePath);

public sealed record FileZillaRestoreResult(bool Applied, string? DiagnosticMessage);

/// <summary>
/// Captura e restaura o FileZilla Server.xml classico (0.9.x). Por decisao Q59, a restauracao
/// e direta quando origem e destino usam a mesma versao (que e o caso aqui: os apps ja vem
/// instalados pelo pendrive da empresa antes da restauracao, versao sempre igual) — nao
/// implementa o importador assistido de versoes diferentes (Q44 opcao 2), que so seria
/// necessario num cenario que a empresa nao usa hoje.
///
/// O CONTEUDO da pasta compartilhada (ex.: Desktop\Scan) nao e copiado por este servico — por
/// decisao Q45, essa pasta sempre fica dentro da Area de Trabalho do perfil selecionado, entao
/// ja e capturada pela varredura normal de arquivos pessoais (SelectionPolicyModule). Este
/// servico so cuida da configuracao do servidor em si e da revinculacao do caminho fisico.
/// </summary>
public sealed class FileZillaMigrationService(
    IFileSystemAdapter fileSystem,
    IWindowsServiceAdapter serviceAdapter)
{
    // TODO: confirmar o nome real do servico Windows numa instalacao de producao antes da
    // homologacao fisica — nomes candidatos comuns sao "FileZilla Server" ou
    // "FileZillaFTPServer"; nunca verificado contra uma instalacao real ainda.
    private const string ServiceName = "FileZilla Server";
    private const string ConfigFileName = "FileZilla Server.xml";
    private static readonly string[] ClassicConfigSegments =
        ["Program Files (x86)", "FileZilla Server", ConfigFileName];
    private static readonly string[] PackageSegments = ["modules", "filezilla", ConfigFileName];

    public async Task<FileZillaCaptureResult> CaptureAsync(
        string sourceVolumeRoot,
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var sourceConfigPath = Path.Combine([sourceVolumeRoot, .. ClassicConfigSegments]);
        var info = await fileSystem.GetInfoAsync(sourceConfigPath, cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
        {
            return new FileZillaCaptureResult(false, null);
        }

        var destinationPath = Path.Combine([packageRoot, .. PackageSegments]);
        await CopyFileAsync(sourceConfigPath, destinationPath, cancellationToken).ConfigureAwait(false);

        return new FileZillaCaptureResult(true, string.Join('/', PackageSegments));
    }

    public async Task<FileZillaRestoreResult> RestoreAsync(
        string packageRoot,
        string oldProfileRoot,
        string newProfileRoot,
        string destinationVolumeRoot,
        CancellationToken cancellationToken = default)
    {
        var capturedConfigPath = Path.Combine([packageRoot, .. PackageSegments]);
        var capturedInfo = await fileSystem.GetInfoAsync(capturedConfigPath, cancellationToken)
            .ConfigureAwait(false);
        if (capturedInfo is null)
        {
            return new FileZillaRestoreResult(false, "Nenhuma configuração do FileZilla neste pacote.");
        }

        var destinationConfigPath = Path.Combine([destinationVolumeRoot, .. ClassicConfigSegments]);
        var destinationInfo = await fileSystem.GetInfoAsync(destinationConfigPath, cancellationToken)
            .ConfigureAwait(false);
        if (destinationInfo is null)
        {
            return new FileZillaRestoreResult(
                false,
                $"FileZilla Server não está instalado no destino ({destinationConfigPath}). " +
                "Instale pelo pendrive padrão da empresa antes de restaurar.");
        }

        var rawXml = await ReadAllTextAsync(capturedConfigPath, cancellationToken).ConfigureAwait(false);
        var rebound = FileZillaServerConfigParser.Parse(rawXml)
            .RebindSharedDirectory(oldProfileRoot, newProfileRoot);

        await TryStopServiceAsync(cancellationToken).ConfigureAwait(false);
        await WriteAllTextAsync(destinationConfigPath, rebound.RawXml, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await serviceAdapter.StartAsync(ServiceName, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return new FileZillaRestoreResult(
                false,
                $"Configuração aplicada em {destinationConfigPath}, mas o serviço \"{ServiceName}\" " +
                $"não iniciou: {exception.Message}");
        }

        return new FileZillaRestoreResult(true, null);
    }

    private async Task TryStopServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await serviceAdapter.StopAsync(ServiceName, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Servico pode ainda nao existir (primeira instalacao) ou ja estar parado —
            // seguimos para aplicar a configuracao; o proximo StartAsync revela problemas reais.
        }
    }

    private async Task CopyFileAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destino sem diretório pai.");
        await fileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken)
            .ConfigureAwait(false);

        await using var source = await fileSystem.OpenReadAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        await using var destination = await fileSystem.OpenWriteAsync(
            destinationPath, FileWriteMode.CreateOrOverwrite, cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAllTextAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenWriteAsync(
            path, FileWriteMode.CreateOrOverwrite, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
