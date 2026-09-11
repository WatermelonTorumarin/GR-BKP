using System.Text.Json;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Reporting;
using GRBKP.Core.Transfers;
using GRBKP.Windows.FileSystem;

namespace GRBKP.IntegrationTests;

public sealed class ReportModuleTests
{
    [Fact]
    public async Task T38_ReportIsCoherentCompleteAndDoesNotExposeSecrets()
    {
        var operationRoot = Path.Combine(
            Path.GetTempPath(),
            "GRBKP-T38",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);

        try
        {
            var operationId = Guid.NewGuid();
            var result = new RestoreOperationResult(
                operationId,
                RestoreState.Complete,
                new DurableCounters(
                    CopiedFiles: 31,
                    CopiedBytes: 987_654_321,
                    FailedFiles: 1,
                    IgnoredFiles: 2),
                [new PreservedItem(
                    "unknown-appdata",
                    "data/profile/AppData/Roaming/Unknown/settings.db",
                    "no-known-module")],
                [new OperationError(
                    "required-file-failed",
                    "Falha ao copiar; password=SUPER_SECRET; token: TOKEN_SECRET",
                    "data/profile/Documents/obrigatorio.db")]);
            ModuleReportEntry[] modules =
            [
                new("personal-files", "applied", "reports/personal-validation.json"),
                new("synthetic-mail", "failed", "reports/mail-validation.json")
            ];
            MigrationEvidence[] evidence =
            [
                new(
                    "mail-database-invalid",
                    "senha: SENHA_SECRET; cookie=COOKIE_SECRET; banco não abriu",
                    EvidenceSeverity.Error)
            ];

            var sut = new ReportModule(
                new WindowsFileSystemAdapter(),
                new FixedClock(new DateTimeOffset(2026, 9, 11, 12, 30, 0, TimeSpan.Zero)));

            await sut.WriteRestoreReportAsync(
                operationRoot,
                result,
                modules,
                evidence,
                CancellationToken.None);

            var jsonPath = Path.Combine(operationRoot, "reports", "report.json");
            var textPath = Path.Combine(operationRoot, "reports", "report.txt");
            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(textPath));

            var rawJson = await File.ReadAllTextAsync(
                jsonPath,
                CancellationToken.None);
            var rawText = await File.ReadAllTextAsync(
                textPath,
                CancellationToken.None);
            using var parsed = JsonDocument.Parse(rawJson);
            var root = parsed.RootElement;

            // A falha obrigatória e o Module reprovado impedem falso "complete".
            Assert.Equal("failed", root.GetProperty("state").GetString());
            Assert.Equal(31, root.GetProperty("counters").GetProperty("copiedFiles").GetInt64());
            Assert.Equal(
                987_654_321,
                root.GetProperty("counters").GetProperty("copiedBytes").GetInt64());
            Assert.Equal(1, root.GetProperty("counters").GetProperty("failedFiles").GetInt64());
            Assert.Equal(2, root.GetProperty("modules").GetArrayLength());
            Assert.Single(root.GetProperty("pendingItems").EnumerateArray());
            Assert.Single(root.GetProperty("evidence").EnumerateArray());
            Assert.Single(root.GetProperty("errors").EnumerateArray());
            Assert.Contains("reports/mail-validation.json", rawJson, StringComparison.Ordinal);
            Assert.Contains("data/profile/Documents/obrigatorio.db", rawJson, StringComparison.Ordinal);

            Assert.Contains("Estado: failed", rawText, StringComparison.Ordinal);
            Assert.Contains("synthetic-mail", rawText, StringComparison.Ordinal);
            Assert.Contains("mail-database-invalid", rawText, StringComparison.Ordinal);
            Assert.Contains("unknown-appdata", rawText, StringComparison.Ordinal);

            foreach (var secret in new[]
                     {
                         "SUPER_SECRET", "TOKEN_SECRET", "SENHA_SECRET", "COOKIE_SECRET"
                     })
            {
                Assert.DoesNotContain(secret, rawJson, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, rawText, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(operationRoot, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
