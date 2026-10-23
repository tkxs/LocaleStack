using System.IO.Compression;
using LocaleStack.Core;

return await Cli.Run(args);

static class Cli
{
    public static Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "help") { Help(); return Task.FromResult(0); }
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
            { if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length || !options.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("参数应使用 --name value，且不能重复。"); }
            string Get(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException("缺少 --" + key);
            switch (args[0])
            {
                case "capture": Capture(JsonData.Read<CaptureConfig>(Get("config")), Get("output")); break;
                case "pack": Pack(Get("source"), Get("output")); break;
                case "verify": using (var archive = PatchArchive.Open(Get("package"), Path.GetTempPath())) Console.WriteLine($"通过：{archive.Manifest.GameId} {archive.Manifest.Version}，{archive.Manifest.Variants.Count} 个变体"); break;
                case "catalog-add": UpdateCatalog(Get("catalog"), Get("game-id"), Get("package"), Get("url"), options.GetValueOrDefault("notes") ?? ""); break;
                case "catalog-validate": GitHubSource.ValidateCatalog(JsonData.Read<Catalog>(Get("catalog"))); Console.WriteLine("目录格式验证通过。"); break;
                case "inspect":
                    using (var archive = PatchArchive.Open(Get("package"), Path.GetTempPath()))
                    {
                        var result = new PatchInstaller().Inspect(archive.Manifest, SteamLocator.Describe(Get("game-directory"), archive.Manifest.SteamAppId));
                        Console.WriteLine(result.Message); return Task.FromResult(result.Compatible ? 0 : 2);
                    }
                default: throw new ArgumentException("未知命令：" + args[0]);
            }
            return Task.FromResult(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return Task.FromResult(1); }
    }
    private static void Help() => Console.WriteLine("""
        译栈维护工具 0.1.0
        capture --config release.json --output staging/game
          对比原版与汉化文件，按配置生成标准清单和 payload；不修改输入目录。
        pack --source staging/game --output artifacts/game-1.0.0.lspatch
        verify --package artifacts/game-1.0.0.lspatch
        inspect --package game.lspatch --game-directory D:/Games/Example
        catalog-add --catalog catalog/catalog.json --game-id example --package game.lspatch
                    --url https://github.com/tkxs/LocaleStack/releases/download/example-v1.0.0/game.lspatch --notes 更新说明
        catalog-validate --catalog catalog/catalog.json
        """);
    public static void Capture(CaptureConfig config, string output)
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
                var original = SafePaths.Resolve(originalRoot, path);
                var translated = SafePaths.Resolve(translatedRoot, path);
                if (!File.Exists(translated)) throw new FileNotFoundException("汉化文件不存在", translated);
                var hash = SafePaths.Hash(translated);
                var old = File.Exists(original) ? SafePaths.Hash(original) : null;
                if (SafePaths.HashEquals(hash, old)) { Console.WriteLine("跳过未改变文件：" + path); continue; }
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
        foreach (var (source, target) in copies) SafePaths.CopyAtomic(source, target);
        JsonData.Write(Path.Combine(output, "manifest.json"), manifest);
        Console.WriteLine($"已生成 {variants.Count} 个变体，{copies.Count} 个补丁文件：{Path.GetFullPath(output)}");
    }
    public static void Pack(string source, string output)
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
        try
        {
            using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.Optimal);
                foreach (var file in files) zip.CreateEntryFromFile(SafePaths.Resolve(source, file.Payload), file.Payload, CompressionLevel.Optimal);
            }
            using var verified = PatchArchive.Open(output, Path.GetTempPath());
            Console.WriteLine($"打包成功：{output}\nSHA-256：{SafePaths.Hash(output)}\n大小：{new FileInfo(output).Length} 字节");
        }
        catch { File.Delete(output); throw; }
    }
    private static void UpdateCatalog(string catalogPath, string gameId, string package, string url, string notes)
    {
        GitHubSource.ValidateAssetUrl(url);
        var catalog = JsonData.Read<Catalog>(catalogPath);
        GitHubSource.ValidateCatalog(catalog);
        var index = catalog.Games.FindIndex(g => g.Id == gameId);
        if (index < 0) throw new ArgumentException("请先在目录中添加游戏基本信息。");
        using var verified = PatchArchive.Open(package, Path.GetTempPath());
        if (verified.Manifest.GameId != gameId) throw new InvalidDataException("包的 gameId 与目录不一致。");
        var game = catalog.Games[index];
        if (game.Patch is { } previous && !ReleaseVersion.IsNewer(verified.Manifest.Version, previous.Version)) throw new InvalidDataException("发布版本必须高于目录当前版本。");
        catalog.Games[index] = game with { Status = "available", Patch = new() { Version = verified.Manifest.Version, DownloadUrl = url, Sha256 = SafePaths.Hash(package), Bytes = new FileInfo(package).Length, Notes = notes, Variants = verified.Manifest.Variants.Select(v => v.Label).ToArray() } };
        GitHubSource.ValidateCatalog(catalog);
        JsonData.Write(catalogPath, catalog);
        Console.WriteLine("目录已更新；请先上传 Release 附件，再提交目录，避免玩家遇到空下载地址。");
    }
}

sealed record CaptureConfig
{
    public required string GameId { get; init; }
    public required string GameTitle { get; init; }
    public required string Version { get; init; }
    public string? SteamAppId { get; init; }
    public string[] ExecutableNames { get; init; } = [];
    public List<CaptureVariant> Variants { get; init; } = [];
}
sealed record CaptureVariant
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
