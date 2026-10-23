using System.Collections.ObjectModel;
using LocaleStack.Core;

namespace LocaleStack.App;

public sealed record StorefrontChoice(string Id, string Label);
public sealed class FileSelection(ComparedFile file, Action selectionChanged) : Observable
{
    private bool selected = true;
    public ComparedFile File { get; } = file;
    public bool Selected { get => selected; set { if (Set(ref selected, value)) selectionChanged(); } }
    public string SizeLabel => File.Bytes >= 1048576 ? $"{File.Bytes / 1048576d:F1} MB" : $"{File.Bytes / 1024d:F1} KB";
}
public sealed class VariantEditor : Observable
{
    private string id = "default", label = "默认游戏版本", storefront = "any", buildIds = "", original = "", translated = "", fingerprintFiles = "";
    private readonly Action changed;
    public DirectoryComparison? Comparison { get; private set; }
    public ObservableCollection<FileSelection> Files { get; } = [];
    public string Id { get => id; set { if (Set(ref id, value)) changed(); } }
    public string Label { get => label; set { if (Set(ref label, value)) changed(); } }
    public string Storefront { get => storefront; set { if (Set(ref storefront, value)) { if (value != "steam") BuildIds = ""; Changed(nameof(IsSteam)); changed(); } } }
    public bool IsSteam => Storefront == "steam";
    public string BuildIds { get => buildIds; set { if (Set(ref buildIds, value)) changed(); } }
    public string OriginalDirectory { get => original; set { if (Set(ref original, value)) { Clear(); changed(); } } }
    public string TranslatedDirectory { get => translated; set { if (Set(ref translated, value)) { Clear(); changed(); } } }
    public string FingerprintFiles { get => fingerprintFiles; set { if (Set(ref fingerprintFiles, value)) changed(); } }
    public VariantEditor(Action changed) => this.changed = changed;
    public void Clear() { Comparison = null; Files.Clear(); }
    public void Apply(DirectoryComparison comparison)
    { Comparison = comparison; Files.Clear(); foreach (var file in comparison.Files) Files.Add(new(file, changed)); }
    public CaptureVariant ToConfig()
    {
        var fingerprints = Split(FingerprintFiles);
        if (fingerprints.Length == 0 && !Files.Any(f => f.Selected && f.File.OriginalSha256 is not null) && Comparison?.SuggestedExecutables.FirstOrDefault() is { } exe) fingerprints = [exe];
        return new() { Id = Id.Trim(), Label = Label.Trim(), Storefront = Storefront, SteamBuildIds = Split(BuildIds), OriginalDirectory = OriginalDirectory.Trim(), TranslatedDirectory = TranslatedDirectory.Trim(), Files = Files.Where(f => f.Selected).Select(f => f.File.Path).ToArray(), FingerprintFiles = fingerprints };
    }
    public static string[] Split(string value) => value.Split([';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
public sealed record MaintainerProfile
{
    public string GameId { get; init; } = "";
    public string GameTitle { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string SteamAppId { get; init; } = "";
    public string ExecutableNames { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Notes { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public List<CaptureVariant> Variants { get; init; } = [];
}
public sealed record PreparedPublication(string PackagePath, string Sha256, PatchManifest Manifest);
