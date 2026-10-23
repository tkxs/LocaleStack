using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocaleStack.Core;

public sealed class GitHubSource : IDisposable
{
    public const string Repository = "tkxs/LocaleStack";
    public const string CatalogUrl = "https://raw.githubusercontent.com/" + Repository + "/main/catalog/catalog.json";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    private readonly HttpClient http;
    public GitHubSource() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal GitHubSource(HttpMessageHandler handler)
    {
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocaleStack", AppInfo.Version));
    }
    public static void ValidateAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("首版只接受 tkxs/LocaleStack 官方 GitHub Releases 下载地址。");
    }
    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                var next = location is null ? null : new Uri(new Uri(url), location);
                if (next is null || next.Scheme != "https" || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    !(next.Host == "github.com" || next.Host == "raw.githubusercontent.com" || next.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
                    throw new HttpRequestException("下载重定向到了非 GitHub 地址。");
                url = next.AbsoluteUri;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            { response.Dispose(); throw new HttpRequestException("GitHub 暂时限制了请求次数，请稍后重试。也可以导入已下载的离线补丁。"); }
            if (!response.IsSuccessStatusCode)
            { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException($"GitHub 请求失败（{(int)code}）。请检查网络或稍后重试。", null, code); }
            return response;
        }
        throw new HttpRequestException("下载重定向次数过多。");
    }
    public static void ValidateCatalog(Catalog catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Repository != Repository) throw new InvalidDataException("不支持的官方目录格式。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in catalog.Games)
        {
            SafePaths.CheckId(game.Id);
            if (!ids.Add(game.Id) || string.IsNullOrWhiteSpace(game.Title)) throw new InvalidDataException("游戏目录存在重复 ID 或空名称。");
            if (game.Status is not ("planned" or "available") || (game.Status == "available") != (game.Patch is not null)) throw new InvalidDataException("补丁发布状态与下载信息不一致。");
            if (game.SteamAppId is not null && (game.SteamAppId.Length == 0 || !game.SteamAppId.All(char.IsAsciiDigit))) throw new InvalidDataException("Steam App ID 无效。");
            if (game.Patch is { } patch)
            {
                ReleaseVersion.Parse(patch.Version); SafePaths.CheckHash(patch.Sha256); ValidateAssetUrl(patch.DownloadUrl);
                if (patch.Bytes is <= 0 or > 2147483648) throw new InvalidDataException("单个发行补丁包应在 1 B–2 GiB 之间。");
            }
        }
    }
    public async Task<Catalog> FetchCatalogAsync(CancellationToken token = default)
    {
        using var response = await GetAsync(CatalogUrl, token).ConfigureAwait(false);
        var data = await ReadLimitedAsync(response, 4 * 1024 * 1024, token).ConfigureAwait(false);
        var catalog = JsonSerializer.Deserialize<Catalog>(data, JsonData.Options) ?? throw new InvalidDataException("在线目录为空。");
        ValidateCatalog(catalog);
        return catalog;
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long max, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("在线信息大小超过限制。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { if (memory.Length + read > max) throw new InvalidDataException("在线信息大小超过限制。"); await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); }
        return memory.ToArray();
    }
    public async Task<string> DownloadAsync(PatchRelease release, string cache, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        ValidateAssetUrl(release.DownloadUrl); SafePaths.CheckHash(release.Sha256);
        if (release.Bytes is <= 0 or > 2147483648) throw new InvalidDataException("下载大小超出格式限制。");
        Directory.CreateDirectory(cache);
        var target = Path.Combine(cache, release.Sha256.ToLowerInvariant() + ".lspatch");
        if (File.Exists(target) && new FileInfo(target).Length == release.Bytes && SafePaths.HashEquals(SafePaths.Hash(target), release.Sha256)) return target;
        var part = target + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await GetAsync(release.DownloadUrl, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Bytes) throw new InvalidDataException("下载大小与目录记录不一致。");
            using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > release.Bytes) throw new InvalidDataException("下载超过声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    progress?.Report(new($"下载汉化 · {total / 1048576d:F1} / {release.Bytes / 1048576d:F1} MB", total / (double)release.Bytes));
                }
                if (total != release.Bytes) throw new InvalidDataException("下载未完成。");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            progress?.Report(new("校验下载文件"));
            if (!SafePaths.HashEquals(SafePaths.Hash(part), release.Sha256)) throw new InvalidDataException("SHA-256 校验失败，请重新下载。");
            File.Move(part, target, true);
            return target;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    public async Task<AppRelease?> CheckAppUpdateAsync(string currentVersion, CancellationToken token = default)
    {
        HttpResponseMessage response;
        try { response = await GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest", token).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        using (response)
        using (var document = JsonDocument.Parse(await ReadLimitedAsync(response, 2 * 1024 * 1024, token).ConfigureAwait(false)))
        {
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v')) return null;
            var version = tag[1..];
            try { if (!ReleaseVersion.IsNewer(version, currentVersion)) return null; } catch (InvalidDataException) { return null; }
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "LocaleStack-Setup-win-x64.exe") continue;
                var digest = asset.TryGetProperty("digest", out var property) ? property.GetString() : null;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) continue;
                var release = new PatchRelease { Version = version, DownloadUrl = asset.GetProperty("browser_download_url").GetString()!, Sha256 = digest[7..], Bytes = asset.GetProperty("size").GetInt64() };
                ValidateAssetUrl(release.DownloadUrl); SafePaths.CheckHash(release.Sha256);
                return new(version, release);
            }
            return null;
        }
    }
    public void Dispose() => http.Dispose();
}
public sealed record AppRelease(string Version, PatchRelease Installer);
