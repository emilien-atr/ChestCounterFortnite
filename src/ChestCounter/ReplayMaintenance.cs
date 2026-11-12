using System.IO.Compression;

namespace ChestCounter;

/// <summary>Disk usage of the Fortnite replays folder and of the archive made by the counter.</summary>
public sealed record ReplayStorageStats(int FolderCount, long FolderBytes, int ArchiveCount, long ArchiveBytes)
{
    public static ReplayStorageStats Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>What an archive action would move. The file list is captured on the UI thread before any background work.</summary>
public sealed record ReplayArchivePlan(int Count, long Bytes, IReadOnlyList<FileInfo> Files);

/// <summary>Outcome of an archive, migration or cleanup pass.</summary>
public sealed record ReplayMaintenanceResult(
    int Archived, long ArchivedBytes,
    int Deleted, long DeletedBytes,
    int Failed,
    int Compressed = 0, long CompressedBytes = 0)
{
    public bool Changed => Archived > 0 || Deleted > 0 || Compressed > 0;
}

/// <summary>
/// Keeps already analyzed replays in one local replays.zip archive. Every change is written to a temporary ZIP and
/// swapped in only after all entries have been copied, so a failed operation never removes a source replay. Only
/// matches present in the history are touched, never a replay still being recorded, and deletion only happens inside
/// the archive. Older per-replay ZIPs and raw archived replays are merged automatically.
/// </summary>
public static class ReplayMaintenance
{
    /// <summary>The single ZIP file stored in the archive folder.</summary>
    public const string ArchiveFileName = "replays.zip";

    /// <summary>Retention choices offered by the interface; 0 means "never delete".</summary>
    public static readonly int[] RetentionDays = { 0, 7, 30, 90 };

    private static readonly object ArchiveGate = new();

    public static string ArchiveFilePath(Store store) => Path.Combine(store.ReplayArchivePath, ArchiveFileName);

    public static int NormalizeRetention(int days) => RetentionDays.Contains(days) ? days : 0;

    /// <summary>Counts and sizes of replays in the Fortnite folder and of the entries in the single archive.</summary>
    public static ReplayStorageStats Inspect(Store store, Config config)
    {
        var folder = Measure(config.DemosFolder, "*.replay");
        var archive = MeasureArchive(store);
        return new ReplayStorageStats(folder.Count, folder.Bytes, archive.Count, archive.Bytes);
    }

    /// <summary>True when an older version left raw replays or per-replay ZIPs in the archive folder.</summary>
    public static bool HasLegacyArchive(Store store) => EnumerateLegacyFiles(store).Count > 0;

    /// <summary>Files that can be archived now: analyzed, not live, and still in the Fortnite folder.</summary>
    public static ReplayArchivePlan PlanArchive(Config config, TrackerData data)
    {
        var analyzed = AnalyzedNames(data);
        var files = Enumerate(config.DemosFolder, "*.replay")
            .Where(f => analyzed.Contains(f.Name))
            .Where(f => ReplayHeaderProbe.IsLive(f.FullName) != true)
            .ToList();
        return new ReplayArchivePlan(files.Count, files.Sum(SafeLength), files);
    }

    /// <summary>Entries in the single archive older than the retention period. 0 keeps everything.</summary>
    public static ReplayArchivePlan PlanDelete(Store store, int days)
    {
        days = NormalizeRetention(days);
        if (days <= 0)
        {
            return new ReplayArchivePlan(0, 0, Array.Empty<FileInfo>());
        }

        var archivePath = ArchiveFilePath(store);
        if (!File.Exists(archivePath))
        {
            return new ReplayArchivePlan(0, 0, Array.Empty<FileInfo>());
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var expired = archive.Entries
                .Where(e => !IsDirectory(e))
                .Where(e => ArchivedAt(e).UtcDateTime <= cutoff)
                .ToList();
            return new ReplayArchivePlan(expired.Count, expired.Sum(SafeCompressedLength), Array.Empty<FileInfo>());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay archive: cannot inspect the archive", ex);
            return new ReplayArchivePlan(0, 0, Array.Empty<FileInfo>());
        }
    }

