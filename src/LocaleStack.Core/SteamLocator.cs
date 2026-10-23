using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace LocaleStack.Core;

public static class ValveKeyValues
{
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]|[^\\s{}\"]+")
            .Select(m => m.Value).Where(x => !x.StartsWith("//", StringComparison.Ordinal)).ToArray();
        var index = 0;
        string Decode(string value) => value.StartsWith('"') ? value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : value;
        Dictionary<string, object> Object(bool nested)
        {
            var output = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (index < tokens.Length)
            {
                if (tokens[index] == "}") { if (!nested) throw new InvalidDataException("VDF 花括号不匹配。"); index++; return output; }
                var key = Decode(tokens[index++]);
                if (index == tokens.Length || tokens[index] == "}") throw new InvalidDataException("VDF 缺少值。");
                object value;
                if (tokens[index] == "{") { index++; value = Object(true); }
                else value = Decode(tokens[index++]);
                if (!output.TryAdd(key, value)) throw new InvalidDataException("VDF 包含重复键。");
            }
            if (nested) throw new InvalidDataException("VDF 花括号不匹配。");
            return output;
        }
        return Object(false);
    }
}

public static class SteamLocator
{
    public static IReadOnlyList<GameLocation> Find(string appId, IEnumerable<string>? steamRoots = null)
    {
        if (appId.Length == 0 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("无效 Steam App ID。", nameof(appId));
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots ?? DefaultRoots())
        {
            if (!Directory.Exists(root)) continue;
            libraries.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                var parsed = ValveKeyValues.Parse(File.ReadAllText(vdf));
                if (parsed.GetValueOrDefault("libraryfolders") is not Dictionary<string, object> folders) continue;
                foreach (var (key, value) in folders)
                {
                    if (!key.All(char.IsAsciiDigit)) continue;
                    var path = value is string legacy ? legacy : (value as Dictionary<string, object>)?.GetValueOrDefault("path") as string;
                    if (!string.IsNullOrWhiteSpace(path)) libraries.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        var locations = new List<GameLocation>();
        foreach (var library in libraries)
        {
            var manifest = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
            var location = ReadManifest(manifest, appId);
            if (location is not null) locations.Add(location);
        }
        return locations.DistinctBy(x => x.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static GameLocation? ReadManifest(string manifest, string appId)
    {
        if (!File.Exists(manifest)) return null;
        try
        {
            if (ValveKeyValues.Parse(File.ReadAllText(manifest)).GetValueOrDefault("AppState") is not Dictionary<string, object> state ||
                state.GetValueOrDefault("appid") as string != appId || state.GetValueOrDefault("installdir") is not string folder) return null;
            SafePaths.Relative(folder);
            if (folder.Contains('/')) return null;
            var path = SafePaths.Resolve(Path.Combine(Path.GetDirectoryName(manifest)!, "common"), folder);
            return Directory.Exists(path) ? new(path, "steam", state.GetValueOrDefault("buildid") as string) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
    public static GameLocation Describe(string directory, string? appId)
    {
        directory = Path.GetFullPath(directory);
        if (appId is not null)
        {
            var steamapps = Directory.GetParent(directory)?.Parent?.FullName;
            if (steamapps is not null)
            {
                var found = ReadManifest(Path.Combine(steamapps, "appmanifest_" + appId + ".acf"), appId);
                if (found is not null && Path.GetFullPath(found.Directory).Equals(directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return found;
            }
        }
        return new(directory, "standalone");
    }
    private static IEnumerable<string> DefaultRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            foreach (var name in new[] { "SteamPath", "InstallPath" })
                if (Registry.GetValue(key, name, null) is string path) roots.Add(path);
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return roots;
    }
}
