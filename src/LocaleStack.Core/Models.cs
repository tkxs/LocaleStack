using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocaleStack.Core;

public static class AppInfo
{
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"JSON 内容为空：{path}");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record Catalog
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public string Repository { get; init; } = "tkxs/LocaleStack";
    public List<GameEntry> Games { get; init; } = [];
}

public sealed record GameEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? ChineseTitle { get; init; }
    public string Summary { get; init; } = "";
    public string Engine { get; init; } = "";
    public string? SteamAppId { get; init; }
    public string[] ExecutableNames { get; init; } = [];
    public string Status { get; init; } = "planned";
    public PatchRelease? Patch { get; init; }
}

public sealed record PatchRelease
{
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public required string Sha256 { get; init; }
    public long Bytes { get; init; }
    public string Notes { get; init; } = "";
    public string[] Variants { get; init; } = [];
}

public sealed record PatchManifest
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public required string GameId { get; init; }
    public required string GameTitle { get; init; }
    public required string Version { get; init; }
    public string Language { get; init; } = "zh-CN";
    public string? SteamAppId { get; init; }
    public string[] ExecutableNames { get; init; } = [];
    public List<PatchVariant> Variants { get; init; } = [];
}

public sealed record PatchVariant
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    [JsonRequired]
    public string Storefront { get; init; } = "any";
    public string[] SteamBuildIds { get; init; } = [];
    public List<Fingerprint> Fingerprints { get; init; } = [];
    public List<PatchFile> Files { get; init; } = [];
}

public sealed record Fingerprint
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record PatchFile
{
    public required string Path { get; init; }
    public required string Payload { get; init; }
    public required string Sha256 { get; init; }
    // null means this file MUST NOT exist in the clean game.
    [JsonRequired]
    public string? OriginalSha256 { get; init; }
}

public sealed record InstallState
{
    public int SchemaVersion { get; init; } = 1;
    public required string GameId { get; init; }
    public required string Version { get; init; }
    public required string VariantId { get; init; }
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public List<InstalledFile> Files { get; init; } = [];
}

public sealed record InstalledFile
{
    public required string Path { get; init; }
    public required string InstalledSha256 { get; init; }
    public string? OriginalSha256 { get; init; }
}

public sealed record GameLocation(string Directory, string Storefront, string? SteamBuildId = null);
public sealed record OperationProgress(string Message, double? Fraction = null);
public sealed record CompatibilityResult(bool Compatible, string Message, string? VariantId = null);

public static class ReleaseVersion
{
    public static Version Parse(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
            throw new InvalidDataException("版本号必须使用 major.minor.patch，例如 1.2.3。");
        return Version.Parse(value);
    }
    public static bool IsNewer(string candidate, string installed) => Parse(candidate) > Parse(installed);
}