    public static ReplayMaintenanceResult ArchiveAnalyzed(Store store, Config config, TrackerData data) =>
        ArchiveFiles(store, PlanArchive(config, data).Files);

    /// <summary>Adds the files captured by PlanArchive and merges legacy archives in the same pass.</summary>
    public static ReplayMaintenanceResult ArchiveFiles(Store store, IReadOnlyList<FileInfo> files) =>
        Rewrite(store, files, 0, cleanExpired: false, migrateLegacy: true);

    /// <summary>Merges legacy raw replays and per-replay ZIPs into the single archive.</summary>
    public static ReplayMaintenanceResult CompressArchive(Store store) =>
        Rewrite(store, Array.Empty<FileInfo>(), 0, cleanExpired: false, migrateLegacy: true);

    public static ReplayMaintenanceResult DeleteExpired(Store store, int days) =>
        Rewrite(store, Array.Empty<FileInfo>(), days, cleanExpired: true, migrateLegacy: true);

    /// <summary>
    /// Single automatic pass: migrate older entries, add analyzed replays when enabled, apply retention when enabled.
    /// The file list must be captured on the UI thread by the caller.
    /// </summary>
    public static ReplayMaintenanceResult Maintain(
        Store store, IReadOnlyList<FileInfo> files, bool archiveEnabled, int retentionDays) =>
        Rewrite(
            store,
            archiveEnabled ? files : Array.Empty<FileInfo>(),
            retentionDays,
            cleanExpired: retentionDays > 0,
            migrateLegacy: true);

    /// <summary>Automatic pass using the current tracker snapshot; kept for callers that do not capture a plan.</summary>
    public static ReplayMaintenanceResult Maintain(Store store, Config config, TrackerData data) =>
        Maintain(
            store,
            config.ReplayArchiveEnabled ? PlanArchive(config, data).Files : Array.Empty<FileInfo>(),
            config.ReplayArchiveEnabled,
            config.ReplayArchiveRetentionDays);

