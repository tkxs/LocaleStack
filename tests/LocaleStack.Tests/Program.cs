using System.IO.Compression;
using System.Net;
using System.Text;
using LocaleStack.Core;

var tests = new List<(string Name, Func<Task> Test)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }
async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected rejection"); }

Test("standalone: install, reinstall, update with removed file, restore", () =>
{
    using var f = new Fixture();
    var installer = new PatchInstaller();
    using (var first = f.Archive("1.0.0", true))
    {
        Equal(true, installer.Inspect(first.Manifest, f.Location).Compatible);
        installer.Install(first, f.Location);
        Equal("译文1.0.0", File.ReadAllText(f.GameFile));
        Equal("新增", File.ReadAllText(Path.Combine(f.Game, "data", "zh.txt")));
        installer.Install(first, f.Location); // must not overwrite the original backup with translated content
    }
    using (var second = f.Archive("1.1.0", false)) installer.Install(second, f.Location);
    Equal(false, File.Exists(Path.Combine(f.Game, "data", "zh.txt")));
    Equal("1.1.0", PatchInstaller.ReadState(f.Game, "example-game")!.Version);
    installer.Restore(f.Game, "example-game", []);
    Equal("original", File.ReadAllText(f.GameFile));
    Equal<InstallState?>(null, PatchInstaller.ReadState(f.Game, "example-game"));
});
Test("mismatched clean game rejected without writes", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0"); File.WriteAllText(f.GameFile, "new-game-build");
    Throws(() => new PatchInstaller().Install(archive, f.Location)); Equal("new-game-build", File.ReadAllText(f.GameFile));
});
Test("modified installed file prevents update and restore", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0"); var installer = new PatchInstaller(); installer.Install(archive, f.Location);
    File.WriteAllText(f.GameFile, "external mod"); Throws(() => installer.Restore(f.Game, "example-game", [])); Throws(() => installer.Install(archive, f.Location)); Equal("external mod", File.ReadAllText(f.GameFile));
});
Test("corrupted backup blocks restore", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0"); var installer = new PatchInstaller(); installer.Install(archive, f.Location);
    var backup = Directory.GetFiles(Path.Combine(f.Game, ".localestack", "example-game", "backups")).Single(); File.WriteAllText(backup, "broken");
    Throws(() => installer.Restore(f.Game, "example-game", [])); Equal("译文1.0.0", File.ReadAllText(f.GameFile));
});
Test("failed install rolls back all files and state", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0", true);
    var installer = new PatchInstaller(n => { if (n == 2) throw new IOException("simulated disk failure"); });
    Throws(() => installer.Install(archive, f.Location)); Equal("original", File.ReadAllText(f.GameFile));
    Equal(false, File.Exists(Path.Combine(f.Game, "data", "zh.txt"))); Equal<InstallState?>(null, PatchInstaller.ReadState(f.Game, "example-game"));
});
Test("failed upgrade restores previous installed version", () =>
{
    using var f = new Fixture(); using var first = f.Archive("1.0.0"); new PatchInstaller().Install(first, f.Location);
    using var second = f.Archive("1.1.0"); var installer = new PatchInstaller(n => { if (n == 1) throw new IOException("simulated"); });
    Throws(() => installer.Install(second, f.Location)); Equal("译文1.0.0", File.ReadAllText(f.GameFile)); Equal("1.0.0", PatchInstaller.ReadState(f.Game, "example-game")!.Version);
});
Test("failed restore rolls back to translated version", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0", true); new PatchInstaller().Install(archive, f.Location);
    var installer = new PatchInstaller(n => { if (n == 2) throw new IOException("simulated"); });
    Throws(() => installer.Restore(f.Game, "example-game", [])); Equal("译文1.0.0", File.ReadAllText(f.GameFile)); Equal("1.0.0", PatchInstaller.ReadState(f.Game, "example-game")!.Version);
});
Test("unexpected existing new-file path is rejected", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0", true); Directory.CreateDirectory(Path.Combine(f.Game, "data")); File.WriteAllText(Path.Combine(f.Game, "data", "zh.txt"), "user data");
    Throws(() => new PatchInstaller().Install(archive, f.Location)); Equal("original", File.ReadAllText(f.GameFile));
});
Test("downgrade rejected", () =>
{
    using var f = new Fixture(); using var newer = f.Archive("2.0.0"); using var older = f.Archive("1.0.0");
    var installer = new PatchInstaller(); installer.Install(newer, f.Location); Throws(() => installer.Install(older, f.Location));
});
Test("Steam source/build requirement and standalone fallback", () =>
{
    using var f = new Fixture(); var manifest = f.Manifest("1.0.0"); var steamVariant = manifest.Variants[0] with { Storefront = "steam", SteamBuildIds = ["26147432"] };
    manifest = manifest with { SteamAppId = "5195950", Variants = [steamVariant] }; var installer = new PatchInstaller();
    Equal(false, installer.Inspect(manifest, f.Location).Compatible);
    Equal(false, installer.Inspect(manifest, new(f.Game, "steam", "old")).Compatible);
    Equal(true, installer.Inspect(manifest, new(f.Game, "steam", "26147432")).Compatible);
    var standalone = steamVariant with { Id = "standalone", Storefront = "standalone", SteamBuildIds = [] };
    Equal(true, installer.Inspect(manifest with { Variants = [steamVariant, standalone] }, f.Location).Compatible);
});
Test("ambiguous variants rejected", () =>
{
    using var f = new Fixture(); var manifest = f.Manifest("1.0.0"); manifest.Variants.Add(manifest.Variants[0] with { Id = "second" }); Equal(false, new PatchInstaller().Inspect(manifest, f.Location).Compatible);
});
Test("exclusive game operation lock", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0"); using var gate = Transactions.Lock(f.Game); Throws(() => new PatchInstaller().Install(archive, f.Location));
});
Test("recovery replays a persisted interrupted transaction", () =>
{
    using var f = new Fixture(); var directory = Path.Combine(f.Game, ".localestack", "transactions", "interrupted"); Directory.CreateDirectory(directory);
    File.Copy(f.GameFile, Path.Combine(directory, "0.bak")); var before = SafePaths.Hash(f.GameFile); File.WriteAllText(f.GameFile, "partial");
    JsonData.Write(Path.Combine(directory, "journal.json"), new Journal([new("game.dat", true, before, SafePaths.Hash(f.GameFile), "0.bak")]));
    Transactions.Recover(f.Game); Equal("original", File.ReadAllText(f.GameFile)); Equal(false, Directory.Exists(directory));
});
Test("recovery protects unrelated changes made after interruption", () =>
{
    using var f = new Fixture(); var directory = Path.Combine(f.Game, ".localestack", "transactions", "interrupted"); Directory.CreateDirectory(directory); File.Copy(f.GameFile, Path.Combine(directory, "0.bak"));
    JsonData.Write(Path.Combine(directory, "journal.json"), new Journal([new("game.dat", true, SafePaths.Hash(f.GameFile), new string('a', 64), "0.bak")]));
    File.WriteAllText(f.GameFile, "user edit after crash"); Throws(() => Transactions.Recover(f.Game)); Equal("user edit after crash", File.ReadAllText(f.GameFile)); Equal(true, Directory.Exists(directory));
});
Test("transaction refuses preflight/write race", () =>
{
    using var f = new Fixture(); var hash = SafePaths.Hash(f.GameFile); var source = Path.Combine(f.Root, "source"); File.WriteAllText(source, "patch"); File.WriteAllText(f.GameFile, "changed");
    Throws(() => Transactions.Apply(f.Game, [new("game.dat", source, hash)], null)); Equal("changed", File.ReadAllText(f.GameFile));
});
foreach (var path in new[] { "../outside", "/absolute", "C:/file", "a\\b", "a/./b", "data/../b", "data/file:stream", ".localestack/state.json", "con.txt", "data/NUL", "data/LPT1.json", "name. ", "a//b" })
    Test("unsafe path: " + path, () => Throws(() => SafePaths.Relative(path)));
