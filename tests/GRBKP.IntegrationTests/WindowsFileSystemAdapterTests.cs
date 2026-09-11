using System.Diagnostics;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class WindowsFileSystemAdapterTests
{
    [Fact]
    public async Task EnumerateFilesAsync_DoesNotFollowDirectoryJunctions_AndDoesNotCrashOnThem()
    {
        // Reproduz o bug real encontrado ao testar num Windows pt-BR: "Documents\Meus Vídeos" e
        // uma junction (link de diretorio) que derrubava o backup inteiro com
        // UnauthorizedAccessException ao ser varrida. A enumeracao nao pode seguir esse link
        // nem travar por causa dele — só precisa ignora-lo e continuar com o resto da arvore.
        using var fixture = new TemporaryDirectory();
        var documentsRoot = fixture.CreateTree("Documents");
        fixture.WriteFile("Documents/relatorio.txt", "conteudo real");

        var linkTargetRoot = fixture.CreateTree("VideosReais");
        fixture.WriteFile("VideosReais/clipe.mp4", "nao deveria aparecer no backup de Documents");

        var junctionPath = Path.Combine(documentsRoot, "Meus Vídeos");
        fixture.CreateDirectoryJunction(junctionPath, linkTargetRoot);

        var fileSystem = new WindowsFileSystemAdapter();

        var entries = new List<string>();
        await foreach (var entry in fileSystem.EnumerateFilesAsync(documentsRoot, CancellationToken.None))
        {
            entries.Add(entry.FullPath);
        }

        Assert.Contains(entries, path => path.EndsWith("relatorio.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, path => path.Contains("clipe.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnumerateFilesAsync_ContinuesPastAnInaccessibleSubdirectory()
    {
        using var fixture = new TemporaryDirectory();
        var root = fixture.CreateTree("Origem");
        fixture.WriteFile("Origem/antes.txt", "arquivo antes");
        var protectedDirectory = fixture.CreateTree("Origem/Protegida");
        fixture.WriteFile("Origem/Protegida/dentro.txt", "nunca deveria ser lido");
        fixture.WriteFile("Origem/depois.txt", "arquivo depois");

        fixture.DenyReadAccess(protectedDirectory);
        try
        {
            var fileSystem = new WindowsFileSystemAdapter();
            var entries = new List<string>();
            await foreach (var entry in fileSystem.EnumerateFilesAsync(root, CancellationToken.None))
            {
                entries.Add(entry.FullPath);
            }

            Assert.Contains(entries, path => path.EndsWith("antes.txt", StringComparison.Ordinal));
            Assert.Contains(entries, path => path.EndsWith("depois.txt", StringComparison.Ordinal));
        }
        finally
        {
            fixture.RestoreAccess(protectedDirectory);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "grbkp-windows-fs-adapter", Guid.NewGuid().ToString("N"));
        private readonly List<string> createdJunctions = [];

        public TemporaryDirectory() => Directory.CreateDirectory(root);

        public string CreateTree(string relativePath)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            return path;
        }

        public void WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void CreateDirectoryJunction(string junctionPath, string targetPath)
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", junctionPath, targetPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Não foi possível iniciar mklink.");
            process.WaitForExit(10_000);
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"mklink /J falhou: {error}");
            }

            createdJunctions.Add(junctionPath);
        }

        public void DenyReadAccess(string directoryPath)
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            var security = directoryInfo.GetAccessControl();
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                identity,
                System.Security.AccessControl.FileSystemRights.Read
                    | System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            directoryInfo.SetAccessControl(security);
        }

        public void RestoreAccess(string directoryPath)
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            var security = directoryInfo.GetAccessControl();
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            security.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                identity,
                System.Security.AccessControl.FileSystemRights.Read
                    | System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            directoryInfo.SetAccessControl(security);
        }

        public void Dispose()
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            // Directory.Delete(recursive: true) tenta entrar nas junctions em vez de so
            // desvincula-las — desvincula manualmente primeiro (Delete nao-recursivo remove
            // apenas o link, sem tocar no alvo), depois apaga o resto normalmente.
            foreach (var junction in createdJunctions)
            {
                if (Directory.Exists(junction))
                {
                    Directory.Delete(junction, recursive: false);
                }
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
