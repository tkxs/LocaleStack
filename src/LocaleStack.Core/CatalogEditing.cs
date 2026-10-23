namespace LocaleStack.Core;

public sealed record PublicationMetadata(string GameId, string Title, string Summary, string Notes, string? SteamAppId, string[] ExecutableNames);

public static class CatalogEditing
{
    // Returns false when an identical package was already published (safe retry after an ambiguous API result).
    public static bool Apply(Catalog catalog, PatchManifest manifest, PublicationMetadata metadata, PatchRelease release)
    {
        GitHubSource.ValidateCatalog(catalog); PatchArchive.Validate(manifest);
        GitHubSource.ValidateAssetUrl(release.DownloadUrl); SafePaths.CheckHash(release.Sha256);
        if (manifest.GameId != metadata.GameId || manifest.Version != release.Version) throw new InvalidDataException("发布信息与补丁清单不一致。");
        var index = catalog.Games.FindIndex(g => g.Id == manifest.GameId);
        var existing = index >= 0 ? catalog.Games[index] : null;
        if (existing?.Patch is { } old)
        {
            var order = ReleaseVersion.Parse(release.Version).CompareTo(ReleaseVersion.Parse(old.Version));
            if (order < 0) throw new InvalidDataException("远程目录中已经有更新的汉化版本，已停止发布。");
            if (order == 0)
            {
                if (SafePaths.HashEquals(old.Sha256, release.Sha256) && old.Bytes == release.Bytes && old.DownloadUrl == release.DownloadUrl) return false;
                throw new InvalidDataException("相同版本号已经发布了不同内容。请递增汉化版本，不要覆盖已发布的包。");
            }
        }
        var entry = new GameEntry
        {
            Id = manifest.GameId, Title = metadata.Title, ChineseTitle = existing?.ChineseTitle,
            Summary = metadata.Summary.Length > 0 ? metadata.Summary : existing?.Summary ?? "", Engine = existing?.Engine ?? "", SteamAppId = manifest.SteamAppId,
            ExecutableNames = manifest.ExecutableNames, Status = "available", Patch = release
        };
        if (index >= 0) catalog.Games[index] = entry; else catalog.Games.Add(entry);
        GitHubSource.ValidateCatalog(catalog); return true;
    }
}