Test("malicious ZIP traversal rejected", () =>
{
    using var f = new Fixture(); var zipPath = Path.Combine(f.Root, "bad.zip"); using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) zip.CreateEntry("../escape"); Throws(() => PatchArchive.Open(zipPath, f.Root));
});
Test("duplicate case-insensitive ZIP entries rejected", () =>
{
    using var f = new Fixture(); var zipPath = Path.Combine(f.Root, "bad.zip"); using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { zip.CreateEntry("file"); zip.CreateEntry("FILE"); } Throws(() => PatchArchive.Open(zipPath, f.Root));
});
Test("bad payload SHA rejected", () =>
{
    using var f = new Fixture(); var path = f.Package("1.0.0"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { zip.GetEntry("payload/game.dat")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("payload/game.dat").Open()); writer.Write("corrupt"); } Throws(() => PatchArchive.Open(path, f.Root));
});
Test("manifest unknown fields and unsupported schema rejected", () =>
{
    using var f = new Fixture(); Throws(() => PatchArchive.Validate(f.Manifest("1.0.0") with { SchemaVersion = 2 }));
    var path = Path.Combine(f.Root, "unknown.json"); File.WriteAllText(path, "{\"schemaVersion\":1,\"repository\":\"tkxs/LocaleStack\",\"games\":[],\"runScript\":\"bad\"}"); Throws(() => JsonData.Read<Catalog>(path));
});
Test("cancellation before commit preserves game", () =>
{
    using var f = new Fixture(); using var archive = f.Archive("1.0.0"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Throws(() => new PatchInstaller().Install(archive, f.Location, token: cancellation.Token)); Equal("original", File.ReadAllText(f.GameFile));
});
Test("Steam multiple libraries, old/new VDF, installed path and build id", () =>
{
    using var f = new Fixture(); var steam = Path.Combine(f.Root, "Steam"); var library = Path.Combine(f.Root, "Library"); Directory.CreateDirectory(Path.Combine(steam, "steamapps")); Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "Example Game"));
    string Quote(string path) => path.Replace("\\", "\\\\");
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Quote(steam)}\" }} \"1\" {{ \"path\" \"{Quote(library)}\" \"apps\" {{ \"123\" \"10\" }} }} }}");
    File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_123.acf"), "\"AppState\" { \"appid\" \"123\" \"installdir\" \"Example Game\" \"buildid\" \"456\" }");
    var found = SteamLocator.Find("123", [steam]).Single(); Equal("456", found.SteamBuildId); Equal("steam", SteamLocator.Describe(found.Directory, "123").Storefront);
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" \"{Quote(library)}\" }}"); Equal(1, SteamLocator.Find("123", [steam]).Count);
});
Test("Steam malformed appmanifest cannot escape common directory", () =>
{
    using var f = new Fixture(); var steam = Path.Combine(f.Root, "Steam"); Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
    File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_123.acf"), "\"AppState\" { \"appid\" \"123\" \"installdir\" \"../../outside\" }"); Equal(0, SteamLocator.Find("123", [steam]).Count);
});
Test("numeric stable versions", () => { Equal(true, ReleaseVersion.IsNewer("1.10.0", "1.9.0")); Equal(false, ReleaseVersion.IsNewer("1.0.0", "1.0.0")); Throws(() => ReleaseVersion.Parse("latest")); Throws(() => ReleaseVersion.Parse("1.0.0-beta")); });
Test("official source URL restrictions", () =>
{
    GitHubSource.ValidateAssetUrl("https://github.com/tkxs/LocaleStack/releases/download/game-v1.0.0/game.lspatch");
    foreach (var url in new[] { "http://github.com/tkxs/LocaleStack/releases/download/x/y", "https://github.com/evil/LocaleStack/releases/download/x/y", "https://github.com.evil.test/tkxs/LocaleStack/releases/download/x/y", "https://user@github.com/tkxs/LocaleStack/releases/download/x/y", "https://github.com:444/tkxs/LocaleStack/releases/download/x/y" }) Throws(() => GitHubSource.ValidateAssetUrl(url));
});
AsyncTest("download hash, size, cache reuse", async () =>
{
    using var f = new Fixture(); var bytes = Encoding.UTF8.GetBytes("download-test"); var original = Path.Combine(f.Root, "download"); File.WriteAllBytes(original, bytes); var calls = 0;
    using var source = new GitHubSource(new FakeHandler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
    var release = new PatchRelease { Version = "1.0.0", DownloadUrl = "https://github.com/tkxs/LocaleStack/releases/download/test/file", Sha256 = SafePaths.Hash(original), Bytes = bytes.Length };
    var cache = Path.Combine(f.Root, "cache"); var result = await source.DownloadAsync(release, cache); Equal(release.Sha256, SafePaths.Hash(result)); await source.DownloadAsync(release, cache); Equal(1, calls);
});
AsyncTest("bad download SHA deletes partial file", async () =>
{
    using var f = new Fixture(); using var source = new GitHubSource(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
    var release = new PatchRelease { Version = "1.0.0", DownloadUrl = "https://github.com/tkxs/LocaleStack/releases/download/test/file", Sha256 = new string('a', 64), Bytes = 3 }; var cache = Path.Combine(f.Root, "cache");
    await ThrowsAsync(() => source.DownloadAsync(release, cache)); Equal(0, Directory.GetFiles(cache).Length);
});
AsyncTest("redirect downgrade/foreign host blocked", async () =>
{
    using var f = new Fixture(); using var source = new GitHubSource(new FakeHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.test/file"); return response; }));
    await ThrowsAsync(() => source.FetchCatalogAsync());
});
AsyncTest("manager updater verifies newer semantic version and GitHub digest", async () =>
{
    var json = "{\"tag_name\":\"v1.10.0\",\"assets\":[{\"name\":\"LocaleStack-Setup-win-x64.exe\",\"browser_download_url\":\"https://github.com/tkxs/LocaleStack/releases/download/v1.10.0/LocaleStack-Setup-win-x64.exe\",\"digest\":\"sha256:" + new string('a', 64) + "\",\"size\":200}]}";
    using var source = new GitHubSource(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
    Equal("1.10.0", (await source.CheckAppUpdateAsync("1.9.0"))!.Version); Equal<AppRelease?>(null, await source.CheckAppUpdateAsync("1.10.0"));
});
AsyncTest("catalog validates planned versus available state", async () =>
{
    using var source = new GitHubSource(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"schemaVersion\":1,\"repository\":\"tkxs/LocaleStack\",\"games\":[{\"id\":\"game\",\"title\":\"Game\",\"status\":\"available\",\"patch\":null}]}") })); await ThrowsAsync(() => source.FetchCatalogAsync());
});

tests.AddRange(MaintainerTests.Cases);
var failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
}
sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "localestack-tests-" + Guid.NewGuid().ToString("N"));
    public string Game => Path.Combine(Root, "游戏 with spaces");
    public string GameFile => Path.Combine(Game, "game.dat");
    public GameLocation Location => new(Game, "standalone");
    private readonly string originalHash;
    public Fixture() { Directory.CreateDirectory(Game); File.WriteAllText(GameFile, "original"); originalHash = SafePaths.Hash(GameFile); }
    public PatchManifest Manifest(string version, bool extra = false)
    {
        var payload = Path.Combine(Root, "payload-" + version); File.WriteAllText(payload, "译文" + version);
        List<PatchFile> files = [new() { Path = "game.dat", Payload = "payload/game.dat", Sha256 = SafePaths.Hash(payload), OriginalSha256 = originalHash }];
        if (extra)
        {
            var added = Path.Combine(Root, "added"); File.WriteAllText(added, "新增"); files.Add(new() { Path = "data/zh.txt", Payload = "payload/data/zh.txt", Sha256 = SafePaths.Hash(added), OriginalSha256 = null });
        }
        return new() { GameId = "example-game", GameTitle = "Example", Version = version, Variants = [new() { Id = "any", Label = "Any build", Files = files, Fingerprints = [new() { Path = "game.dat", Sha256 = originalHash }] }] };
    }
    public string Package(string version, bool extra = false)
    {
        var manifest = Manifest(version, extra); var json = Path.Combine(Root, "manifest.json"); JsonData.Write(json, manifest);
        var zipPath = Path.Combine(Root, Guid.NewGuid() + ".lspatch");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create); zip.CreateEntryFromFile(json, "manifest.json"); zip.CreateEntryFromFile(Path.Combine(Root, "payload-" + version), "payload/game.dat");
        if (extra) zip.CreateEntryFromFile(Path.Combine(Root, "added"), "payload/data/zh.txt"); return zipPath;
    }
    public PatchArchive Archive(string version, bool extra = false) => PatchArchive.Open(Package(version, extra), Root);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
