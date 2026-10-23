using System.IO.Compression;

namespace LocaleStack.Core;

public static class PatchAuthoring
{
    public static void Capture(CaptureConfig config, string output, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        if (Directory.Exists(output)) throw new IOException("输出目录已存在，请使用新目录，以免混入旧文件。");
        SafePaths.CheckId(config.GameId); ReleaseVersion.Parse(config.Version);
        var variants = new List<PatchVariant>();
        var copies = new List<(string Source, string Target)>();
        foreach (var source in config.Variants)
        {
            SafePaths.CheckId(source.Id);
            var originalRoot = Path.GetFullPath(source.OriginalDirectory);
            var translatedRoot = Path.GetFullPath(source.TranslatedDirectory);
            if (!Directory.Exists(originalRoot) || !Directory.Exists(translatedRoot)) throw new DirectoryNotFoundException("原版或汉化目录不存在。");
            if (originalRoot.Equals(translatedRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("原版和汉化目录必须不同。");
            var files = new List<PatchFile>();
            var fingerprints = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in source.Files)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("准备补丁 · " + path));
                var original = SafePaths.Resolve(originalRoot, path);
                var translated = SafePaths.Resolve(translatedRoot, path);
                if (!File.Exists(translated)) throw new FileNotFoundException("汉化文件不存在", translated);
                var hash = SafePaths.Hash(translated);
                var old = File.Exists(original) ? SafePaths.Hash(original) : null;
                if (SafePaths.HashEquals(hash, old)) { progress?.Report(new("跳过未改变文件：" + path)); continue; }
                var payload = $"payload/{source.Id}/{path}";
                files.Add(new() { Path = path, Payload = payload, Sha256 = hash, OriginalSha256 = old });
                copies.Add((translated, SafePaths.Resolve(output, payload)));
                if (old is not null) fingerprints[path] = new() { Path = path, Sha256 = old };
            }
            foreach (var path in source.FingerprintFiles)
            { var original = SafePaths.Resolve(originalRoot, path); fingerprints[path] = new() { Path = path, Sha256 = SafePaths.Hash(original) }; }
            variants.Add(new() { Id = source.Id, Label = source.Label, Storefront = source.Storefront, SteamBuildIds = source.SteamBuildIds, Files = files, Fingerprints = fingerprints.Values.ToList() });
        }
        var manifest = new PatchManifest { GameId = config.GameId, GameTitle = config.GameTitle, Version = config.Version, SteamAppId = config.SteamAppId, ExecutableNames = config.ExecutableNames, Variants = variants };
        PatchArchive.Validate(manifest); // Do all source validation before creating an output directory.
        foreach (var (source, target) in copies) { token.ThrowIfCancellationRequested(); SafePaths.CopyAtomic(source, target); }
        JsonData.Write(Path.Combine(output, "manifest.json"), manifest);
        progress?.Report(new($"已生成 {variants.Count} 个变体，{copies.Count} 个补丁文件", 1));
    }
    public static void Pack(string source, string output, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        var manifestPath = Path.Combine(source, "manifest.json");
        var manifest = JsonData.Read<PatchManifest>(manifestPath);
        PatchArchive.Validate(manifest);
        var files = manifest.Variants.SelectMany(v => v.Files).DistinctBy(f => f.Payload, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in files)
            if (!SafePaths.HashEquals(SafePaths.Hash(SafePaths.Resolve(source, file.Payload)), file.Sha256)) throw new IOException($"文件校验失败：{file.Payload}");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output)) throw new IOException("输出包已存在，请使用新文件名。");
        var temporary = output + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.Optimal);
                foreach (var file in files) { token.ThrowIfCancellationRequested(); progress?.Report(new("压缩 · " + file.Path)); zip.CreateEntryFromFile(SafePaths.Resolve(source, file.Payload), file.Payload, CompressionLevel.Optimal); }
            }
            using var verified = PatchArchive.Open(temporary, Path.GetTempPath(), progress, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, false);
            progress?.Report(new("补丁打包与校验完成", 1));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record CaptureConfig
{
    public required string GameId { get; init; }
    public required string GameTitle { get; init; }
    public required string Version { get; init; }
    public string? SteamAppId { get; init; }
    public string[] ExecutableNames { get; init; } = [];
    public List<CaptureVariant> Variants { get; init; } = [];
}
public sealed record CaptureVariant
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string OriginalDirectory { get; init; }
    public required string TranslatedDirectory { get; init; }
    public string Storefront { get; init; } = "any";
    public string[] SteamBuildIds { get; init; } = [];
    public string[] Files { get; init; } = [];
    public string[] FingerprintFiles { get; init; } = [];
}
