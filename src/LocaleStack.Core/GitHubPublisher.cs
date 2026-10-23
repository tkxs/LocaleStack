using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LocaleStack.Core;

public sealed record PublishResult(string ReleaseUrl, string CatalogCommitUrl, bool AlreadyPublished);
public sealed class PublicationException(string message, bool packagePublished, Exception inner) : IOException(message, inner)
{
    public bool PackagePublished { get; } = packagePublished;
}

public sealed class GitHubPublisher : IDisposable
{
    private const string Api = "https://api.github.com/repos/" + GitHubSource.Repository;
    private readonly HttpClient http;
    private readonly string credential;
    public GitHubPublisher(string token) : this(token, new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal GitHubPublisher(string token, HttpMessageHandler handler)
    {
        token = token.Trim();
        if (token.Length is < 8 or > 2048 || token.Any(char.IsControl)) throw new ArgumentException("请输入有效的 GitHub 访问令牌。");
        credential = token;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new("LocaleStack-Maintainer", AppInfo.Version));
        http.DefaultRequestHeaders.Accept.Add(new("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }
    public async Task<string> ConnectAsync(CancellationToken token = default)
    {
        using var user = await SendJsonAsync(HttpMethod.Get, "https://api.github.com/user", null, token).ConfigureAwait(false);
        using var repository = await SendJsonAsync(HttpMethod.Get, Api, null, token).ConfigureAwait(false);
        var root = repository!.RootElement;
        if (!string.Equals(root.GetProperty("full_name").GetString(), GitHubSource.Repository, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub 返回了其他仓库。");
        if (root.TryGetProperty("archived", out var archived) && archived.GetBoolean()) throw new IOException("仓库已归档，不能发布。");
        if (root.TryGetProperty("permissions", out var permissions) && permissions.TryGetProperty("push", out var push) && !push.GetBoolean())
            throw new IOException("此账号或令牌没有仓库写入权限。请授权 LocaleStack 仓库的 Contents: Read and write。");
        return user!.RootElement.GetProperty("login").GetString() ?? throw new InvalidDataException("GitHub 账号信息不完整。");
    }
    private async Task<JsonDocument?> SendJsonAsync(HttpMethod method, string url, object? body, CancellationToken token, bool allowMissing = false)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        return await ReadJsonAsync(response, token).ConfigureAwait(false);
    }
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        // Never log request headers, credentials, or raw server bodies.
        var hint = code switch
        {
            401 => "GitHub 令牌无效或已过期，请重新连接。",
            403 => "GitHub 拒绝写入。请检查 Contents: Read and write 权限、仓库分支保护或 API 限流。",
            404 => "GitHub 资源不存在，或令牌未获授权访问 LocaleStack 仓库。",
            409 => "远程目录在发布期间发生了变化。",
            422 => "GitHub 拒绝了重复或不满足仓库规则的写入。",
            429 => "GitHub 暂时限流，请稍后重试。",
            >= 300 and < 400 => "GitHub API 返回了重定向，已停止转发访问令牌。",
            _ => "GitHub 请求未完成，请保留本地补丁后重试。"
        };
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        throw new HttpRequestException($"{hint}（HTTP {code}）", null, response.StatusCode);
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int max = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("GitHub 响应过大。");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (memory.Length + count > max) throw new InvalidDataException("GitHub 响应过大。"); await memory.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        return JsonDocument.Parse(memory.ToArray());
    }
    private async Task<(Catalog Catalog, string Sha)> ReadCatalogAsync(CancellationToken token)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, Api + "/contents/catalog/catalog.json?ref=main", null, token).ConfigureAwait(false);
        var root = document!.RootElement;
        if (root.GetProperty("encoding").GetString() != "base64") throw new InvalidDataException("远程目录编码不受支持。");
        var bytes = Convert.FromBase64String(root.GetProperty("content").GetString()!);
        var catalog = JsonSerializer.Deserialize<Catalog>(bytes, JsonData.Options) ?? throw new InvalidDataException("远程目录为空。");
        GitHubSource.ValidateCatalog(catalog);
        return (catalog, root.GetProperty("sha").GetString()!);
    }
    private async Task<JsonDocument?> FindReleaseAsync(string tag, CancellationToken token)
    {
        var published = await SendJsonAsync(HttpMethod.Get, Api + "/releases/tags/" + Uri.EscapeDataString(tag), null, token, true).ConfigureAwait(false);
        if (published is not null) return published;
        // GitHub's by-tag endpoint is for published releases. Drafts require the authenticated list endpoint.
        for (var page = 1; page <= 100; page++)
        {
            using var listing = await SendJsonAsync(HttpMethod.Get, Api + $"/releases?per_page=100&page={page}", null, token).ConfigureAwait(false);
            foreach (var entry in listing!.RootElement.EnumerateArray())
                if (entry.GetProperty("tag_name").GetString() == tag) return JsonDocument.Parse(entry.GetRawText());
            if (listing.RootElement.GetArrayLength() < 100) return null;
        }
        throw new IOException("Release 列表过长，无法确认是否存在同名草稿。请在 GitHub 检查该版本后再发布。");
    }
    public async Task<PublishResult> PublishAsync(string packagePath, PublicationMetadata metadata, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        // Keep the immutable local package open throughout the operation, including uncertain upload retries.
        using var packageLock = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = await Task.Run(() => PatchArchive.Open(packagePath, Path.GetTempPath(), progress, token), token).ConfigureAwait(false);
        var manifest = archive.Manifest;
        var tag = $"patch-{manifest.GameId}-v{manifest.Version}";
        var name = Path.GetFileName(packagePath);
        var releaseUrl = "https://github.com/" + GitHubSource.Repository + "/releases/tag/" + tag;
        var release = new PatchRelease
        {
            Version = manifest.Version, DownloadUrl = "https://github.com/" + GitHubSource.Repository + "/releases/download/" + tag + "/" + Uri.EscapeDataString(name),
            Sha256 = SafePaths.Hash(packagePath), Bytes = packageLock.Length, Notes = metadata.Notes, Variants = manifest.Variants.Select(v => v.Label).ToArray()
        };
        if (release.Bytes > 2147483648) throw new IOException("单个 GitHub 补丁包不能超过 2 GiB。");
        progress?.Report(new("检查远程目录与版本"));
        var preflight = await ReadCatalogAsync(token).ConfigureAwait(false);
        CatalogEditing.Apply(preflight.Catalog, manifest, metadata, release); // Reject version conflicts before any cloud mutation.
        var published = false;
        try
        {
            using var existing = await FindReleaseAsync(tag, token).ConfigureAwait(false);
            using var created = existing is null ? await SendJsonAsync(HttpMethod.Post, Api + "/releases", new
            {
                tag_name = tag, target_commitish = "main", name = metadata.Title + " 汉化 " + manifest.Version,
                body = metadata.Notes, draft = true, prerelease = false, make_latest = "false"
            }, token).ConfigureAwait(false) : null;
            var cloud = (existing ?? created)!.RootElement;
            if (cloud.GetProperty("tag_name").GetString() != tag || cloud.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("远程 Release 的版本信息不一致。");
            var id = cloud.GetProperty("id").GetInt64();
            var draft = cloud.GetProperty("draft").GetBoolean();
            JsonElement? matchingAsset = null;
            foreach (var asset in cloud.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == name) { matchingAsset = asset; break; }
            if (matchingAsset is { } oldAsset) ValidateAsset(oldAsset, release);
            else
            {
                if (!draft) throw new IOException("此版本已公开发布但缺少同名附件，请检查 Release 或递增版本号。");
                progress?.Report(new("正在上传补丁", 0));
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://uploads.github.com/repos/{GitHubSource.Repository}/releases/{id}/assets?name={Uri.EscapeDataString(name)}");
                request.Content = new UploadContent(packagePath, progress);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                await EnsureSuccessAsync(response, token).ConfigureAwait(false);
                using var asset = await ReadJsonAsync(response, token).ConfigureAwait(false);
                ValidateAsset(asset.RootElement, release);
            }
            if (draft)
            {
                progress?.Report(new("附件校验通过，发布汉化 Release"));
                using var visible = await SendJsonAsync(HttpMethod.Patch, Api + "/releases/" + id,
                    new { draft = false, prerelease = false, make_latest = "false", body = metadata.Notes }, token).ConfigureAwait(false);
                if (visible!.RootElement.GetProperty("draft").GetBoolean()) throw new IOException("GitHub 仍将此 Release 标记为草稿。");
            }
            published = true;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                progress?.Report(new("补丁已发布，正在更新游戏目录"));
                var current = await ReadCatalogAsync(token).ConfigureAwait(false);
                if (!CatalogEditing.Apply(current.Catalog, manifest, metadata, release)) return new(releaseUrl, "https://github.com/" + GitHubSource.Repository + "/blob/main/catalog/catalog.json", true);
                try
                {
                    using var commit = await SendJsonAsync(HttpMethod.Put, Api + "/contents/catalog/catalog.json", new
                    {
                        message = $"Publish {manifest.GameId} localization {manifest.Version}",
                        content = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(current.Catalog, JsonData.Options))),
                        sha = current.Sha, branch = "main"
                    }, token).ConfigureAwait(false);
                    progress?.Report(new("发布完成，玩家同步目录后即可安装", 1));
                    return new(releaseUrl, commit!.RootElement.GetProperty("commit").GetProperty("html_url").GetString()!, false);
                }
                catch (HttpRequestException ex) when (attempt < 2 && ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity) { }
            }
            throw new IOException("远程目录持续发生冲突，请稍后重试。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var safeMessage = ex.Message.Replace(credential, "[令牌已隐藏]", StringComparison.Ordinal);
            throw new PublicationException((published ? "补丁已上传并发布，但游戏目录尚未更新。保留本地包，点击发布即可重试。\n" : "发布未完成，本地包已保留。可以重试相同包。\n") + safeMessage, published, new IOException(safeMessage));
        }
    }
    private static void ValidateAsset(JsonElement asset, PatchRelease release)
    {
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("size").GetInt64() != release.Bytes ||
            digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !SafePaths.HashEquals(digest[7..], release.Sha256))
            throw new IOException("远程同名附件的 SHA-256 或大小与本地包不一致。没有覆盖或删除远程文件；请检查草稿附件，或使用新的版本号。");
    }
    public void Dispose() => http.Dispose();

    private sealed class UploadContent : HttpContent
    {
        private readonly string path;
        private readonly IProgress<OperationProgress>? progress;
        private readonly long length;
        public UploadContent(string path, IProgress<OperationProgress>? progress)
        {
            this.path = path; this.progress = progress; length = new FileInfo(path).Length;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            using var file = File.OpenRead(path); var buffer = new byte[131072]; long uploaded = 0; int read;
            while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); uploaded += read;
                progress?.Report(new($"上传补丁 · {uploaded / 1048576d:F1} / {length / 1048576d:F1} MB", uploaded / (double)length));
            }
        }
    }
}
