using System.Xml.Linq;

namespace GRBKP.Core.Modules.FileZilla;

/// <summary>
/// Modelo minimo do <c>FileZilla Server.xml</c> classico (0.9.x). Por decisao Q48 do
/// levantamento, o conteudo (usuarios, senhas, permissoes) e dado opaco — nunca reescrito,
/// apenas preservado e restaurado como esta. A unica excecao e o caminho fisico da pasta
/// compartilhada (<c>Dir</c> em cada <c>Permission</c>), que precisa apontar para o perfil de
/// destino, senao a impressora continuaria tentando gravar scans num caminho que nao existe mais.
/// </summary>
public sealed record FileZillaServerConfig(string RawXml, IReadOnlyList<string> SharedDirectories)
{
    public FileZillaServerConfig RebindSharedDirectory(string oldProfileRoot, string newProfileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldProfileRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(newProfileRoot);

        var document = XDocument.Parse(RawXml);
        foreach (var permission in document.Descendants("Permission"))
        {
            var directoryAttribute = permission.Attribute("Dir");
            if (directoryAttribute is null)
            {
                continue;
            }

            if (directoryAttribute.Value.StartsWith(oldProfileRoot, StringComparison.OrdinalIgnoreCase))
            {
                directoryAttribute.Value =
                    newProfileRoot + directoryAttribute.Value[oldProfileRoot.Length..];
            }
        }

        return FileZillaServerConfigParser.Parse(document.ToString());
    }
}

public static class FileZillaServerConfigParser
{
    public static FileZillaServerConfig Parse(string xmlContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlContent);

        var document = XDocument.Parse(xmlContent);
        var sharedDirectories = document
            .Descendants("Permission")
            .Select(permission => permission.Attribute("Dir")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FileZillaServerConfig(xmlContent, sharedDirectories);
    }
}
