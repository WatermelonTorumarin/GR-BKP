namespace GRBKP.Core.Packages;

public static class PackagePath
{
    public static string ResolveInside(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ValidateRelative(relativePath);

        var fullRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var platformRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, platformRelative));
        var requiredPrefix = fullRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidPackagePathException(relativePath, "path-escapes-root");
        }

        return candidate;
    }

    public static void ValidateRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidPackagePathException(relativePath, "path-empty");
        }

        if (relativePath.Contains('\\') ||
            relativePath.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':'))
        {
            throw new InvalidPackagePathException(relativePath, "path-not-normalized-relative");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
            segment.Length == 0 ||
            segment is "." or ".."))
        {
            throw new InvalidPackagePathException(relativePath, "path-escapes-root");
        }
    }
}

public sealed class InvalidPackagePathException(string? path, string errorCode)
    : Exception($"Caminho inválido no pacote ({errorCode}): {path}")
{
    public string? InvalidPath { get; } = path;

    public string ErrorCode { get; } = errorCode;
}
