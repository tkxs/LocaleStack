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
                case "capture": PatchAuthoring.Capture(JsonData.Read<CaptureConfig>(Get("config")), Get("output")); Console.WriteLine("补丁清单与 payload 已生成：" + Get("output")); break;
                case "pack": PatchAuthoring.Pack(Get("source"), Get("output")); Console.WriteLine($"打包成功：{Get("output")}\nSHA-256：{SafePaths.Hash(Get("output"))}"); break;
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
        译栈维护工具
        capture --config release.json --output staging/game
          对比原版与汉化文件，按配置生成标准清单和 payload；不修改输入目录。
        pack --source staging/game --output artifacts/game-1.0.0.lspatch
        verify --package artifacts/game-1.0.0.lspatch
        inspect --package game.lspatch --game-directory D:/Games/Example
        catalog-add --catalog catalog/catalog.json --game-id example --package game.lspatch
                    --url https://github.com/tkxs/LocaleStack/releases/download/example-v1.0.0/game.lspatch --notes 更新说明
        catalog-validate --catalog catalog/catalog.json
        """);
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
