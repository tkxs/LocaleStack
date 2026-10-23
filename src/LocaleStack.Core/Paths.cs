using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LocaleStack.Core;

public static partial class SafePaths
{
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$")]
    private static partial Regex HashPattern();
    public static void CheckId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || !IdPattern().IsMatch(id))
            throw new InvalidDataException($"无效的游戏或变体 ID：{id}");
    }
    public static void CheckHash(string hash)
    {
        if (!HashPattern().IsMatch(hash)) throw new InvalidDataException("需要 64 位 SHA-256。");
    }
    public static string Relative(string relative, bool internalPath = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 220 || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new InvalidDataException($"必须使用以 / 分隔的相对路径：{relative}");
        var parts = relative.Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException($"不安全的文件路径：{relative}");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, @"^(COM|LPT)[0-9¹²³]$"))
                throw new InvalidDataException($"路径含 Windows 保留名称：{relative}");
        }
        if (!internalPath && parts[0].Equals(".localestack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("补丁不得修改译栈的备份或安装状态。");
        return relative;
    }
    public static string Resolve(string root, string relative, bool internalPath = false)
    {
        Relative(relative, internalPath);
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出游戏目录。");
        RejectLinks(result);
        return result;
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"请使用真实目录，路径中含符号链接或联接点：{current}");
    }
    public static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    public static bool HashEquals(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public static void CopyAtomic(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".localestack-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
