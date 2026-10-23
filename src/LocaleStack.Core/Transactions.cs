namespace LocaleStack.Core;

internal sealed record Mutation(string Path, string? Source, string? ExpectedBeforeHash);
internal sealed record JournalEntry(string Path, bool BeforeExists, string? BeforeHash, string? AfterHash, string Snapshot);
internal sealed record Journal(List<JournalEntry> Entries);

internal static class Transactions
{
    public static FileStream Lock(string root)
    {
        SafePaths.RejectLinks(root);
        var path = SafePaths.Resolve(root, ".localestack/operation.lock", true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("此游戏正在进行另一项译栈操作，请稍后重试。", ex); }
    }
    public static void Recover(string root, IProgress<OperationProgress>? progress = null)
    {
        var parent = SafePaths.Resolve(root, ".localestack/transactions", true);
        if (!Directory.Exists(parent)) return;
        foreach (var directory in Directory.GetDirectories(parent))
        {
            SafePaths.RejectLinks(directory);
            if (File.Exists(Path.Combine(directory, "committed"))) { Directory.Delete(directory, true); continue; }
            var journalPath = Path.Combine(directory, "journal.json");
            if (!File.Exists(journalPath)) { Directory.Delete(directory, true); continue; } // No writes start before the journal exists.
            var journal = JsonData.Read<Journal>(journalPath);
            // Validate everything BEFORE restoring any file. Never overwrite an unrelated change after a crash.
            foreach (var entry in journal.Entries)
            {
                var target = SafePaths.Resolve(root, entry.Path, true);
                var hash = File.Exists(target) ? SafePaths.Hash(target) : null;
                if (!SafePaths.HashEquals(hash, entry.BeforeHash) && !SafePaths.HashEquals(hash, entry.AfterHash))
                    throw new IOException($"上次操作中断后文件又发生了变化，已保留恢复现场：{entry.Path}\n{directory}");
                if (entry.BeforeExists)
                {
                    var snapshot = SafePaths.Resolve(directory, entry.Snapshot, true);
                    if (!SafePaths.HashEquals(SafePaths.Hash(snapshot), entry.BeforeHash)) throw new IOException("恢复快照校验失败，已停止操作。");
                }
            }
            progress?.Report(new("正在恢复上次中断的操作"));
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                var target = SafePaths.Resolve(root, entry.Path, true);
                if (entry.BeforeExists) SafePaths.CopyAtomic(SafePaths.Resolve(directory, entry.Snapshot, true), target);
                else if (File.Exists(target)) File.Delete(target);
            }
            Directory.Delete(directory, true);
        }
    }
    public static void Apply(string root, List<Mutation> mutations, IProgress<OperationProgress>? progress, Action<int>? afterWrite = null)
    {
        var directory = SafePaths.Resolve(root, ".localestack/transactions/" + Guid.NewGuid().ToString("N"), true);
        Directory.CreateDirectory(directory);
        var entries = new List<JournalEntry>();
        try
        {
            foreach (var mutation in mutations)
            {
                var target = SafePaths.Resolve(root, mutation.Path, true);
                var exists = File.Exists(target);
                var beforeHash = exists ? SafePaths.Hash(target) : null;
                if (!SafePaths.HashEquals(beforeHash, mutation.ExpectedBeforeHash)) throw new IOException($"预检后文件发生变化：{mutation.Path}");
                var snapshot = entries.Count + ".bak";
                if (exists) SafePaths.CopyAtomic(target, Path.Combine(directory, snapshot));
                if (exists && !SafePaths.HashEquals(SafePaths.Hash(Path.Combine(directory, snapshot)), beforeHash)) throw new IOException("事务快照与预检内容不一致。");
                entries.Add(new(mutation.Path, exists, exists ? SafePaths.Hash(Path.Combine(directory, snapshot)) : null,
                    mutation.Source is null ? null : SafePaths.Hash(mutation.Source), snapshot));
            }
            JsonData.Write(Path.Combine(directory, "journal.json"), new Journal(entries));
            var count = 0;
            foreach (var mutation in mutations)
            {
                var target = SafePaths.Resolve(root, mutation.Path, true);
                // Catch changes between the preflight and the commit, including a game restarted by the user.
                var before = File.Exists(target) ? SafePaths.Hash(target) : null;
                if (!SafePaths.HashEquals(before, entries[count].BeforeHash)) throw new IOException($"操作期间文件发生了变化：{mutation.Path}");
                if (mutation.Source is null) { if (File.Exists(target)) File.Delete(target); }
                else SafePaths.CopyAtomic(mutation.Source, target);
                var actual = File.Exists(target) ? SafePaths.Hash(target) : null;
                if (!SafePaths.HashEquals(actual, entries[count].AfterHash)) throw new IOException($"写入校验失败：{mutation.Path}");
                count++;
                afterWrite?.Invoke(count);
                progress?.Report(new("写入并校验 · " + mutation.Path, count / (double)mutations.Count));
            }
            using (var marker = new FileStream(Path.Combine(directory, "committed"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) marker.Flush(true);
        }
        catch (Exception failure)
        {
            try { Recover(root, progress); }
            catch (Exception recovery) { throw new AggregateException("操作失败，自动恢复也未完成。已保留事务快照，请勿删除 .localestack 目录。", failure, recovery); }
            throw;
        }
        // A cleanup failure does not turn a successfully committed installation into a reported failure.
        try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
