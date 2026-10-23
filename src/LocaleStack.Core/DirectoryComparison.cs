namespace LocaleStack.Core;

public sealed record ComparedFile(string Path, string? OriginalSha256, string TranslatedSha256, long Bytes)
{
    public string Change => OriginalSha256 is null ? "新增" : "修改";
}
public sealed record DirectoryComparison(string OriginalDirectory, string TranslatedDirectory, List<ComparedFile> Files, string[] SuggestedExecutables);

public static class DirectoryComparer
{
    public static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static DirectoryComparison Compare(string originalDirectory, string translatedDirectory, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        var original = Path.GetFullPath(originalDirectory.Trim());
        var translated = Path.GetFullPath(translatedDirectory.Trim());
        if (!Directory.Exists(original) || !Directory.Exists(translated)) throw new DirectoryNotFoundException("请选择存在的原版目录和汉化目录。");
        SafePaths.RejectLinks(original); SafePaths.RejectLinks(translated);
        if (original.Equals(translated, StringComparison.OrdinalIgnoreCase)) throw new IOException("原版目录和汉化目录必须不同。");
        var paths = new List<string>();
        var directories = new Stack<string>(); directories.Push(translated);
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".localestack", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_zh_cn_backup", StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(child).Equals(original, StringComparison.OrdinalIgnoreCase)) continue;
                SafePaths.RejectLinks(child); directories.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var relative = Path.GetRelativePath(translated, file).Replace('\\', '/');
                SafePaths.Relative(relative); SafePaths.RejectLinks(file); paths.Add(relative);
                if (paths.Count > 100000) throw new IOException("文件超过 100,000 个，请选择仅包含汉化输出的目录。");
            }
        }
        var changed = new List<ComparedFile>();
        var index = 0;
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("对比文件 · " + path, ++index / (double)paths.Count));
            var source = SafePaths.Resolve(original, path);
            var target = SafePaths.Resolve(translated, path);
            if (Directory.Exists(source)) throw new IOException("原版目录中的同名路径是文件夹：" + path);
            var hash = SafePaths.Hash(target);
            var oldHash = File.Exists(source) ? SafePaths.Hash(source) : null;
            if (!SafePaths.HashEquals(hash, oldHash)) changed.Add(new(path, oldHash, hash, new FileInfo(target).Length));
        }
        var executables = Directory.EnumerateFiles(original, "*.exe").Select(Path.GetFileName).OfType<string>().Order().ToArray();
        return new(original, translated, changed, executables);
    }
    public static void VerifySelection(DirectoryComparison comparison, IEnumerable<ComparedFile> selected, CancellationToken token = default)
    {
        foreach (var file in selected)
        {
            token.ThrowIfCancellationRequested();
            var original = SafePaths.Resolve(comparison.OriginalDirectory, file.Path);
            var translated = SafePaths.Resolve(comparison.TranslatedDirectory, file.Path);
            if (!File.Exists(translated) || !SafePaths.HashEquals(SafePaths.Hash(translated), file.TranslatedSha256) ||
                !SafePaths.HashEquals(File.Exists(original) ? SafePaths.Hash(original) : null, file.OriginalSha256))
                throw new IOException("扫描后文件发生了变化，请重新扫描：" + file.Path);
        }
    }
}
