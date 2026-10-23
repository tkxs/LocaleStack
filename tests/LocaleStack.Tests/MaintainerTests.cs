using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocaleStack.Core;

internal static class MaintainerTests
{
    private const string Credential = "synthetic-test-token";
    public static IEnumerable<(string Name, Func<Task> Test)> Cases =>
    [
        ("authoring comparison skips framework metadata and original backup subtree", Compare),
        ("authoring snapshot rejects edits made after scanning", ScanRace),
        ("shared authoring builds multiple storefront variants", MultiVariant),
        ("shared packer preserves an existing output and cleans cancelled work", ExistingOutput),
        ("publisher creates draft, uploads verified bytes, publishes then commits catalog", Publish),
        ("publisher retry reuses matching attachment without duplicate uploads", Retry),
        ("publisher finds an uploaded draft through authenticated release listing", DraftRetry),
        ("publisher merges concurrent catalog edits after SHA conflict", Conflict),
        ("publisher rejects newer and conflicting versions before remote mutations", VersionConflict),
        ("publisher rejects corrupted cloud attachment without catalog write", BadDigest),
        ("publisher retains public package and resumes after catalog failure", CatalogFailure),
        ("publisher handles a catalog commit whose response was lost", AmbiguousCommit),
        ("publisher checks repository write permission", Permissions),
        ("publisher refuses authenticated API redirects", Redirect),
        ("publisher never exposes supplied credential in wrapped upload errors", SecretRedaction)
    ];
    private static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static async Task<T> Failure<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
    private static PublicationMetadata Metadata => new("example-game", "Example Game", "Game summary", "Release notes", null, []);
    private static Task Compare()
    {
        using var f = new Fixture(); var translated = Path.Combine(f.Root, "translated"); Directory.CreateDirectory(translated);
        File.WriteAllText(Path.Combine(translated, "game.dat"), "changed"); File.WriteAllText(Path.Combine(translated, "added.txt"), "added");
        Directory.CreateDirectory(Path.Combine(translated, ".git")); File.WriteAllText(Path.Combine(translated, ".git", "config"), "private");
        Directory.CreateDirectory(Path.Combine(translated, "_zh_cn_backup_old")); File.WriteAllText(Path.Combine(translated, "_zh_cn_backup_old", "old.dat"), "original-copy");
        var result = DirectoryComparer.Compare(f.Game, translated);
        Check(result.Files.Count == 2 && result.Files.Single(x => x.Path == "added.txt").OriginalSha256 is null);
        var nested = DirectoryComparer.Compare(f.Game, f.Root);
        Check(nested.Files.All(x => !x.Path.StartsWith("游戏 with spaces/", StringComparison.Ordinal)), "Original subtree leaked into comparison");
        Check(File.ReadAllText(f.GameFile) == "original"); return Task.CompletedTask;
    }
    private static Task ScanRace()
    {
        using var f = new Fixture(); var translated = Path.Combine(f.Root, "translated"); Directory.CreateDirectory(translated); File.WriteAllText(Path.Combine(translated, "game.dat"), "changed");
        var scan = DirectoryComparer.Compare(f.Game, translated); File.WriteAllText(Path.Combine(translated, "game.dat"), "changed again");
        try { DirectoryComparer.VerifySelection(scan, scan.Files); } catch (IOException) { return Task.CompletedTask; }
        throw new Exception("A changed source was accepted");
    }
    private static Task MultiVariant()
    {
        using var f = new Fixture(); var translated = Path.Combine(f.Root, "translated"); Directory.CreateDirectory(translated); File.WriteAllText(Path.Combine(translated, "game.dat"), "中文");
        var config = new CaptureConfig
        {
            GameId = "example-game", GameTitle = "Example", Version = "1.0.0", SteamAppId = "123",
            Variants = [
                new() { Id = "steam", Label = "Steam", Storefront = "steam", SteamBuildIds = ["100"], OriginalDirectory = f.Game, TranslatedDirectory = translated, Files = ["game.dat"] },
                new() { Id = "standalone", Label = "Standalone", Storefront = "standalone", OriginalDirectory = f.Game, TranslatedDirectory = translated, Files = ["game.dat"] }
            ]
        };
        var staging = Path.Combine(f.Root, "staging"); var output = Path.Combine(f.Root, "package.lspatch");
        PatchAuthoring.Capture(config, staging); PatchAuthoring.Pack(staging, output);
        using var archive = PatchArchive.Open(output, f.Root);
        var inspector = new PatchInstaller(); Check(inspector.Inspect(archive.Manifest, f.Location).VariantId == "standalone");
        Check(inspector.Inspect(archive.Manifest, new(f.Game, "steam", "100")).VariantId == "steam");
        return Task.CompletedTask;
    }
    private static async Task Publish()
    {
        using var f = new Fixture(); var package = f.Package("1.0.0"); var cloud = new Cloud(); using var publisher = new GitHubPublisher(Credential, cloud);
        Check(await publisher.ConnectAsync() == "test-maintainer"); var result = await publisher.PublishAsync(package, Metadata);
        Check(cloud.Uploads == 1 && cloud.CatalogWrites == 1 && !cloud.Draft && result.CatalogCommitUrl.EndsWith("test-commit", StringComparison.Ordinal));
        Check(cloud.Catalog.Games.Single(x => x.Id == "example-game").Patch!.Sha256 == SafePaths.Hash(package));
        Check(cloud.Catalog.Games.Any(x => x.Id == "unrelated-game"));
        Check(cloud.Events.IndexOf("upload") < cloud.Events.IndexOf("publish") && cloud.Events.IndexOf("publish") < cloud.Events.IndexOf("catalog-write"));
    }
    private static Task ExistingOutput()
    {
        using var f = new Fixture(); var translated = Path.Combine(f.Root, "translated"); Directory.CreateDirectory(translated); File.WriteAllText(Path.Combine(translated, "game.dat"), "changed");
        var staging = Path.Combine(f.Root, "staging"); var output = Path.Combine(f.Root, "existing.lspatch");
        PatchAuthoring.Capture(new() { GameId = "example-game", GameTitle = "Example", Version = "1.0.0", Variants = [new() { Id = "any", Label = "Any", OriginalDirectory = f.Game, TranslatedDirectory = translated, Files = ["game.dat"] }] }, staging);
        File.WriteAllText(output, "existing output");
        try { PatchAuthoring.Pack(staging, output); throw new Exception("Overwrite accepted"); } catch (IOException) { Check(File.ReadAllText(output) == "existing output"); }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var cancelled = Path.Combine(f.Root, "cancelled.lspatch");
        try { PatchAuthoring.Pack(staging, cancelled, token: cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(!File.Exists(cancelled) && !Directory.GetFiles(f.Root, "*.partial-*").Any()); }
        var raced = Path.Combine(f.Root, "raced.lspatch");
        var progress = new ImmediateProgress(p => { if (p.Message.StartsWith("压缩", StringComparison.Ordinal) && !File.Exists(raced)) File.WriteAllText(raced, "another writer"); });
        try { PatchAuthoring.Pack(staging, raced, progress); throw new Exception("Concurrent output overwritten"); } catch (IOException) { Check(File.ReadAllText(raced) == "another writer" && !Directory.GetFiles(f.Root, "*.partial-*").Any()); }
        return Task.CompletedTask;
    }
    private sealed class ImmediateProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => report(value); }
    private static async Task Retry()
    {
        using var f = new Fixture(); var package = f.Package("1.0.0"); var cloud = new Cloud(); using var publisher = new GitHubPublisher(Credential, cloud);
        await publisher.PublishAsync(package, Metadata); var result = await publisher.PublishAsync(package, Metadata);
        Check(result.AlreadyPublished && cloud.Uploads == 1 && cloud.CatalogWrites == 1);
    }
    private static async Task DraftRetry()
    {
        using var f = new Fixture(); var cloud = new Cloud { RejectPublishOnce = true }; using var publisher = new GitHubPublisher(Credential, cloud); var package = f.Package("1.0.0");
        await Failure<PublicationException>(() => publisher.PublishAsync(package, Metadata)); Check(cloud.Draft && cloud.Uploads == 1);
        await publisher.PublishAsync(package, Metadata); Check(!cloud.Draft && cloud.Uploads == 1 && cloud.CatalogWrites == 1);
    }
    private static async Task Conflict()
    {
        using var f = new Fixture(); var cloud = new Cloud { ConflictOnce = true }; using var publisher = new GitHubPublisher(Credential, cloud);
        await publisher.PublishAsync(f.Package("1.0.0"), Metadata);
        Check(cloud.Catalog.Games.Any(g => g.Id == "concurrent-game") && cloud.Catalog.Games.Any(g => g.Id == "example-game"));
        Check(cloud.CatalogAttempts == 2 && cloud.Uploads == 1);
    }
    private static async Task VersionConflict()
    {
        using var f = new Fixture(); var package = f.Package("1.0.0");
        foreach (var version in new[] { "2.0.0", "1.0.0" })
        {
            var cloud = new Cloud(); cloud.Catalog.Games.Add(new() { Id = "example-game", Title = "Example", Status = "available", Patch = new() { Version = version, DownloadUrl = "https://github.com/tkxs/LocaleStack/releases/download/old/file.lspatch", Sha256 = new string('a', 64), Bytes = 123 } });
            using var publisher = new GitHubPublisher(Credential, cloud); await Failure<InvalidDataException>(() => publisher.PublishAsync(package, Metadata)); Check(cloud.Writes == 0);
        }
    }
    private static async Task BadDigest()
    {
        using var f = new Fixture(); var cloud = new Cloud { CorruptDigest = true }; using var publisher = new GitHubPublisher(Credential, cloud);
        var error = await Failure<PublicationException>(() => publisher.PublishAsync(f.Package("1.0.0"), Metadata));
        Check(!error.PackagePublished && cloud.Draft && cloud.CatalogWrites == 0 && cloud.Uploads == 1 && cloud.Deletes == 0);
    }
    private static async Task CatalogFailure()
    {
        using var f = new Fixture(); var cloud = new Cloud { RejectCatalog = true }; using var publisher = new GitHubPublisher(Credential, cloud); var package = f.Package("1.0.0");
        var error = await Failure<PublicationException>(() => publisher.PublishAsync(package, Metadata)); Check(error.PackagePublished && !cloud.Draft && cloud.CatalogWrites == 0);
        cloud.RejectCatalog = false; await publisher.PublishAsync(package, Metadata); Check(cloud.Uploads == 1 && cloud.CatalogWrites == 1 && File.Exists(package));
    }
    private static async Task AmbiguousCommit()
    {
        using var f = new Fixture(); var cloud = new Cloud { LoseCommitResponse = true }; using var publisher = new GitHubPublisher(Credential, cloud); var package = f.Package("1.0.0");
        await Failure<PublicationException>(() => publisher.PublishAsync(package, Metadata));
        var result = await publisher.PublishAsync(package, Metadata); Check(result.AlreadyPublished && cloud.CatalogWrites == 1 && cloud.Uploads == 1);
    }
    private static async Task Permissions()
    {
        var cloud = new Cloud { CanPush = false }; using var publisher = new GitHubPublisher(Credential, cloud);
        await Failure<IOException>(() => publisher.ConnectAsync()); Check(cloud.Writes == 0);
    }
    private static async Task Redirect()
    {
        var count = 0;
        using var publisher = new GitHubPublisher(Credential, new Handler(_ => { count++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://evil.test/steal"); return response; }));
        await Failure<HttpRequestException>(() => publisher.ConnectAsync()); Check(count == 1);
    }
    private static async Task SecretRedaction()
    {
        using var f = new Fixture(); var cloud = new Cloud { ThrowSecretOnUpload = true }; using var publisher = new GitHubPublisher(Credential, cloud);
        var error = await Failure<PublicationException>(() => publisher.PublishAsync(f.Package("1.0.0"), Metadata)); Check(!error.ToString().Contains(Credential, StringComparison.Ordinal));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(handler(request)); }
    private sealed class Cloud : HttpMessageHandler
    {
        public Catalog Catalog { get; private set; } = new() { Games = [new() { Id = "unrelated-game", Title = "Unrelated", Status = "planned" }] };
        public bool ConflictOnce, RejectCatalog, CorruptDigest, LoseCommitResponse, ThrowSecretOnUpload, RejectPublishOnce;
        public bool CanPush = true, Draft = true;
        private bool exists;
        private string assetName = "", digest = "", sha = "initial-sha";
        private long bytes;
        public int Writes, Uploads, CatalogWrites, CatalogAttempts, Deletes;
        public List<string> Events { get; } = [];
        private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        private object Asset() => new { name = assetName, state = "uploaded", size = bytes, digest = "sha256:" + digest };
        private object Release() => new { id = 42, tag_name = "patch-example-game-v1.0.0", draft = Draft, prerelease = false, assets = assetName.Length > 0 ? new[] { Asset() } : Array.Empty<object>() };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Headers.Authorization?.Parameter == Credential, "Missing authentication");
            var path = request.RequestUri!.AbsolutePath; var method = request.Method;
            Check(request.RequestUri.Scheme == "https" && request.RequestUri.Host is "api.github.com" or "uploads.github.com", "Credential leaked to a foreign host");
            if (method != HttpMethod.Get) Writes++;
            if (method == HttpMethod.Delete) { Deletes++; throw new Exception("Unexpected destructive request"); }
            if (path == "/user") return Json(new { login = "test-maintainer" });
            if (path == "/repos/tkxs/LocaleStack") return Json(new { full_name = "tkxs/LocaleStack", archived = false, permissions = new { push = CanPush } });
            if (path.EndsWith("/contents/catalog/catalog.json", StringComparison.Ordinal) && method == HttpMethod.Get)
                return Json(new { encoding = "base64", sha, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Catalog, JsonData.Options))) });
            if (path.Contains("/releases/tags/", StringComparison.Ordinal)) return exists && !Draft ? Json(Release()) : Json(new { message = "Not Found" }, HttpStatusCode.NotFound);
            if (path.EndsWith("/releases", StringComparison.Ordinal) && method == HttpMethod.Get) return Json(exists ? new[] { Release() } : Array.Empty<object>());
            if (path.EndsWith("/releases", StringComparison.Ordinal) && method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Check(body.RootElement.GetProperty("draft").GetBoolean() && body.RootElement.GetProperty("make_latest").GetString() == "false"); exists = true; Events.Add("create-draft"); return Json(Release(), HttpStatusCode.Created);
            }
            if (request.RequestUri.Host == "uploads.github.com")
            {
                if (ThrowSecretOnUpload) throw new IOException(Credential);
                Check(request.Content!.Headers.ContentType?.MediaType == "application/octet-stream", "Upload content type not set before sending");
                var uploaded = await request.Content.ReadAsByteArrayAsync(token); bytes = uploaded.Length;
                digest = CorruptDigest ? new string('a', 64) : Convert.ToHexStringLower(SHA256.HashData(uploaded));
                assetName = Uri.UnescapeDataString(request.RequestUri.Query.Split("name=", 2)[1]); Uploads++; Events.Add("upload"); return Json(Asset(), HttpStatusCode.Created);
            }
            if (path.EndsWith("/releases/42", StringComparison.Ordinal) && method == HttpMethod.Patch)
            {
                if (RejectPublishOnce) { RejectPublishOnce = false; return Json(new { message = "temporary failure" }, HttpStatusCode.ServiceUnavailable); }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Check(body.RootElement.GetProperty("make_latest").GetString() == "false");
                Draft = false; Events.Add("publish"); return Json(Release());
            }
            if (path.EndsWith("/contents/catalog/catalog.json", StringComparison.Ordinal) && method == HttpMethod.Put)
            {
                CatalogAttempts++;
                if (RejectCatalog) return Json(new { message = "Permission denied" }, HttpStatusCode.Forbidden);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (ConflictOnce)
                { ConflictOnce = false; Catalog.Games.Add(new() { Id = "concurrent-game", Title = "Concurrent", Status = "planned" }); sha = "changed-sha"; return Json(new { message = "SHA mismatch" }, HttpStatusCode.Conflict); }
                Check(body.RootElement.GetProperty("sha").GetString() == sha, "Stale catalog SHA");
                Catalog = JsonSerializer.Deserialize<Catalog>(Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!), JsonData.Options)!;
                CatalogWrites++; Events.Add("catalog-write"); sha = "new-sha";
                if (LoseCommitResponse) { LoseCommitResponse = false; throw new HttpRequestException("Response lost after successful commit"); }
                return Json(new { commit = new { html_url = "https://github.com/tkxs/LocaleStack/commit/test-commit" } });
            }
            throw new Exception("Unexpected request: " + method + " " + path);
        }
    }
}
