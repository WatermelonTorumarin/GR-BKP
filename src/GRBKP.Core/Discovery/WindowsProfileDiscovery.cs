using GRBKP.Core.Adapters;

namespace GRBKP.Core.Discovery;

public enum ProfileClassification
{
    /// <summary>Pasta com marcas reais de perfil de usuario (ex.: AppData) — candidata a funcionario.</summary>
    RealProfile,

    /// <summary>Nome conhecido de pasta-molde/sistema do Windows — nunca e um funcionario.</summary>
    TemplateOrSystem,

    /// <summary>Nao e um nome conhecido de sistema, mas tambem nao tem marcas de perfil real
    /// (ou e um reparse point/junction) — precisa de revisao manual do tecnico antes de decidir.</summary>
    Suspicious
}

public sealed record DiscoveredProfile(
    string Name,
    string ProfileRoot,
    ProfileClassification Classification);

/// <summary>
/// Descobre candidatos a perfil de funcionario dentro de <c>&lt;volume&gt;\Users</c> num SSD
/// (online ou offline, montado em outra letra). Fase 2 da especificacao: "descoberta de
/// Windows/perfis offline". Por decisao Q2, a escolha de QUAL perfil migrar continua manual —
/// este Modulo so apresenta e classifica os candidatos, nunca seleciona automaticamente.
/// </summary>
public sealed class WindowsProfileDiscovery(IFileSystemAdapter fileSystem)
{
    private const string UsersFolderName = "Users";
    private const string AppDataFolderName = "AppData";

    /// <summary>
    /// Nomes de pasta que o proprio Windows usa para perfis-molde/sistema, nunca funcionarios
    /// reais — mesmo quando tecnicamente contem uma subpasta AppData (ex.: "Default"). Inclui as
    /// junctions localizadas em portugues que um Windows em pt-BR cria dentro de Users
    /// (confirmado num C:\Users real: "Todos os Usuários" -> ProgramData, "Usuário Padrão" -> Default).
    /// </summary>
    private static readonly HashSet<string> TemplateOrSystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default", "Default User", "Public", "All Users", "defaultuser0", "WDAGUtilityAccount",
        "Todos os Usuários", "Usuário Padrão", "Padrão"
    };

    public async Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(
        string volumeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        var usersRoot = Path.Combine(volumeRoot, UsersFolderName);
        var results = new List<DiscoveredProfile>();

        await foreach (var directory in fileSystem
            .EnumerateDirectoriesAsync(usersRoot, cancellationToken)
            .ConfigureAwait(false))
        {
            var name = Path.GetFileName(directory.FullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

            if (TemplateOrSystemNames.Contains(name))
            {
                results.Add(new DiscoveredProfile(name, directory.FullPath, ProfileClassification.TemplateOrSystem));
                continue;
            }

            if (directory.IsReparsePoint)
            {
                // Reparse points/junctions dentro de Users normalmente sao compatibilidade legada
                // do Windows (ex.: "All Users" apontando para ProgramData em versoes antigas), nao
                // perfis reais — mas o nome pode nao bater com a lista conhecida.
                results.Add(new DiscoveredProfile(name, directory.FullPath, ProfileClassification.Suspicious));
                continue;
            }

            var appDataPath = Path.Combine(directory.FullPath, AppDataFolderName);
            var appDataInfo = await fileSystem.GetInfoAsync(appDataPath, cancellationToken)
                .ConfigureAwait(false);
            var classification = appDataInfo?.Kind == PathEntryKind.Directory
                ? ProfileClassification.RealProfile
                : ProfileClassification.Suspicious;
            results.Add(new DiscoveredProfile(name, directory.FullPath, classification));
        }

        return results;
    }
}
