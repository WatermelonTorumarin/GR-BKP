using System.Text;
using System.Text.Json;
using GRBKP.Core.Adapters;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Packages;
using GRBKP.Core.Transfers;

namespace GRBKP.Core.Reporting;

public sealed record MigrationReportDocument(
    string FormatVersion,
    Guid OperationId,
    OperationKind OperationKind,
    string State,
    DateTimeOffset GeneratedAtUtc,
    DurableCounters Counters,
    IReadOnlyList<ModuleReportEntry> Modules,
    IReadOnlyList<PreservedItem> PendingItems,
    IReadOnlyList<MigrationEvidence> Evidence,
    IReadOnlyList<OperationError> Errors);

public sealed record ModuleReportEntry(
    string ModuleId,
    string Outcome,
    string? DiagnosticPath = null);

public sealed class ReportModule(IFileSystemAdapter fileSystem, IClock clock)
{
    public async Task WriteRestoreReportAsync(
        string operationRoot,
        RestoreOperationResult result,
        IReadOnlyList<ModuleReportEntry>? modules = null,
        IReadOnlyList<MigrationEvidence>? evidence = null,
        CancellationToken cancellationToken = default)
    {
        var safeModules = SanitizeModules(modules ?? BuildPreservedModules(result.PreservedItems));
        var safePendingItems = result.PreservedItems
            .Select(item => new PreservedItem(
                SensitiveDataRedactor.Sanitize(item.ModuleId),
                SensitiveDataRedactor.Sanitize(item.PackageRelativePath),
                SensitiveDataRedactor.Sanitize(item.ReasonCode)))
            .ToArray();
        var safeEvidence = (evidence ?? [])
            .Select(item => new MigrationEvidence(
                SensitiveDataRedactor.Sanitize(item.Code),
                SensitiveDataRedactor.Sanitize(item.Message),
                item.Severity))
            .ToArray();
        var safeErrors = result.Errors
            .Select(error => new OperationError(
                SensitiveDataRedactor.Sanitize(error.Code),
                SensitiveDataRedactor.Sanitize(error.Message),
                error.RelativePath is null
                    ? null
                    : SensitiveDataRedactor.Sanitize(error.RelativePath)))
            .ToArray();
        var effectiveState = DetermineEffectiveState(
            result.State,
            result.Counters,
            safeModules,
            safeEvidence,
            safeErrors);
        var document = new MigrationReportDocument(
            "1.0",
            result.OperationId,
            OperationKind.Restore,
            effectiveState.ToString().ToLowerInvariant(),
            clock.UtcNow,
            result.Counters,
            safeModules,
            safePendingItems,
            safeEvidence,
            safeErrors);
        var reportsRoot = Path.Combine(operationRoot, "reports");
        await fileSystem.CreateDirectoryAsync(reportsRoot, cancellationToken).ConfigureAwait(false);
        await WriteJsonAtomicallyAsync(
            Path.Combine(reportsRoot, "report.json"),
            document,
            result.OperationId,
            cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicallyAsync(
            Path.Combine(reportsRoot, "report.txt"),
            RenderHumanSummary(document),
            result.OperationId,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<ModuleReportEntry> BuildPreservedModules(
        IReadOnlyList<PreservedItem> items) =>
        items.Select(item => item.ModuleId)
            .Distinct(StringComparer.Ordinal)
            .Select(moduleId => new ModuleReportEntry(moduleId, "preserved-not-reintegrated"))
            .ToArray();

    private static IReadOnlyList<ModuleReportEntry> SanitizeModules(
        IReadOnlyList<ModuleReportEntry> modules) =>
        modules.Select(module => new ModuleReportEntry(
                SensitiveDataRedactor.Sanitize(module.ModuleId),
                SensitiveDataRedactor.Sanitize(module.Outcome),
                module.DiagnosticPath is null
                    ? null
                    : SensitiveDataRedactor.Sanitize(module.DiagnosticPath)))
            .ToArray();

    private static RestoreState DetermineEffectiveState(
        RestoreState requestedState,
        DurableCounters counters,
        IReadOnlyList<ModuleReportEntry> modules,
        IReadOnlyList<MigrationEvidence> evidence,
        IReadOnlyList<OperationError> errors)
    {
        var hasMandatoryFailure = counters.FailedFiles > 0 ||
            errors.Count > 0 ||
            modules.Any(module =>
                module.Outcome.Equals("failed", StringComparison.OrdinalIgnoreCase)) ||
            evidence.Any(item => item.Severity == EvidenceSeverity.Error);

        return hasMandatoryFailure ? RestoreState.Failed : requestedState;
    }

    private static string RenderHumanSummary(MigrationReportDocument report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("GRBKP v1.0 — Relatório da operação");
        builder.Append("Operação: ").AppendLine(report.OperationId.ToString());
        builder.Append("Estado: ").AppendLine(report.State);
        builder.Append("Arquivos copiados: ").AppendLine(
            report.Counters.CopiedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("Bytes copiados: ").AppendLine(
            report.Counters.CopiedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("Arquivos falhos: ").AppendLine(
            report.Counters.FailedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("Itens preservados sem reintegração: ").AppendLine(
            report.PendingItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("Módulos:");
        foreach (var module in report.Modules)
        {
            builder.Append("- ")
                .Append(module.ModuleId)
                .Append(": ")
                .Append(module.Outcome);
            if (module.DiagnosticPath is not null)
            {
                builder.Append(" — diagnóstico: ").Append(module.DiagnosticPath);
            }

            builder.AppendLine();
        }

        builder.AppendLine("Pendências:");

        foreach (var pending in report.PendingItems)
        {
            builder.Append("- ")
                .Append(pending.ModuleId)
                .Append(": ")
                .Append(pending.ReasonCode)
                .Append(" — ")
                .AppendLine(pending.PackageRelativePath);
        }

        builder.AppendLine("Evidências:");
        foreach (var evidence in report.Evidence)
        {
            builder.Append("- ")
                .Append(evidence.Severity)
                .Append(' ')
                .Append(evidence.Code)
                .Append(": ")
                .AppendLine(evidence.Message);
        }

        foreach (var error in report.Errors)
        {
            builder.Append("ERRO ")
                .Append(error.Code)
                .Append(": ")
                .Append(error.Message);
            if (error.RelativePath is not null)
            {
                builder.Append(" — ").Append(error.RelativePath);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".grbkp.partial.{operationId:N}";
        await using (var stream = await fileSystem.OpenWriteAsync(
            temporaryPath,
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                BackupPackageModule.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTextAtomicallyAsync(
        string destinationPath,
        string content,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".grbkp.partial.{operationId:N}";
        await using (var stream = await fileSystem.OpenWriteAsync(
            temporaryPath,
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }
}
