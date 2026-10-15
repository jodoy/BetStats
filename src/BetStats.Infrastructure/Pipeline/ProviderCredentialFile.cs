using System.Text.Json;

namespace BetStats.Infrastructure.Pipeline;

public static class ProviderCredentialFile
{
    public static string LocalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetStats", "secrets", "api-football.json");

    public static ProviderTransportOptions CreateOptions(bool development) => new(development,
        Environment.GetEnvironmentVariable("BETSTATS_API_FOOTBALL_ENABLED") == "true",
        () => Resolve(development, Environment.GetEnvironmentVariable("BETSTATS_API_FOOTBALL_KEY"), LocalPath));

    // Explicit arguments allow isolated tests without modifying process-wide secrets.
    // Runtime hosts always use LocalPath, never a path from a request or tracked config.
    public static string? Resolve(bool development, string? environmentKey, string localPath)
    {
        if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey;
        if (!development) return null;
        try
        {
            if (!Path.IsPathFullyQualified(localPath)) return null;
            var path = Path.GetFullPath(localPath);
            if (new FileInfo(path).LinkTarget is not null) return null;
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
                if (directory.LinkTarget is not null || File.Exists(Path.Combine(directory.FullName, "BetStats.slnx")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 4096) return null;
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("ApiKey", out var key) || key.ValueKind != JsonValueKind.String) return null;
            var value = key.GetString();
            return value is { Length: > 0 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return null; } // Never propagate JSON snippets, secret values or file diagnostics.
    }
}
