using System.Diagnostics;

namespace LocaleStack.Core;

public sealed class PatchInstaller
{
    private readonly Action<int>? afterWrite;
    public PatchInstaller() { }
    internal PatchInstaller(Action<int> afterWrite) => this.afterWrite = afterWrite;
    private static string StatePath(string gameId) => $".localestack/{gameId}/state.json";
    private static string BackupPath(string gameId, string hash) => $".localestack/{gameId}/backups/{hash.ToLowerInvariant()}";
    public static InstallState? ReadState(string root, string gameId)
    {
        SafePaths.CheckId(gameId);
        var path = SafePaths.Resolve(root, StatePath(gameId), true);
        if (!File.Exists(path)) return null;
        var state = JsonData.Read<InstallState>(path);
        if (state.SchemaVersion != 1 || state.GameId != gameId || state.Files.Count == 0) throw new InvalidDataException("安装记录无效。");
        ReleaseVersion.Parse(state.Version);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in state.Files)
        {
            SafePaths.Relative(file.Path); SafePaths.CheckHash(file.InstalledSha256);
            if (file.OriginalSha256 is not null) SafePaths.CheckHash(file.OriginalSha256);
            if (!paths.Add(file.Path)) throw new InvalidDataException("安装记录含重复文件。");
        }
        return state;
    }
    public CompatibilityResult Inspect(PatchManifest manifest, GameLocation location)
    {
        PatchArchive.Validate(manifest);
        if (!Directory.Exists(location.Directory)) throw new DirectoryNotFoundException("游戏目录不存在。");
        using var gate = Transactions.Lock(location.Directory);
        Transactions.Recover(location.Directory);
        var state = ReadState(location.Directory, manifest.GameId);
        ValidateInstalled(location.Directory, state);
        var matches = manifest.Variants.Where(v => Matches(manifest, v, location, state)).ToArray();
        return matches.Length switch
        {
            1 => new(true, "兼容 · " + matches[0].Label, matches[0].Id),
            0 => new(false, "文件指纹或游戏来源不匹配。请确认选择了游戏根目录，并使用对应游戏版本的补丁。"),
            _ => new(false, "多个变体同时匹配，请维护者为这些变体增加独立的文件指纹。")
        };
    }
    private static void ValidateInstalled(string root, InstallState? state)
    {
        if (state is null) return;
        foreach (var file in state.Files)
        {
            var target = SafePaths.Resolve(root, file.Path);
            if (!File.Exists(target) || !SafePaths.HashEquals(SafePaths.Hash(target), file.InstalledSha256))
                throw new IOException($"已安装文件被游戏更新或其他补丁修改，已停止以免覆盖：{file.Path}\n请先核对文件，必要时用游戏平台验证文件完整性。");
            if (file.OriginalSha256 is { } hash)
            {
                var backup = SafePaths.Resolve(root, BackupPath(state.GameId, hash), true);
                if (!File.Exists(backup) || !SafePaths.HashEquals(SafePaths.Hash(backup), hash)) throw new IOException($"原版备份缺失或损坏：{file.Path}");
            }
        }
    }
    private static string? OriginalHash(string root, string path, InstallState? state)
    {
        var previous = state?.Files.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (previous is not null) return previous.OriginalSha256;
        var target = SafePaths.Resolve(root, path);
        if (Directory.Exists(target)) throw new IOException($"目标文件的位置是文件夹：{path}");
        return File.Exists(target) ? SafePaths.Hash(target) : null;
    }
    private static bool Matches(PatchManifest manifest, PatchVariant variant, GameLocation location, InstallState? state)
    {
        if (variant.Storefront != "any" && variant.Storefront != location.Storefront) return false;
        if (variant.SteamBuildIds.Length > 0 && (location.SteamBuildId is null || !variant.SteamBuildIds.Contains(location.SteamBuildId))) return false;
        if (variant.Storefront == "steam" && manifest.SteamAppId is null) return false;
        return variant.Fingerprints.All(fp => SafePaths.HashEquals(OriginalHash(location.Directory, fp.Path, state), fp.Sha256)) &&
            variant.Files.All(file => SafePaths.HashEquals(OriginalHash(location.Directory, file.Path, state), file.OriginalSha256));
    }
    public void Install(PatchArchive archive, GameLocation location, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        var manifest = archive.Manifest;
        var root = location.Directory;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("游戏目录不存在。");
        using var gate = Transactions.Lock(root);
        Transactions.Recover(root, progress);
        EnsureNotRunning(root, manifest.ExecutableNames);
        var state = ReadState(root, manifest.GameId);
        ValidateInstalled(root, state);
        if (state is not null && ReleaseVersion.Parse(manifest.Version) < ReleaseVersion.Parse(state.Version)) throw new IOException("不支持直接降级汉化。请先恢复原版，再安装旧版本。");
        progress?.Report(new("检查游戏文件与兼容变体"));
        var matches = manifest.Variants.Where(v => Matches(manifest, v, location, state)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException(matches.Length == 0 ? "此补丁与游戏文件不兼容，未修改任何游戏文件。" : "多个变体匹配，无法安全选择。");
        var variant = matches[0];
        var mutations = new List<Mutation>();
        var installed = new List<InstalledFile>();
        foreach (var file in variant.Files)
        {
            token.ThrowIfCancellationRequested();
            var source = SafePaths.Resolve(archive.DirectoryPath, file.Payload);
            if (!SafePaths.HashEquals(SafePaths.Hash(source), file.Sha256)) throw new IOException("安装前补丁内容发生变化。");
            if (file.OriginalSha256 is { } originalHash)
            {
                var backup = SafePaths.Resolve(root, BackupPath(manifest.GameId, originalHash), true);
                if (File.Exists(backup))
                { if (!SafePaths.HashEquals(SafePaths.Hash(backup), originalHash)) throw new IOException("原版备份校验失败。"); }
                else
                {
                    progress?.Report(new("备份原版 · " + file.Path));
                    var original = SafePaths.Resolve(root, file.Path);
                    if (!SafePaths.HashEquals(SafePaths.Hash(original), originalHash)) throw new IOException("备份前原版文件发生变化。");
                    SafePaths.CopyAtomic(original, backup);
                    if (!SafePaths.HashEquals(SafePaths.Hash(backup), originalHash)) throw new IOException("备份写入校验失败。");
                }
            }
            installed.Add(new() { Path = file.Path, InstalledSha256 = file.Sha256, OriginalSha256 = file.OriginalSha256 });
            var previous = state?.Files.FirstOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            mutations.Add(new(file.Path, source, previous?.InstalledSha256 ?? file.OriginalSha256));
        }
        // An update may remove files from a previous patch. Restore them to their clean-game state.
        foreach (var old in state?.Files ?? [])
            if (!installed.Any(x => x.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase)))
                mutations.Add(new(old.Path, old.OriginalSha256 is { } hash ? SafePaths.Resolve(root, BackupPath(manifest.GameId, hash), true) : null, old.InstalledSha256));
        var newState = new InstallState() { GameId = manifest.GameId, Version = manifest.Version, VariantId = variant.Id, Files = installed };
        var stateSource = Path.Combine(archive.DirectoryPath, "new-state.json");
        JsonData.Write(stateSource, newState);
        var oldStatePath = SafePaths.Resolve(root, StatePath(manifest.GameId), true);
        mutations.Add(new(StatePath(manifest.GameId), stateSource, File.Exists(oldStatePath) ? SafePaths.Hash(oldStatePath) : null));
        token.ThrowIfCancellationRequested();
        EnsureNotRunning(root, manifest.ExecutableNames);
        progress?.Report(new("开始安装，完成前请勿启动游戏"));
        // Cancellation stops at the commit boundary; after it, finish atomically or roll back.
        Transactions.Apply(root, mutations, progress, afterWrite);
        progress?.Report(new("汉化安装完成", 1));
    }
    public void Restore(string root, string gameId, string[] executableNames, IProgress<OperationProgress>? progress = null)
    {
        SafePaths.CheckId(gameId);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("游戏目录不存在。");
        using var gate = Transactions.Lock(root);
        Transactions.Recover(root, progress);
        EnsureNotRunning(root, executableNames);
        var state = ReadState(root, gameId) ?? throw new IOException("此目录没有译栈安装记录。");
        ValidateInstalled(root, state);
        var mutations = state.Files.Select(file => new Mutation(file.Path,
            file.OriginalSha256 is { } hash ? SafePaths.Resolve(root, BackupPath(gameId, hash), true) : null, file.InstalledSha256)).ToList();
        mutations.Add(new(StatePath(gameId), null, SafePaths.Hash(SafePaths.Resolve(root, StatePath(gameId), true))));
        Transactions.Apply(root, mutations, progress, afterWrite);
        progress?.Report(new("已恢复原版，原版备份继续保留", 1));
    }
    public static void EnsureNotRunning(string root, string[] names)
    {
        var candidates = names.Length > 0 ? names : Directory.GetFiles(root, "*.exe").Select(Path.GetFileName).OfType<string>().ToArray();
        foreach (var name in candidates)
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
        {
            using (process)
            {
                try
                {
                    var exe = process.MainModule?.FileName;
                    if (exe is not null && !Path.GetFullPath(exe).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch (System.ComponentModel.Win32Exception) { /* If access is denied, conservatively require closing this process. */ }
                catch (InvalidOperationException) { continue; }
                throw new IOException($"请先关闭游戏（{name}），再安装或恢复。");
            }
        }
    }
}
