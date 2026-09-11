using GRBKP.Core.Modules.FileZilla;

namespace GRBKP.Core.Tests;

public sealed class FileZillaServerConfigTests
{
    // Estrutura real observada no anexo analisado durante o levantamento (Q48): usuario "1",
    // senha "1", pasta compartilhada Scan na Area de Trabalho, permissoes completas.
    private const string ClassicConfigXml = """
        <FileZillaServer>
          <Settings>
            <Item name="Server Port">21</Item>
          </Settings>
          <Groups />
          <Users>
            <User Name="1">
              <Option Name="Pass">1</Option>
              <IpFilter />
              <Permissions>
                <Permission Dir="C:\Users\auxatc\Desktop\Scan">
                  <FileRead>1</FileRead>
                  <FileWrite>1</FileWrite>
                  <FileDelete>1</FileDelete>
                  <FileAppend>1</FileAppend>
                  <DirCreate>1</DirCreate>
                  <DirDelete>1</DirDelete>
                  <DirList>1</DirList>
                  <IsHome>1</IsHome>
                </Permission>
              </Permissions>
            </User>
          </Users>
        </FileZillaServer>
        """;

    [Fact]
    public void Parse_ExtractsSharedDirectories_FromPermissionElements()
    {
        var config = FileZillaServerConfigParser.Parse(ClassicConfigXml);

        var directory = Assert.Single(config.SharedDirectories);
        Assert.Equal(@"C:\Users\auxatc\Desktop\Scan", directory);
    }

    [Fact]
    public void Parse_PreservesRawXml_AsOpaqueData()
    {
        // Decisao Q48: a configuracao (usuario/senha/permissoes) e dado opaco — nunca reescrita,
        // so preservada e restaurada como esta, exceto o caminho fisico da pasta compartilhada.
        var config = FileZillaServerConfigParser.Parse(ClassicConfigXml);

        Assert.Equal(ClassicConfigXml, config.RawXml);
    }

    [Fact]
    public void Parse_DeduplicatesRepeatedDirectories_AcrossUsersAndGroups()
    {
        const string xmlWithDuplicateDirs = """
            <FileZillaServer>
              <Groups>
                <Group Name="Impressoras">
                  <Permissions>
                    <Permission Dir="C:\Scan" />
                  </Permissions>
                </Group>
              </Groups>
              <Users>
                <User Name="1">
                  <Permissions>
                    <Permission Dir="C:\Scan" />
                  </Permissions>
                </User>
              </Users>
            </FileZillaServer>
            """;

        var config = FileZillaServerConfigParser.Parse(xmlWithDuplicateDirs);

        Assert.Equal(["C:\\Scan"], config.SharedDirectories);
    }

    [Fact]
    public void RebindSharedDirectory_ReplacesOldProfileRoot_WithNewDestinationRoot()
    {
        // Cenario real: o caminho fisico da pasta compartilhada (Desktop\Scan) precisa apontar
        // para o perfil do usuario NOVO, mesmo que o mount point virtual do FTP continue "/" —
        // sem isso, a impressora continuaria escrevendo scans num caminho que nao existe mais.
        var config = FileZillaServerConfigParser.Parse(ClassicConfigXml);

        var rebound = config.RebindSharedDirectory(
            @"C:\Users\auxatc", @"C:\Users\auxatc.DOMINIO");

        var directory = Assert.Single(rebound.SharedDirectories);
        Assert.Equal(@"C:\Users\auxatc.DOMINIO\Desktop\Scan", directory);
        Assert.Contains(@"C:\Users\auxatc.DOMINIO\Desktop\Scan", rebound.RawXml);
        Assert.DoesNotContain(@"C:\Users\auxatc\Desktop\Scan", rebound.RawXml);
    }
}
