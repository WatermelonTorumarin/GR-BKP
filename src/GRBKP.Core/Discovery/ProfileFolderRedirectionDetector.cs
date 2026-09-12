using GRBKP.Core.Adapters;

namespace GRBKP.Core.Discovery;

public sealed record RedirectedFolder(string FolderName, string ConfiguredPath);

/// <summary>
/// Descobre se as pastas pessoais do perfil (Área de Trabalho, Documentos, Downloads, Imagens)
/// apontam para FORA do perfil — o caso mais comum sendo o "Backup de pastas" do OneDrive, que
/// move Documentos e Área de Trabalho para dentro da pasta do OneDrive, e o redirecionamento
/// corporativo para um compartilhamento de rede.
///
/// Isso importa porque a varredura padrão do app procura literalmente em
/// <c>&lt;perfil&gt;\Documents</c>, <c>&lt;perfil&gt;\Desktop</c> etc. Quando essas pastas estão
/// redirecionadas, o caminho padrão fica vazio e o backup terminaria "com sucesso" SEM os
/// documentos do funcionário — perda silenciosa, o pior tipo de falha num utilitário de backup.
/// Aqui só DETECTAMOS e avisamos: o que fazer com a pasta redirecionada é decisão do técnico.
///
/// A leitura é do <c>NTUSER.DAT</c> do próprio perfil, offline (nunca monta nada no registro ao
/// vivo). Se o hive não puder ser lido — perfil logado no momento, arquivo danificado — a
/// detecção simplesmente não acusa nada: ela nunca pode impedir um backup de acontecer.
/// </summary>
public sealed class ProfileFolderRedirectionDetector(IOfflineRegistryHiveReader registryReader)
{
    private const string ExplorerKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string UserShellFoldersKeyName = "User Shell Folders";
    private const string HiveFileName = "NTUSER.DAT";

    /// <summary>
    /// Nome do valor no registro, nome amigável em português e o caminho padrão esperado. O
    /// Downloads não tem nome legível: desde o Windows Vista ele é identificado pelo GUID da
    /// pasta conhecida.
    /// </summary>
    private static readonly (string ValueName, string FolderName, string DefaultLeaf)[] Watched =
    [
        ("Desktop", "Área de Trabalho", "Desktop"),
        ("Personal", "Documentos", "Documents"),
        ("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads", "Downloads"),
        ("My Pictures", "Imagens", "Pictures")
    ];

    public async Task<IReadOnlyList<RedirectedFolder>> DetectAsync(
        string profileRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);

        var hivePath = Path.Combine(profileRoot, HiveFileName);
        IReadOnlyList<OfflineRegistrySubKey> subKeys;
        try
        {
            subKeys = await registryReader.EnumerateSubKeysAsync(
                hivePath,
                ExplorerKeyPath,
                [.. Watched.Select(watched => watched.ValueName)],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }

        var shellFolders = subKeys.FirstOrDefault(subKey =>
            string.Equals(subKey.Name, UserShellFoldersKeyName, StringComparison.OrdinalIgnoreCase));
        if (shellFolders is null)
        {
            return [];
        }

        var redirected = new List<RedirectedFolder>();
        foreach (var (valueName, folderName, defaultLeaf) in Watched)
        {
            if (!shellFolders.Values.TryGetValue(valueName, out var configuredPath) ||
                string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            if (!IsDefaultLocation(configuredPath, defaultLeaf))
            {
                redirected.Add(new RedirectedFolder(folderName, configuredPath));
            }
        }

        return redirected;
    }

    /// <summary>
    /// O valor padrão é sempre <c>%USERPROFILE%\&lt;Pasta&gt;</c>. Não dá para expandir a variável
    /// aqui (o hive é de OUTRA máquina), mas também não é preciso: qualquer coisa que não seja
    /// exatamente o padrão já merece o aviso ao técnico.
    /// </summary>
    private static bool IsDefaultLocation(string configuredPath, string defaultLeaf) =>
        string.Equals(
            configuredPath.TrimEnd('\\'),
            $@"%USERPROFILE%\{defaultLeaf}",
            StringComparison.OrdinalIgnoreCase);
}