    private static ReplayMaintenanceResult Rewrite(
        Store store,
        IReadOnlyList<FileInfo> newFiles,
        int retentionDays,
        bool cleanExpired,
        bool migrateLegacy)
    {
        lock (ArchiveGate)
        {
            retentionDays = cleanExpired ? NormalizeRetention(retentionDays) : 0;
            var archivePath = ArchiveFilePath(store);
            var archiveExists = File.Exists(archivePath);
            var legacyFiles = migrateLegacy ? EnumerateLegacyFiles(store) : new List<FileInfo>();
            var cutoff = retentionDays > 0 ? DateTime.UtcNow.AddDays(-retentionDays) : DateTime.MaxValue;

            // Nothing to do: avoid rewriting the archive for an empty periodic pass.
            if (!archiveExists && newFiles.Count == 0 && legacyFiles.Count == 0)
            {
                return new ReplayMaintenanceResult(0, 0, 0, 0, 0);
            }
            if (newFiles.Count == 0 && legacyFiles.Count == 0
                && (retentionDays <= 0 || !HasExpired(archivePath, cutoff)))
            {
                return new ReplayMaintenanceResult(0, 0, 0, 0, 0);
            }

            var missing = newFiles.Count(f => !f.Exists);
            var files = newFiles.Where(f => f.Exists).ToList();
            var archived = 0;
            long archivedBytes = 0;
            var deleted = 0;
            long deletedBytes = 0;
            var compressed = 0;
            long compressedBytes = 0;
            var failed = missing;
            var written = 0;
            var newToDelete = new List<FileInfo>();
            var legacyToDelete = new List<FileInfo>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var temporary = archivePath + ".tmp";

            try
            {
                Directory.CreateDirectory(store.ReplayArchivePath);
                TryDelete(temporary);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var target = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    // Entries already in the single archive, optionally dropping the expired ones.
                    if (archiveExists)
                    {
                        using var source = ZipFile.OpenRead(archivePath);
                        foreach (var entry in source.Entries.Where(e => !IsDirectory(e)))
                        {
                            var at = ArchivedAt(entry);
                            if (retentionDays > 0 && at.UtcDateTime <= cutoff)
                            {
                                deleted++;
                                deletedBytes += SafeCompressedLength(entry);
                                continue;
                            }
                            var name = UniqueEntryName(entry.FullName, names);
                            CopyEntry(entry, target, name, at);
                            names.Add(name);
                            written++;
                        }
                    }

                    // Older raw replays and per-replay ZIPs are folded into the same archive.
                    foreach (var legacy in legacyFiles)
                    {
                        if (string.Equals(legacy.Extension, ".replay", StringComparison.OrdinalIgnoreCase))
                        {
                            var at = ArchivedAt(legacy);
                            if (retentionDays > 0 && at <= cutoff)
                            {
                                deleted++;
                                deletedBytes += SafeLength(legacy);
                                legacyToDelete.Add(legacy);
                                continue;
                            }
                            var name = UniqueEntryName(legacy.Name, names);
                            AddFile(target, legacy, name, ToZipTimestamp(at));
                            names.Add(name);
                            compressed++;
                            compressedBytes += SafeLength(legacy);
                            written++;
                            legacyToDelete.Add(legacy);
                            continue;
                        }

                        using var oldArchive = ZipFile.OpenRead(legacy.FullName);
                        foreach (var entry in oldArchive.Entries.Where(e => !IsDirectory(e)))
                        {
                            var at = ArchivedAt(entry);
                            if (retentionDays > 0 && at.UtcDateTime <= cutoff)
                            {
                                deleted++;
                                deletedBytes += SafeCompressedLength(entry);
                                continue;
                            }
                            var name = UniqueEntryName(entry.FullName, names);
                            CopyEntry(entry, target, name, at);
                            names.Add(name);
                            compressed++;
                            compressedBytes += SafeCompressedLength(entry);
                            written++;
                        }
                        legacyToDelete.Add(legacy);
                    }

                    // New replays captured on the UI thread before this background pass.
                    foreach (var file in files)
                    {
                        var name = UniqueEntryName(file.Name, names);
                        AddFile(target, file, name, DateTimeOffset.Now);
                        names.Add(name);
                        archived++;
                        archivedBytes += SafeLength(file);
                        written++;
                        newToDelete.Add(file);
                    }
                }

                if (written == 0)
                {
                    TryDelete(temporary);
                    if (archiveExists)
                    {
                        File.Delete(archivePath);
                    }
                }
                else
                {
                    File.Move(temporary, archivePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                ErrorLog.Write("Replay archive: rewrite failed", ex);
                // Keep the old archive and every source file untouched. Report the pass as failed.
                return new ReplayMaintenanceResult(0, 0, 0, 0, Math.Max(1, failed + files.Count + legacyFiles.Count));
            }

            foreach (var file in newToDelete)
            {
                try { File.Delete(file.FullName); }
                catch (Exception ex)
                {
                    failed++;
                    ErrorLog.Write($"Replay archive: cannot remove {file.FullName} after archiving", ex);
                }
            }
            foreach (var legacy in legacyToDelete)
            {
                try { File.Delete(legacy.FullName); }
                catch (Exception ex)
                {
                    failed++;
                    ErrorLog.Write($"Replay archive: cannot remove legacy archive {legacy.FullName}", ex);
                }
            }

            return new ReplayMaintenanceResult(
                archived, archivedBytes, deleted, deletedBytes, failed, compressed, compressedBytes);
        }
    }

    /// <summary>Replay names that belong to a match in the history (including skipped duplicates of a match).</summary>
    private static HashSet<string> AnalyzedNames(TrackerData data)
    {
        var sessions = data.Matches
            .Select(m => m.SessionId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in data.ProcessedFiles)
        {
            if (sessions.Contains(value))
            {
                names.Add(FileNameFromKey(key));
            }
        }
        foreach (var match in data.Matches)
        {
            if (!string.IsNullOrWhiteSpace(match.ReplayFile))
            {
                names.Add(match.ReplayFile);
            }
        }
        return names;
    }

    /// <summary>ProcessedFiles keys are "name|size|lastWriteTicks".</summary>
    private static string FileNameFromKey(string key)
    {
        var separator = key.IndexOf('|');
        return separator < 0 ? key : key[..separator];
    }

    private static List<FileInfo> Enumerate(string folder, string pattern)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            return directory.Exists
                ? directory.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly).ToList()
                : new List<FileInfo>();
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Replay maintenance: cannot read {folder}", ex);
            return new List<FileInfo>();
        }
    }

    /// <summary>Raw replays and per-replay ZIPs created by older versions, excluding the single archive.</summary>
    private static List<FileInfo> EnumerateLegacyFiles(Store store)
    {
        var archivePath = ArchiveFilePath(store);
        return Enumerate(store.ReplayArchivePath, "*.replay")
            .Concat(Enumerate(store.ReplayArchivePath, "*.zip")
                .Where(f => !string.Equals(f.FullName, archivePath, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !string.Equals(f.Extension, ".tmp", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static (int Count, long Bytes) Measure(string folder, string pattern)
    {
        var files = Enumerate(folder, pattern);
        return (files.Count, files.Sum(SafeLength));
    }

    private static (int Count, long Bytes) MeasureArchive(Store store)
    {
        var archivePath = ArchiveFilePath(store);
        if (!File.Exists(archivePath))
        {
            return (0, 0);
        }
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var count = archive.Entries.Count(e => !IsDirectory(e));
            return (count, SafeLength(new FileInfo(archivePath)));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay archive: cannot measure the archive", ex);
            return (0, 0);
        }
    }

    private static bool HasExpired(string archivePath, DateTime cutoff)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Any(e => !IsDirectory(e) && ArchivedAt(e).UtcDateTime <= cutoff);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay archive: cannot inspect the archive", ex);
            return false;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0; }
    }

    private static long SafeCompressedLength(ZipArchiveEntry entry)
    {
        try { return entry.CompressedLength; }
        catch { return 0; }
    }

    /// <summary>Creation time is set when a replay enters the archive; fall back to the original date if needed.</summary>
    private static DateTime ArchivedAt(FileInfo file)
    {
        try
        {
            var created = file.CreationTimeUtc;
            return created == DateTime.MinValue || created > DateTime.UtcNow.AddDays(1)
                ? file.LastWriteTimeUtc
                : created;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static DateTimeOffset ArchivedAt(ZipArchiveEntry entry)
    {
        try
        {
            var value = entry.LastWriteTime;
            return value == default ? DateTimeOffset.Now : value;
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }

    /// <summary>ZIP dates cannot represent dates before 1980; an invalid source date becomes now.</summary>
    private static DateTimeOffset ToZipTimestamp(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return local.Year < 1980 || local > DateTime.Now.AddDays(1)
            ? DateTimeOffset.Now
            : new DateTimeOffset(local);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal)
        || entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static string UniqueEntryName(string desired, HashSet<string> used)
    {
        var name = Path.GetFileName(desired.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "replay";
        }
        if (!used.Contains(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}{extension}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyEntry(ZipArchiveEntry source, ZipArchive target, string name, DateTimeOffset at)
    {
        var entry = target.CreateEntry(name, CompressionLevel.SmallestSize);
        try { entry.LastWriteTime = at; } catch { /* keep the default timestamp */ }
        using var input = source.Open();
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void AddFile(ZipArchive target, FileInfo file, string name, DateTimeOffset at)
    {
        var entry = target.CreateEntry(name, CompressionLevel.SmallestSize);
        try { entry.LastWriteTime = at; } catch { /* keep the default timestamp */ }
        using var input = new FileStream(
            file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
