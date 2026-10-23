using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using LocaleStack.Core;
using Microsoft.Win32;

namespace LocaleStack.App;

public sealed class MaintainerViewModel : Observable, INotifyDataErrorInfo, IDisposable
{
    private readonly List<AsyncCommand> commands = [];
    private readonly Dictionary<string, string> errors = [];
    private readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaleStack", "maintainer");
    private GitHubPublisher? publisher;
    private CancellationTokenSource? cancellation;
    private PreparedPublication? prepared;
    private VariantEditor? selectedVariant;
    private string gameId = "", gameTitle = "", version = "1.0.0", steamAppId = "", executableNames = "", summary = "", notes = "", outputDirectory = "";
    private string status = "选择原版与汉化目录，扫描后可勾选要打包的文件。", account = "尚未连接 GitHub", resultUrl = "", packageLabel = "尚未生成补丁";
    private bool busy, loading = true, indeterminate;
    private double fraction;
    public ObservableCollection<VariantEditor> Variants { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public StorefrontChoice[] Storefronts { get; } = [new("any", "所有来源 · 仍校验文件"), new("steam", "Steam"), new("standalone", "非 Steam / 独立安装")];
    public bool IsBusy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); RefreshCommands(); } }
    public bool CanEdit => !IsBusy;
    public bool IsConnected => publisher is not null;
    public string AccountLabel { get => account; private set => Set(ref account, value); }
    public string StatusMessage { get => status; private set => Set(ref status, value); }
    public double ProgressFraction { get => fraction; private set => Set(ref fraction, value); }
    public bool IsIndeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string PackageLabel { get => packageLabel; private set => Set(ref packageLabel, value); }
    public string VersionLabel => "维护者工作台 · v" + AppInfo.Version;
    public string PublishLabel => prepared is null ? "一键打包并发布" : "上传补丁并更新目录";
    public string SelectionSummary => $"{Variants.Count} 个兼容版本 · 已选 {Variants.Sum(v => v.Files.Count(f => f.Selected))} 个文件 · {Variants.Sum(v => v.Files.Where(f => f.Selected).Sum(f => f.File.Bytes)) / 1048576d:F1} MB";
    public string TagPreview => string.IsNullOrWhiteSpace(GameId) ? "发布到 tkxs/LocaleStack，自动生成独立的补丁 Release" : $"patch-{GameId}-v{Version} · 自动更新 catalog/catalog.json";
    public VariantEditor? SelectedVariant { get => selectedVariant; set { if (Set(ref selectedVariant, value)) { Changed(nameof(SelectedFiles)); RefreshCommands(); } } }
    public ObservableCollection<FileSelection>? SelectedFiles => SelectedVariant?.Files;
    public string GameId { get => gameId; set { if (Set(ref gameId, value)) { Validate(nameof(GameId), () => SafePaths.CheckId(value.Trim())); Invalidate(); } } }
    public string GameTitle { get => gameTitle; set { if (Set(ref gameTitle, value)) { Validate(nameof(GameTitle), () => { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请填写游戏名称。"); }); Invalidate(); } } }
    public string Version { get => version; set { if (Set(ref version, value)) { Validate(nameof(Version), () => ReleaseVersion.Parse(value.Trim())); Invalidate(); } } }
    public string SteamAppId { get => steamAppId; set { if (Set(ref steamAppId, value)) { Validate(nameof(SteamAppId), () => { if (value.Trim().Length > 0 && !value.Trim().All(char.IsAsciiDigit)) throw new ArgumentException("App ID 只能包含数字；非 Steam 游戏可留空。"); }); Invalidate(); } } }
    public string ExecutableNames { get => executableNames; set { if (Set(ref executableNames, value)) Invalidate(); } }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string Notes { get => notes; set => Set(ref notes, value); }
    public string OutputDirectory { get => outputDirectory; set { if (Set(ref outputDirectory, value)) RefreshCommands(); } }
    public bool HasErrors => errors.Count != 0;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) => propertyName is not null && errors.TryGetValue(propertyName, out var error) ? new[] { error } : Array.Empty<string>();

    public AsyncCommand ScanCommand { get; }
    public AsyncCommand BuildCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand LoadPackageCommand { get; }
    public AsyncCommand AddVariantCommand { get; }
    public AsyncCommand RemoveVariantCommand { get; }
    public AsyncCommand BrowseOriginalCommand { get; }
    public AsyncCommand BrowseTranslatedCommand { get; }
    public AsyncCommand BrowseOutputCommand { get; }
    public AsyncCommand BrowseFingerprintCommand { get; }
    public AsyncCommand UseSavedCredentialCommand { get; }
    public AsyncCommand ForgetCredentialCommand { get; }
    public AsyncCommand CreateTokenCommand { get; }
    public AsyncCommand OpenResultCommand { get; }
    public AsyncCommand OpenOutputCommand { get; }
    public AsyncCommand CancelCommand { get; }

    public MaintainerViewModel(GameEntry? game = null, string? profileDirectory = null)
    {
        if (profileDirectory is not null) dataRoot = Path.GetFullPath(profileDirectory);
        AsyncCommand Command(Func<Task> action, Func<bool>? allowed = null) { var c = new AsyncCommand(action, allowed); commands.Add(c); return c; }
        ScanCommand = Command(() => RunAsync(ScanAsync), () => !IsBusy);
        BuildCommand = Command(() => RunAsync(async token => { await BuildAsync(token); }), () => !IsBusy);
        PublishCommand = Command(() => RunAsync(PublishAsync), () => !IsBusy && IsConnected);
        LoadPackageCommand = Command(() => RunAsync(LoadPackageAsync), () => !IsBusy);
        AddVariantCommand = Command(() =>
        {
            var index = Variants.Count + 1;
            while (Variants.Any(v => v.Id.Equals("version-" + index, StringComparison.OrdinalIgnoreCase))) index++;
            var variant = NewVariant(); variant.Id = "version-" + index; variant.Label = "兼容版本 " + index;
            Variants.Add(variant); SelectedVariant = variant; Invalidate(); return Task.CompletedTask;
        }, () => !IsBusy && Variants.Count < 32);
        RemoveVariantCommand = Command(() => { if (SelectedVariant is { } v) Variants.Remove(v); SelectedVariant = Variants.FirstOrDefault(); Invalidate(); return Task.CompletedTask; }, () => !IsBusy && Variants.Count > 1);
        BrowseOriginalCommand = Command(() => BrowseFolder(path => SelectedVariant!.OriginalDirectory = path, "选择干净原版或对应构建的英文备份"), () => !IsBusy && SelectedVariant is not null);
        BrowseTranslatedCommand = Command(() => BrowseFolder(path => SelectedVariant!.TranslatedDirectory = path, "选择汉化目录或 Chinese_Patch 输出目录"), () => !IsBusy && SelectedVariant is not null);
        BrowseOutputCommand = Command(() => BrowseFolder(path => OutputDirectory = path, "选择补丁输出目录"), () => !IsBusy);
        BrowseFingerprintCommand = Command(() => RunAsync(_ =>
        {
            var variant = SelectedVariant!;
            if (!Directory.Exists(variant.OriginalDirectory)) throw new DirectoryNotFoundException("请先选择原版目录。");
            var dialog = new OpenFileDialog { Title = "选择额外的原版版本识别文件", InitialDirectory = variant.OriginalDirectory };
            if (dialog.ShowDialog() == true)
            {
                var relative = Path.GetRelativePath(variant.OriginalDirectory, dialog.FileName).Replace('\\', '/');
                SafePaths.Resolve(variant.OriginalDirectory, relative);
                variant.FingerprintFiles = string.Join(";", VariantEditor.Split(variant.FingerprintFiles).Append(relative).Distinct());
            }
            return Task.CompletedTask;
        }), () => !IsBusy && SelectedVariant is not null);
        UseSavedCredentialCommand = Command(() => RunAsync(async token => { var saved = WindowsCredentialStore.Read() ?? throw new IOException("此电脑尚未保存 GitHub 连接。请先粘贴令牌并连接。"); await ConnectCoreAsync(saved, token); }), () => !IsBusy);
        ForgetCredentialCommand = Command(() => RunAsync(_ => { publisher?.Dispose(); publisher = null; WindowsCredentialStore.Delete(); AccountLabel = "已断开，并移除此电脑保存的令牌"; Changed(nameof(IsConnected)); RefreshCommands(); return Task.CompletedTask; }), () => !IsBusy);
        CreateTokenCommand = Command(() => Open("https://github.com/settings/personal-access-tokens/new"));
        OpenResultCommand = Command(() => Open(resultUrl), () => resultUrl.Length > 0);
        OpenOutputCommand = Command(() => RunAsync(_ => { var path = prepared is null ? OutputDirectory : Path.GetDirectoryName(prepared.PackagePath)!; if (!Directory.Exists(path)) throw new DirectoryNotFoundException("输出目录尚不存在，请先生成补丁。"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return Task.CompletedTask; }), () => !IsBusy);
        CancelCommand = Command(() => { cancellation?.Cancel(); StatusMessage = "正在取消；已上传的草稿或附件会保留，可用相同本地包重试。"; return Task.CompletedTask; }, () => IsBusy);
        LoadProfile(game); loading = false; RefreshCommands();
    }
    private VariantEditor NewVariant() => new(() => { Invalidate(); Changed(nameof(SelectionSummary)); });
    private void Invalidate()
    {
        if (loading) return;
        prepared = null; PackageLabel = "内容已更改，需重新生成补丁";
        Changed(nameof(TagPreview)); Changed(nameof(PublishLabel)); Changed(nameof(SelectionSummary)); RefreshCommands();
    }
    private void Validate(string property, Action check)
    {
        if (loading) return;
        try { check(); errors.Remove(property); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { errors[property] = ex.Message; }
        ErrorsChanged?.Invoke(this, new(property)); Changed(nameof(HasErrors));
    }
    private void ValidateForm()
    {
        SafePaths.CheckId(GameId.Trim()); ReleaseVersion.Parse(Version.Trim());
        if (string.IsNullOrWhiteSpace(GameTitle)) throw new ArgumentException("请填写游戏名称。");
        if (SteamAppId.Trim().Length > 0 && !SteamAppId.Trim().All(char.IsAsciiDigit)) throw new ArgumentException("Steam App ID 必须为数字。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in Variants)
        {
            SafePaths.CheckId(variant.Id.Trim());
            if (!ids.Add(variant.Id.Trim())) throw new ArgumentException("兼容版本 ID 不能重复。");
            if (string.IsNullOrWhiteSpace(variant.Label)) throw new ArgumentException("请填写每个兼容版本的名称。");
        }
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var source = new CancellationTokenSource(); cancellation = source; IsBusy = true; IsIndeterminate = true;
        try { await action(source.Token); }
        catch (OperationCanceledException) { Log("操作已取消。本地已生成的补丁会保留；若上传已开始，可载入相同包后重试发布。"); }
        catch (Exception ex) { Log("未完成：" + ex.Message); MessageBox.Show(ex.Message, "译栈维护者工作台", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { cancellation = null; IsBusy = false; IsIndeterminate = false; SaveProfile(); }
    }
    private IProgress<OperationProgress> Progress() => new Progress<OperationProgress>(p => { StatusMessage = p.Message; IsIndeterminate = p.Fraction is null; ProgressFraction = p.Fraction ?? 0; });
    private async Task ScanAsync(CancellationToken token)
    {
        ValidateForm(); prepared = null; PackageLabel = "正在对比目录";
        foreach (var variant in Variants)
        {
            var progress = Progress();
            var comparison = await Task.Run(() => DirectoryComparer.Compare(variant.OriginalDirectory, variant.TranslatedDirectory, progress, token), token);
            variant.Apply(comparison);
            if (comparison.Files.Count == 0) throw new IOException(variant.Label + "：没有发现修改或新增文件。");
        }
        if (string.IsNullOrWhiteSpace(ExecutableNames)) executableNames = string.Join(";", Variants.SelectMany(v => v.Comparison!.SuggestedExecutables).Distinct(StringComparer.OrdinalIgnoreCase));
        Changed(nameof(ExecutableNames)); Changed(nameof(SelectionSummary)); Changed(nameof(SelectedFiles)); Changed(nameof(PublishLabel));
        Log("目录对比完成：" + SelectionSummary + "。可取消勾选不需要的文件。");
    }
    private async Task<PreparedPublication> BuildAsync(CancellationToken token)
    {
        if (prepared is { } existing)
        {
            if (!File.Exists(existing.PackagePath) || !SafePaths.HashEquals(await Task.Run(() => SafePaths.Hash(existing.PackagePath), token), existing.Sha256)) throw new IOException("本地已打包文件发生变化，请重新载入或生成。");
            return existing;
        }
        ValidateForm();
        if (Variants.Any(v => v.Comparison is null)) await ScanAsync(token);
        if (Variants.Any(v => v.Files.All(f => !f.Selected))) throw new ArgumentException("每个兼容版本都至少需要勾选一个汉化文件。");
        var output = Path.GetFullPath(OutputDirectory.Trim());
        if (Variants.Any(v => DirectoryComparer.IsWithin(output, v.OriginalDirectory) || DirectoryComparer.IsWithin(output, v.TranslatedDirectory))) throw new IOException("输出目录不能位于原版或汉化目录内，请另选一个目录。");
        var path = Path.Combine(output, $"{GameId.Trim()}-{Version.Trim()}.lspatch");
        if (File.Exists(path)) throw new IOException("同名补丁已存在。可点击“载入已有补丁”继续发布，或换一个版本号/输出目录。");
        var config = new CaptureConfig { GameId = GameId.Trim(), GameTitle = GameTitle.Trim(), Version = Version.Trim(), SteamAppId = SteamAppId.Trim().Length == 0 ? null : SteamAppId.Trim(), ExecutableNames = VariantEditor.Split(ExecutableNames), Variants = Variants.Select(v => v.ToConfig()).ToList() };
        var progress = Progress();
        var work = SafePaths.Resolve(dataRoot, "work/" + Guid.NewGuid().ToString("N"));
        var comparisons = Variants.Select(v => (Comparison: v.Comparison!, Selected: v.Files.Where(f => f.Selected).Select(f => f.File).ToArray())).ToArray();
        try
        {
            prepared = await Task.Run(() =>
            {
                foreach (var (comparison, files) in comparisons) DirectoryComparer.VerifySelection(comparison, files, token);
                PatchAuthoring.Capture(config, work, progress, token);
                var manifest = JsonData.Read<PatchManifest>(Path.Combine(work, "manifest.json"));
                for (var i = 0; i < comparisons.Length; i++)
                {
                    var (comparison, files) = comparisons[i]; DirectoryComparer.VerifySelection(comparison, files, token);
                    foreach (var file in manifest.Variants[i].Files)
                    {
                        var scanned = files.Single(f => f.Path == file.Path);
                        if (!SafePaths.HashEquals(scanned.OriginalSha256, file.OriginalSha256) || !SafePaths.HashEquals(scanned.TranslatedSha256, file.Sha256)) throw new IOException("打包期间文件发生了变化，请重新扫描：" + file.Path);
                    }
                }
                PatchAuthoring.Pack(work, path, progress, token);
                return new PreparedPublication(path, SafePaths.Hash(path), manifest);
            }, token);
        }
        finally { if (Directory.Exists(work)) { SafePaths.RejectLinks(work); Directory.Delete(work, true); } }
        SaveReceipt(); PackageLabel = Path.GetFileName(path) + " · SHA-256 已校验"; Changed(nameof(PublishLabel));
        Log("补丁已生成：" + path); return prepared;
    }
    private PublicationMetadata Metadata(PatchManifest manifest) => new(manifest.GameId, manifest.GameTitle, Summary.Trim(), Notes.Trim(), manifest.SteamAppId, manifest.ExecutableNames);
    private async Task PublishAsync(CancellationToken token)
    {
        var package = await BuildAsync(token);
        resultUrl = GitHubSource.ReleasesUrl;
        RefreshCommands();
        var result = await publisher!.PublishAsync(package.PackagePath, Metadata(package.Manifest), Progress(), token);
        resultUrl = result.ReleaseUrl; SaveReceipt(result); RefreshCommands();
        Log("发布成功：补丁附件已校验，游戏目录已更新。玩家同步目录后即可安装。\n" + result.ReleaseUrl);
    }
    public Task ConnectAsync(string credential, bool remember) => RunAsync(async token =>
    {
        await ConnectCoreAsync(credential, token);
        try { if (remember) WindowsCredentialStore.Save(credential.Trim(), AccountLabel); else WindowsCredentialStore.Delete(); }
        catch (Win32Exception) { Log("连接成功，但 Windows 凭据未能保存。本次会话仍可发布。"); }
    });
    private async Task ConnectCoreAsync(string credential, CancellationToken token)
    {
        var connection = new GitHubPublisher(credential);
        try
        {
            StatusMessage = "正在验证 GitHub 账号与仓库访问权限";
            var login = await connection.ConnectAsync(token);
            publisher?.Dispose(); publisher = connection; AccountLabel = login + " · tkxs/LocaleStack";
            Changed(nameof(IsConnected)); RefreshCommands(); Log("已连接 GitHub：" + AccountLabel);
        }
        catch { connection.Dispose(); throw; }
    }
    private async Task LoadPackageAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Title = "载入现有补丁以继续发布", Filter = "译栈补丁 (*.lspatch)|*.lspatch" };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        if (dialog.ShowDialog() != true) return;
        using var archive = await Task.Run(() => PatchArchive.Open(dialog.FileName, dataRoot, Progress(), token), token);
        var manifest = archive.Manifest;
        if (GameId != manifest.GameId) { Summary = ""; Notes = ""; }
        GameId = manifest.GameId; GameTitle = manifest.GameTitle; Version = manifest.Version; SteamAppId = manifest.SteamAppId ?? ""; ExecutableNames = string.Join(";", manifest.ExecutableNames);
        Variants.Clear();
        foreach (var v in manifest.Variants)
        {
            var editor = NewVariant(); editor.Id = v.Id; editor.Label = v.Label; editor.Storefront = v.Storefront; editor.BuildIds = string.Join(";", v.SteamBuildIds);
            editor.Apply(new("", "", v.Files.Select(f => new ComparedFile(f.Path, f.OriginalSha256, f.Sha256, new FileInfo(SafePaths.Resolve(archive.DirectoryPath, f.Payload)).Length)).ToList(), manifest.ExecutableNames));
            Variants.Add(editor);
        }
        SelectedVariant = Variants[0];
        prepared = new(dialog.FileName, SafePaths.Hash(dialog.FileName), manifest);
        PackageLabel = Path.GetFileName(dialog.FileName) + " · 已校验，可直接发布"; Changed(nameof(PublishLabel)); Changed(nameof(SelectionSummary));
        Log("已载入补丁，不必重新扫描目录。连接 GitHub 后可继续上传或补写目录。");
    }
    private Task BrowseFolder(Action<string> selected, string title)
    { var dialog = new OpenFolderDialog { Title = title }; if (dialog.ShowDialog() == true) selected(dialog.FolderName); return Task.CompletedTask; }
    private Task Open(string url)
    {
        try { if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com") Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception ex) { Log("无法打开浏览器：" + ex.Message); }
        return Task.CompletedTask;
    }
    private void LoadProfile(GameEntry? game)
    {
        MaintainerProfile profile = new();
        var path = Path.Combine(dataRoot, "profile.json");
        try { if (File.Exists(path)) profile = JsonData.Read<MaintainerProfile>(path); }
        catch (Exception ex) when (ex is IOException or JsonException) { Log("旧工作台配置未能加载，已使用默认值。"); }
        if (game is not null && profile.GameId != game.Id) profile = new() { GameId = game.Id, GameTitle = game.Title, Summary = game.Summary, SteamAppId = game.SteamAppId ?? "", ExecutableNames = string.Join(";", game.ExecutableNames), Version = game.Patch is { } p ? NextVersion(p.Version) : "1.0.0" };
        GameId = profile.GameId; GameTitle = profile.GameTitle; Version = profile.Version; SteamAppId = profile.SteamAppId; ExecutableNames = profile.ExecutableNames; Summary = profile.Summary; Notes = profile.Notes;
        OutputDirectory = profile.OutputDirectory.Length > 0 ? profile.OutputDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LocaleStackPackages");
        foreach (var v in profile.Variants)
        { var editor = NewVariant(); editor.Id = v.Id; editor.Label = v.Label; editor.OriginalDirectory = v.OriginalDirectory; editor.TranslatedDirectory = v.TranslatedDirectory; editor.Storefront = v.Storefront; editor.BuildIds = string.Join(";", v.SteamBuildIds); editor.FingerprintFiles = string.Join(";", v.FingerprintFiles); Variants.Add(editor); }
        if (Variants.Count == 0) { var v = NewVariant(); if (SteamAppId.Length > 0) { v.Id = "steam"; v.Label = "Steam 版本"; v.Storefront = "steam"; } Variants.Add(v); }
        SelectedVariant = Variants[0];
    }
    private static string NextVersion(string current) { var v = ReleaseVersion.Parse(current); return $"{v.Major}.{v.Minor}.{v.Build + 1}"; }
    private void SaveProfile()
    {
        try { JsonData.Write(Path.Combine(dataRoot, "profile.json"), new MaintainerProfile { GameId = GameId, GameTitle = GameTitle, Version = Version, SteamAppId = SteamAppId, ExecutableNames = ExecutableNames, Summary = Summary, Notes = Notes, OutputDirectory = OutputDirectory, Variants = Variants.Select(v => v.ToConfig()).ToList() }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusMessage = "工作配置未能保存：" + ex.Message; }
    }
    private void SaveReceipt(PublishResult? result = null)
    { if (prepared is not null) JsonData.Write(Path.Combine(dataRoot, "last-publication.json"), new { packagePath = prepared.PackagePath, sha256 = prepared.Sha256, metadata = Metadata(prepared.Manifest), result }); }
    private void RefreshCommands() { foreach (var command in commands) command.Refresh(); }
    private void Log(string message)
    {
        StatusMessage = message; Logs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), message));
        if (Logs.Count > 100) Logs.RemoveAt(Logs.Count - 1);
        try { Directory.CreateDirectory(dataRoot); File.AppendAllText(Path.Combine(dataRoot, "activity.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); }
        catch (IOException) { }
    }
    internal async Task ExerciseLocalWorkflowAsync()
    {
        if (!Environment.GetCommandLineArgs().Contains("--smoke-maintainer") || !dataRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("桌面测试必须使用隔离目录。");
        AddVariantCommand.Execute(null); AddVariantCommand.Execute(null);
        SelectedVariant = Variants[1]; RemoveVariantCommand.Execute(null); AddVariantCommand.Execute(null);
        if (Variants.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Variants.Count) throw new IOException("移除再新增兼容版本产生了重复 ID。");
        IsBusy = true;
        try
        {
            var original = Path.Combine(dataRoot, "原版 with spaces"); var translated = Path.Combine(dataRoot, "汉化 with spaces");
            Directory.CreateDirectory(original); Directory.CreateDirectory(translated);
            File.WriteAllText(Path.Combine(original, "game.dat"), "English"); File.WriteAllText(Path.Combine(original, "LocaleStack.TestGame.exe"), "version-marker");
            File.WriteAllText(Path.Combine(translated, "game.dat"), "中文内容"); File.WriteAllText(Path.Combine(translated, "font.txt"), "font-data"); File.WriteAllText(Path.Combine(translated, "debug.txt"), "do-not-package");
            GameId = "ui-workflow-example"; GameTitle = "桌面流程隔离测试"; Version = "1.0.0"; SteamAppId = ""; Notes = "仅用于本地验证，没有上传。";
            OutputDirectory = Path.Combine(dataRoot, "output"); Variants.Clear(); var variant = NewVariant(); variant.Storefront = "standalone"; variant.OriginalDirectory = original; variant.TranslatedDirectory = translated; Variants.Add(variant); SelectedVariant = variant;
            await ScanAsync(CancellationToken.None);
            variant.Files.Single(f => f.File.Path == "debug.txt").Selected = false;
            var package = await BuildAsync(CancellationToken.None);
            if (package.Manifest.Variants[0].Files.Count != 2) throw new IOException("GUI 文件勾选没有传入打包器。");
            using (var archive = PatchArchive.Open(package.PackagePath, dataRoot))
            {
                var installer = new PatchInstaller(); installer.Install(archive, new(original, "standalone"));
                if (File.ReadAllText(Path.Combine(original, "game.dat")) != "中文内容" || File.Exists(Path.Combine(original, "debug.txt"))) throw new IOException("GUI 生成补丁的安装结果不正确。");
                installer.Restore(original, package.Manifest.GameId, []);
                if (File.ReadAllText(Path.Combine(original, "game.dat")) != "English" || File.Exists(Path.Combine(original, "font.txt"))) throw new IOException("GUI 生成补丁的恢复结果不正确。");
            }
            var target = "LocaleStack:Test:" + Guid.NewGuid().ToString("N");
            try { WindowsCredentialStore.Save("synthetic-test-credential", "test-account", target); if (WindowsCredentialStore.Read(target) != "synthetic-test-credential") throw new IOException("Windows 凭据往返不一致。"); }
            finally { WindowsCredentialStore.Delete(target); }
            if (WindowsCredentialStore.Read(target) is not null) throw new IOException("测试凭据未移除。");
            Log("桌面扫描、勾选、打包、安装、恢复及隔离凭据往返验证通过。未访问 GitHub 写入接口。");
        }
        finally { IsBusy = false; }
    }
    public void Dispose() { SaveProfile(); publisher?.Dispose(); }
}
