using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LocaleStack.Core;
using Microsoft.Win32;

namespace LocaleStack.App;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}
public sealed class AsyncCommand(Func<Task> action, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Refresh();
        try { await action(); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed record GameCard(GameEntry Entry)
{
    public string Availability => Entry.Status == "available" ? "可下载安装" : "等待补丁上传";
    public string VersionLabel => Entry.Patch is { } patch ? "v" + patch.Version : "尚未发布";
    public string SourceLabel => Entry.SteamAppId is null ? "手动定位 · 文件指纹匹配" : "Steam 自动定位 / 手动选择";
}
public sealed record LogEntry(string Time, string Message);
public sealed record Preferences
{
    public bool AutoCheck { get; set; } = true;
    public Dictionary<string, string> GameDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GameEntry> LocalGames { get; init; } = [];
}

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly GitHubSource source = new();
    private readonly PatchInstaller installer = new();
    private readonly Preferences preferences;
    private readonly Dictionary<string, (string Path, PatchManifest Manifest)> localPackages = [];
    private readonly List<AsyncCommand> commands = [];
    private CancellationTokenSource? cancellation;
    private AppRelease? appRelease;
    private GameCard? selectedGame;
    private string gameDirectory = "", search = "", page = "library", installedLabel = "未安装", locationLabel = "可自动查找 Steam，也可以手动选择其他来源的游戏目录。";
    private string statusMessage = "就绪 · 选择游戏开始", sourceStatus = "正在使用内置目录", appUpdateLabel = $"当前版本 v{AppInfo.Version}。更新安装程序会在校验后由你启动。";
    private bool isBusy;
    private double progressFraction;
    private bool isIndeterminate;
    private InstallState? installed;
    private MaintainerWindow? maintainerWindow;
    public bool MaintainerBusy => maintainerWindow?.IsOperationInProgress == true;
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaleStack");
    public string AppVersionLabel => $"v{AppInfo.Version}  ·  Windows x64";
    private string CacheDirectory => Path.Combine(DataDirectory, "cache");
    private string PreferencePath => Path.Combine(DataDirectory, "preferences.json");
    public ObservableCollection<GameCard> Games { get; } = [];
    public ICollectionView GamesView { get; }
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public string GameCountText => Games.Count + " 款游戏";
    public string PageTitle => page switch { "tasks" => "下载与任务", "help" => "使用说明", "settings" => "设置", _ => "我的汉化游戏库" };
    public bool IsLibrary => page == "library";
    public bool IsTasks => page == "tasks";
    public bool IsHelp => page == "help";
    public bool IsSettings => page == "settings";
    public bool IsBusy { get => isBusy; private set { Set(ref isBusy, value); RefreshCommands(); } }
    public double ProgressFraction { get => progressFraction; private set => Set(ref progressFraction, value); }
    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string SourceStatus { get => sourceStatus; private set => Set(ref sourceStatus, value); }
    public string InstalledLabel { get => installedLabel; private set => Set(ref installedLabel, value); }
    public string LocationLabel { get => locationLabel; private set => Set(ref locationLabel, value); }
    public string AppUpdateLabel { get => appUpdateLabel; private set => Set(ref appUpdateLabel, value); }
    public string Search { get => search; set { if (Set(ref search, value)) GamesView.Refresh(); } }
    public GameCard? SelectedGame
    {
        get => selectedGame;
        set
        {
            if (IsBusy && selectedGame is not null) { Changed(); return; }
            if (!Set(ref selectedGame, value)) return;
            GameDirectory = value is null ? "" : preferences.GameDirectories.GetValueOrDefault(value.Entry.Id) ?? "";
            RefreshSelection();
        }
    }
    public string GameDirectory
    {
        get => gameDirectory;
        set { if (IsBusy) { Changed(); return; } if (Set(ref gameDirectory, value)) RefreshSelection(); }
    }
    public bool AutoCheck
    {
        get => preferences.AutoCheck;
        set { preferences.AutoCheck = value; Changed(); SavePreferences(); }
    }
    private (string Path, PatchManifest Manifest)? LocalPackage => selectedGame is not null && localPackages.TryGetValue(selectedGame.Entry.Id, out var local) ? local : null;
    public string ReleaseNotes => LocalPackage is { } local ? $"已校验离线补丁 v{local.Manifest.Version}。点击下方按钮开始安装。\n兼容变体：{string.Join("、", local.Manifest.Variants.Select(v => v.Label))}" :
        SelectedGame?.Entry.Patch?.Notes ?? "此游戏已列入汉化目录。维护者正在准备补丁，发布后可在这里下载安装。你也可以导入已取得的标准离线补丁。";
    public string InstallLabel => LocalPackage is { } local ? "安装离线汉化 v" + local.Manifest.Version : SelectedGame?.Entry.Patch is { } patch ?
        (installed is null ? "下载安装汉化" : ReleaseVersion.IsNewer(patch.Version, installed.Version) ? "更新汉化至 v" + patch.Version : "重新安装 v" + patch.Version) : "等待维护者发布补丁";

    public AsyncCommand LibraryCommand { get; }
    public AsyncCommand TasksCommand { get; }
    public AsyncCommand HelpCommand { get; }
    public AsyncCommand SettingsCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand DetectCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand InstallCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand OpenRepoCommand { get; }
    public AsyncCommand OpenDocsCommand { get; }
    public AsyncCommand OpenDataCommand { get; }
    public AsyncCommand ClearCacheCommand { get; }
    public AsyncCommand CheckAppCommand { get; }
    public AsyncCommand UpdateAppCommand { get; }
    public AsyncCommand MaintainerCommand { get; }

    public MainViewModel()
    {
        Directory.CreateDirectory(DataDirectory);
        try { preferences = File.Exists(PreferencePath) ? JsonData.Read<Preferences>(PreferencePath) : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { preferences = new(); AddLog("偏好设置未能加载，已使用默认值：" + ex.Message); }
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = value => value is GameCard card && (card.Entry.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || card.Entry.Id.Contains(Search, StringComparison.OrdinalIgnoreCase) || (card.Entry.ChineseTitle?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        AsyncCommand Command(Func<Task> action, Func<bool>? allowed = null)
        { var command = new AsyncCommand(action, allowed); commands.Add(command); return command; }
        AsyncCommand Navigation(string target) => Command(() => { page = target; foreach (var property in new[] { nameof(PageTitle), nameof(IsLibrary), nameof(IsTasks), nameof(IsHelp), nameof(IsSettings) }) Changed(property); return Task.CompletedTask; });
        LibraryCommand = Navigation("library"); TasksCommand = Navigation("tasks"); HelpCommand = Navigation("help"); SettingsCommand = Navigation("settings");
        MaintainerCommand = Command(() =>
        {
            if (maintainerWindow is null)
            {
                maintainerWindow = new MaintainerWindow(SelectedGame?.Entry) { Owner = Application.Current.MainWindow };
                maintainerWindow.Closed += (_, _) => maintainerWindow = null;
                maintainerWindow.Show();
            }
            else { if (maintainerWindow.WindowState == WindowState.Minimized) maintainerWindow.WindowState = WindowState.Normal; maintainerWindow.Activate(); }
            return Task.CompletedTask;
        }, () => !IsBusy);
        RefreshCommand = Command(() => RunAsync(RefreshAsync), () => !IsBusy);
        DetectCommand = Command(() => RunAsync(DetectAsync), () => !IsBusy && SelectedGame?.Entry.SteamAppId is not null);
        BrowseCommand = Command(() => { Browse(); return Task.CompletedTask; }, () => !IsBusy && SelectedGame is not null);
        InstallCommand = Command(() => RunAsync(InstallAsync), () => !IsBusy && SelectedGame is not null && !string.IsNullOrWhiteSpace(GameDirectory) && (LocalPackage is not null || SelectedGame.Entry.Patch is not null));
        ImportCommand = Command(() => RunAsync(ImportAsync), () => !IsBusy);
        RestoreCommand = Command(() => RunAsync(RestoreAsync), () => !IsBusy && installed is not null);
        CancelCommand = Command(() => { cancellation?.Cancel(); StatusMessage = "正在取消准备工作；如已开始写入，将先完成安装或回滚。"; return Task.CompletedTask; }, () => cancellation is not null);
        OpenRepoCommand = Command(() => OpenAsync("https://github.com/tkxs/LocaleStack"));
        OpenDocsCommand = Command(() => OpenAsync("https://github.com/tkxs/LocaleStack/blob/main/docs/maintainers.md"));
        OpenDataCommand = Command(() => OpenAsync(DataDirectory));
        ClearCacheCommand = Command(() => RunAsync(_ => { if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, true); AddLog("已清理下载缓存。"); return Task.CompletedTask; }), () => !IsBusy);
        CheckAppCommand = Command(() => RunAsync(CheckAppAsync), () => !IsBusy);
        UpdateAppCommand = Command(() => RunAsync(UpdateAppAsync), () => !IsBusy && appRelease is not null);
        LoadCatalog();
    }
    private void LoadCatalog()
    {
        var cached = Path.Combine(DataDirectory, "catalog.json");
        try
        {
            if (File.Exists(cached)) { var catalog = JsonData.Read<Catalog>(cached); GitHubSource.ValidateCatalog(catalog); ApplyCatalog(catalog); SourceStatus = "使用上次同步的官方目录"; return; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { AddLog("目录缓存无效，将使用内置目录：" + ex.Message); }
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/catalog.json"))!;
        using var stream = resource.Stream;
        var bundled = JsonSerializer.Deserialize<Catalog>(stream, JsonData.Options)!;
        GitHubSource.ValidateCatalog(bundled); ApplyCatalog(bundled);
    }
    private void ApplyCatalog(Catalog catalog)
    {
        var id = SelectedGame?.Entry.Id;
        // Reset selection while rebuilding; catalog sync is already serialized by RunAsync.
        selectedGame = null;
        Games.Clear();
        foreach (var game in catalog.Games.Concat(preferences.LocalGames).DistinctBy(x => x.Id)) Games.Add(new(game));
        selectedGame = Games.FirstOrDefault(g => g.Entry.Id == id) ?? Games.FirstOrDefault();
        Changed(nameof(SelectedGame)); Changed(nameof(GameCountText));
        gameDirectory = SelectedGame is null ? "" : preferences.GameDirectories.GetValueOrDefault(SelectedGame.Entry.Id) ?? "";
        Changed(nameof(GameDirectory)); RefreshSelection();
    }
    public async Task StartAsync(bool offline)
    {
        AddLog("译栈已启动。汉化更新由你确认安装。");
        if (!offline && AutoCheck) await RunAsync(RefreshAsync);
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy) return;
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource; IsBusy = true; IsIndeterminate = true; ProgressFraction = 0;
        try { await operation(tokenSource.Token); }
        catch (OperationCanceledException) { AddLog("操作已取消。"); }
        catch (Exception ex)
        {
            var message = ex is UnauthorizedAccessException ? "没有写入权限。请将游戏放在可写目录，或在需要时用管理员身份运行译栈。" : ex.Message;
            AddLog("操作未完成：" + message);
            MessageBox.Show(message, "译栈 · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { cancellation = null; IsBusy = false; IsIndeterminate = false; RefreshSelection(); SavePreferences(); }
    }
    private IProgress<OperationProgress> Progress() => new Progress<OperationProgress>(p => { StatusMessage = p.Message; IsIndeterminate = p.Fraction is null; ProgressFraction = p.Fraction ?? 0; });
    private async Task RefreshAsync(CancellationToken token)
    {
        StatusMessage = "正在同步 GitHub 官方目录…";
        try
        {
            var catalog = await source.FetchCatalogAsync(token);
            JsonData.Write(Path.Combine(DataDirectory, "catalog.json"), catalog); ApplyCatalog(catalog);
            SourceStatus = "官方目录已同步 · " + DateTime.Now.ToString("HH:mm");
            var updates = 0;
            foreach (var game in Games)
                if (game.Entry.Patch is { } patch && preferences.GameDirectories.TryGetValue(game.Entry.Id, out var directory))
                {
                    try { if (PatchInstaller.ReadState(directory, game.Entry.Id) is { } state && ReleaseVersion.IsNewer(patch.Version, state.Version)) updates++; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { AddLog(game.Entry.Title + " 安装记录未能读取：" + ex.Message); }
                }
            AddLog(updates > 0 ? $"官方目录同步完成，发现 {updates} 款游戏有汉化更新。选择游戏后点击更新。" : "官方目录同步完成。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        { SourceStatus = "离线模式 · 已保留本地目录"; AddLog("在线目录暂不可用，保留本地目录。" + ex.Message); }
    }
    private async Task DetectAsync(CancellationToken token)
    {
        var game = SelectedGame!.Entry;
        var locations = await Task.Run(() => SteamLocator.Find(game.SteamAppId!), token);
        if (locations.Count == 0) { AddLog("未在 Steam 游戏库中找到此游戏。可手动选择其他来源的游戏目录。"); return; }
        gameDirectory = locations[0].Directory; Changed(nameof(GameDirectory)); RememberDirectory(); RefreshSelection();
        AddLog("已定位 Steam 游戏：" + gameDirectory + (locations.Count > 1 ? "。发现多个安装位置，也可手动选择其他目录。" : ""));
    }
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏根目录", Multiselect = false };
        if (Directory.Exists(GameDirectory)) dialog.InitialDirectory = GameDirectory;
        if (dialog.ShowDialog() != true) return;
        GameDirectory = dialog.FolderName; RememberDirectory(); SavePreferences(); AddLog("已选择游戏目录：" + GameDirectory);
    }
    private async Task ImportAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Title = "导入标准译栈补丁", Filter = "译栈补丁 (*.lspatch)|*.lspatch|标准 ZIP 补丁 (*.zip)|*.zip" };
        if (dialog.ShowDialog() != true) return;
        var progress = Progress();
        using var archive = await Task.Run(() => PatchArchive.Open(dialog.FileName, CacheDirectory, progress, token), token);
        var manifest = archive.Manifest;
        var card = Games.FirstOrDefault(g => g.Entry.Id == manifest.GameId);
        if (card is null)
        {
            var entry = new GameEntry { Id = manifest.GameId, Title = manifest.GameTitle, SteamAppId = manifest.SteamAppId, ExecutableNames = manifest.ExecutableNames, Summary = "从标准离线补丁导入的游戏。", Status = "planned" };
            card = new(entry); Games.Add(card); preferences.LocalGames.Add(entry); Changed(nameof(GameCountText));
        }
        localPackages[manifest.GameId] = (dialog.FileName, manifest);
        selectedGame = card; Changed(nameof(SelectedGame));
        gameDirectory = preferences.GameDirectories.GetValueOrDefault(manifest.GameId) ?? ""; Changed(nameof(GameDirectory));
        LibraryCommand.Execute(null); RefreshSelection();
        AddLog($"已校验离线补丁：{manifest.GameTitle} v{manifest.Version}。选择游戏目录后点击安装。");
        if (Directory.Exists(GameDirectory))
        {
            var location = SteamLocator.Describe(GameDirectory, manifest.SteamAppId);
            var result = await Task.Run(() => installer.Inspect(manifest, location), token); AddLog(result.Message);
        }
    }
    private async Task InstallAsync(CancellationToken token)
    {
        var game = SelectedGame!.Entry;
        RememberDirectory();
        var root = Path.GetFullPath(GameDirectory.Trim());
        var progress = Progress();
        var local = LocalPackage;
        var release = local is null ? game.Patch : null;
        var package = local?.Path ?? await source.DownloadAsync(release!, CacheDirectory, progress, token);
        using var archive = await Task.Run(() => PatchArchive.Open(package, CacheDirectory, progress, token), token);
        if (archive.Manifest.GameId != game.Id || (release is not null && archive.Manifest.Version != release.Version)) throw new InvalidDataException("下载补丁的游戏或版本与目录不一致。");
        var location = SteamLocator.Describe(root, archive.Manifest.SteamAppId);
        await Task.Run(() => installer.Install(archive, location, progress, token), token);
        AddLog($"汉化安装完成：{game.Title} v{archive.Manifest.Version}。原版备份已保留。");
    }
    private async Task RestoreAsync(CancellationToken token)
    {
        var game = SelectedGame!.Entry;
        var progress = Progress();
        await Task.Run(() => installer.Restore(GameDirectory.Trim(), game.Id, game.ExecutableNames, progress), token);
        AddLog("已恢复原版：" + game.Title);
    }
    private async Task CheckAppAsync(CancellationToken token)
    {
        appRelease = await source.CheckAppUpdateAsync(AppInfo.Version, token);
        AppUpdateLabel = appRelease is { } release ? "发现 v" + release.Version + "，可下载经 SHA-256 校验的官方安装程序。" : $"未发现更新的正式版安装程序。当前 v{AppInfo.Version}。";
        AddLog(AppUpdateLabel); RefreshCommands();
    }
    private async Task UpdateAppAsync(CancellationToken token)
    {
        var downloaded = await source.DownloadAsync(appRelease!.Installer, CacheDirectory, Progress(), token);
        var setup = Path.Combine(CacheDirectory, "LocaleStack-Setup-" + appRelease.Version + ".exe");
        SafePaths.CopyAtomic(downloaded, setup);
        AddLog("新版安装程序已下载并校验，将启动安装向导。");
        Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
        // Let the installer close the old process after its own user confirmation.
    }
    private void RefreshSelection()
    {
        installed = null; InstalledLabel = "未安装";
        if (SelectedGame is not null && Directory.Exists(GameDirectory.Trim()))
        {
            try
            {
                var location = SteamLocator.Describe(GameDirectory.Trim(), SelectedGame.Entry.SteamAppId);
                LocationLabel = location.Storefront == "steam" ? "Steam 安装 · Build " + (location.SteamBuildId ?? "未知") : "手动目录 · 安装前将通过文件指纹检查兼容性";
                installed = PatchInstaller.ReadState(GameDirectory.Trim(), SelectedGame.Entry.Id);
                if (installed is not null) InstalledLabel = "v" + installed.Version;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { LocationLabel = "目录或安装记录需要检查：" + ex.Message; }
        }
        else LocationLabel = "可自动查找 Steam，也可以手动选择其他来源的游戏目录。";
        Changed(nameof(InstallLabel)); Changed(nameof(ReleaseNotes)); RefreshCommands();
    }
    private void RememberDirectory()
    { if (SelectedGame is not null && !string.IsNullOrWhiteSpace(GameDirectory)) preferences.GameDirectories[SelectedGame.Entry.Id] = Path.GetFullPath(GameDirectory.Trim()); }
    private void SavePreferences()
    {
        try { RememberDirectory(); JsonData.Write(PreferencePath, preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { StatusMessage = "偏好设置未能保存：" + ex.Message; }
    }
    private void RefreshCommands() { foreach (var command in commands) command.Refresh(); }
    private async Task OpenAsync(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { AddLog("无法打开：" + ex.Message); }
        await Task.CompletedTask;
    }
    private void AddLog(string message)
    {
        StatusMessage = message;
        Logs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), message));
        if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
        try { File.AppendAllText(Path.Combine(DataDirectory, "activity-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusMessage = "日志写入失败：" + ex.Message; }
    }
    public void Dispose() { SavePreferences(); source.Dispose(); }
}
