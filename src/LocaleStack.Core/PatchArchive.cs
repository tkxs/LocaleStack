using System.IO.Compression;
using System.Text.Json;

namespace LocaleStack.Core;

public sealed class PatchArchive : IDisposable
{
    public const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    public PatchManifest Manifest { get; }
    public string DirectoryPath { get; }
    private PatchArchive(PatchManifest manifest, string directory) { Manifest = manifest; DirectoryPath = directory; }
    public static void Validate(PatchManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("补丁格式版本不受支持，请更新译栈。");
        SafePaths.CheckId(manifest.GameId);
        ReleaseVersion.Parse(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.GameTitle) || manifest.Language != "zh-CN") throw new InvalidDataException("请填写游戏名称和 zh-CN 目标语言。");
        if (manifest.SteamAppId is { } appId && (appId.Length == 0 || !appId.All(char.IsAsciiDigit))) throw new InvalidDataException("Steam App ID 必须是数字。");
        foreach (var exe in manifest.ExecutableNames)
        { SafePaths.Relative(exe); if (exe.Contains('/') || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("进程名称应是 exe 文件名。"); }
        if (manifest.Variants.Count is < 1 or > 32) throw new InvalidDataException("补丁必须包含 1–32 个兼容变体。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in manifest.Variants)
        {
            SafePaths.CheckId(variant.Id);
            if (string.IsNullOrWhiteSpace(variant.Label)) throw new InvalidDataException("变体名称不能为空。");
            if (!ids.Add(variant.Id)) throw new InvalidDataException("变体 ID 重复。");
            if (variant.Storefront is not ("any" or "steam" or "standalone")) throw new InvalidDataException("未知游戏来源。");
            if (variant.SteamBuildIds.Length > 0 && (variant.Storefront != "steam" || manifest.SteamAppId is null || variant.SteamBuildIds.Any(x => x.Length == 0 || !x.All(char.IsAsciiDigit))))
                throw new InvalidDataException("Build ID 限制只能用于有 App ID 的 Steam 变体。");
            if (variant.Files.Count is < 1 or > 10000 || variant.Fingerprints.Count < 1) throw new InvalidDataException("每个变体需要文件和原版文件指纹。");
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in variant.Files)
            {
                SafePaths.Relative(file.Path); SafePaths.Relative(file.Payload); SafePaths.CheckHash(file.Sha256);
                if (!file.Payload.StartsWith("payload/", StringComparison.Ordinal)) throw new InvalidDataException("补丁内容应存放在 payload/ 内。");
                if (!filePaths.Add(file.Path)) throw new InvalidDataException("补丁目标文件重复。");
                if (file.OriginalSha256 is not null) SafePaths.CheckHash(file.OriginalSha256);
                if (payloadHashes.TryGetValue(file.Payload, out var hash) && !SafePaths.HashEquals(hash, file.Sha256)) throw new InvalidDataException("同一 payload 的校验值不一致。");
                payloadHashes[file.Payload] = file.Sha256;
            }
            var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fp in variant.Fingerprints)
            {
                SafePaths.Relative(fp.Path); SafePaths.CheckHash(fp.Sha256);
                if (!fingerprints.Add(fp.Path)) throw new InvalidDataException("文件指纹重复。");
                var replacement = variant.Files.FirstOrDefault(x => x.Path.Equals(fp.Path, StringComparison.OrdinalIgnoreCase));
                if (replacement is not null && !SafePaths.HashEquals(replacement.OriginalSha256, fp.Sha256)) throw new InvalidDataException("文件指纹与原版哈希矛盾。");
            }
            var allPaths = filePaths.Concat(fingerprints).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in allPaths)
                for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                    if (allPaths.Contains(path[..slash])) throw new InvalidDataException("文件路径不能同时是其他文件的父目录。");
        }
    }
    public static PatchArchive Open(string path, string scratchRoot, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        var directory = Path.Combine(scratchRoot, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > 11000) throw new InvalidDataException("压缩包文件过多。");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                SafePaths.Relative(entry.FullName);
                if (entry.Length > 2L * 1024 * 1024 * 1024 || (expanded = checked(expanded + entry.Length)) > MaxExpandedBytes) throw new InvalidDataException("解压大小超过格式限制。");
                if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("压缩包含重复路径。");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包不得含符号链接。");
            }
            if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException("缺少有效的 manifest.json。");
            using var manifestStream = manifestEntry.Open();
            var manifest = JsonSerializer.Deserialize<PatchManifest>(manifestStream, JsonData.Options) ?? throw new InvalidDataException("补丁清单为空。");
            Validate(manifest);
            var files = manifest.Variants.SelectMany(x => x.Files).DistinctBy(x => x.Payload, StringComparer.OrdinalIgnoreCase).ToArray();
            if (entries.Count != files.Length + 1) throw new InvalidDataException("压缩包中含未声明的文件或目录条目。");
            var index = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(file.Payload, out var entry)) throw new InvalidDataException($"缺少补丁内容：{file.Payload}");
                var target = SafePaths.Resolve(directory, file.Payload);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
                if (!SafePaths.HashEquals(SafePaths.Hash(target), file.Sha256)) throw new InvalidDataException($"补丁内容校验失败：{file.Payload}");
                progress?.Report(new("校验补丁内容", ++index / (double)files.Length));
            }
            return new(manifest, directory);
        }
        catch { System.IO.Directory.Delete(directory, true); throw; }
    }
    public void Dispose() { if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, true); }
}
