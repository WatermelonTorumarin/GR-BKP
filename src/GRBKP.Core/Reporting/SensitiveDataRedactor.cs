using System.Text.RegularExpressions;

namespace GRBKP.Core.Reporting;

internal static partial class SensitiveDataRedactor
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return LabeledSecretPattern().Replace(
            value,
            match => match.Groups["label"].Value +
                match.Groups["separator"].Value +
                "[REDACTED]");
    }

    [GeneratedRegex(
        "(?<label>password|passwd|pwd|senha|token|cookie|secret|segredo)(?<separator>\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s;,]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabeledSecretPattern();
}
