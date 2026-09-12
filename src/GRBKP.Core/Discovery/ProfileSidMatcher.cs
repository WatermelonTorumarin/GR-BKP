namespace GRBKP.Core.Discovery;

public enum SidMatchOutcome
{
    /// <summary>O pacote nao tem SID gravado (backup antigo, ou hive nao pode ser lido na origem).</summary>
    PackageSidUnavailable,

    /// <summary>O perfil de destino ainda nao fez login neste Windows — sem SID pra comparar.</summary>
    DestinationSidNotFound,

    /// <summary>Mesmo SID — mesmo usuario de dominio do backup original.</summary>
    Match,

    /// <summary>SIDs diferentes — o destino parece ser de outro usuario.</summary>
    Mismatch
}

public sealed record SidMatchResult(SidMatchOutcome Outcome, string Message);

/// <summary>
/// Compara o SID gravado no pacote (lido offline do SSD antigo, ver
/// <see cref="OfflineProfileSidResolver"/>) com o SID do perfil de destino (lido ao vivo, ver
/// <c>LiveProfileSidReader</c> em GRBKP.Windows). Isso e uma checagem de seguranca adicional —
/// nunca bloqueia a restauracao sozinha, so avisa; a decisao final continua manual (Q2 do
/// levantamento com o responsavel).
/// </summary>
public static class ProfileSidMatcher
{
    public static SidMatchResult Describe(
        string? packageSid,
        IReadOnlyList<ProfileSidInfo> destinationProfiles,
        string destinationProfileRoot)
    {
        ArgumentNullException.ThrowIfNull(destinationProfiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationProfileRoot);

        if (string.IsNullOrWhiteSpace(packageSid))
        {
            return new SidMatchResult(
                SidMatchOutcome.PackageSidUnavailable,
                "Conferência de usuário: SID do backup original não disponível.");
        }

        var normalizedDestination = NormalizePath(destinationProfileRoot);
        var destinationSid = destinationProfiles
            .FirstOrDefault(profile =>
                string.Equals(
                    NormalizePath(profile.ProfileImagePath),
                    normalizedDestination,
                    StringComparison.OrdinalIgnoreCase))
            ?.Sid;

        if (destinationSid is null)
        {
            return new SidMatchResult(
                SidMatchOutcome.DestinationSidNotFound,
                "Conferência de usuário: este perfil de destino ainda não fez login neste " +
                "Windows (SID não encontrado).");
        }

        return string.Equals(packageSid, destinationSid, StringComparison.OrdinalIgnoreCase)
            ? new SidMatchResult(
                SidMatchOutcome.Match,
                "✓ Conferência de usuário: mesmo usuário de domínio do backup original.")
            : new SidMatchResult(
                SidMatchOutcome.Mismatch,
                "⚠ ATENÇÃO — Conferência de usuário: este destino parece ser de um usuário " +
                "DIFERENTE do backup original. Confirme antes de prosseguir.");
    }

    private static string NormalizePath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
