using System.IO.Compression;

namespace ChestCounter;

/// <summary>Disk usage of the Fortnite replays folder and of the archive made by the counter.</summary>
public sealed record ReplayStorageStats(int FolderCount, long FolderBytes, int ArchiveCount, long ArchiveBytes)
{
    public static ReplayStorageStats Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>What an archive action would move. The file list is captured on the UI thread before any background work.</summary>
public sealed record ReplayArchivePlan(int Count, long Bytes, IReadOnlyList<FileInfo> Files);

/// <summary>Outcome of an archive, compression or cleanup pass.</summary>
public sealed record ReplayMaintenanceResult(
    int Archived, long ArchivedBytes,
    int Deleted, long DeletedBytes,
    int Failed,
    int Compressed = 0, long CompressedBytes = 0)
{
    public bool Changed => Archived > 0 || Deleted > 0 || Compressed > 0;
}

/// <summary>
/// Moves replays that have already been analyzed into a local archive, compressed as individual ZIP files. Only
/// matches present in the history are touched, never a replay still being recorded, and deletion only ever happens
/// inside the archive. Existing uncompressed archive entries from the first version are compressed automatically.
/// </summary>
public static class ReplayMaintenance
{
    /// <summary>Retention choices offered by the interface; 0 means "never delete".</summary>
    public static readonly int[] RetentionDays = { 0, 7, 30, 90 };

    public static int NormalizeRetention(int days) => RetentionDays.Contains(days) ? days : 0;

    /// <summary>Counts and sizes of every replay in the Fortnite folder and of every ZIP/legacy archive entry.</summary>
    public static ReplayStorageStats Inspect(Store store, Config config)
    {
        var folder = Measure(config.DemosFolder, "*.replay");
        var archive = MeasureArchive(store.ReplayArchivePath);
        return new ReplayStorageStats(folder.Count, folder.Bytes, archive.Count, archive.Bytes);
    }

    /// <summary>True when an earlier version left uncompressed replays in the archive.</summary>
    public static bool HasUncompressedArchive(Store store) => Enumerate(store.ReplayArchivePath, "*.replay").Count > 0;

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

    /// <summary>Archived ZIPs or legacy replays older than the given retention period. 0 keeps everything.</summary>
    public static ReplayArchivePlan PlanDelete(Store store, int days)
    {
        days = NormalizeRetention(days);
        if (days <= 0)
        {
            return new ReplayArchivePlan(0, 0, Array.Empty<FileInfo>());
        }
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var files = EnumerateArchive(store.ReplayArchivePath)
            .Where(f => ArchivedAt(f) <= cutoff)
            .ToList();
        return new ReplayArchivePlan(files.Count, files.Sum(SafeLength), files);
    }

    public static ReplayMaintenanceResult ArchiveAnalyzed(Store store, Config config, TrackerData data) =>
        ArchiveFiles(store, PlanArchive(config, data).Files);

    /// <summary>Compresses the files captured by <see cref="PlanArchive"/>; safe to run on a background thread.</summary>
    public static ReplayMaintenanceResult ArchiveFiles(Store store, IReadOnlyList<FileInfo> files)
    {
        if (files.Count == 0)
        {
            return new ReplayMaintenanceResult(0, 0, 0, 0, 0);
        }

        var archived = 0;
        long bytes = 0;
        var failed = 0;
        try
        {
            Directory.CreateDirectory(store.ReplayArchivePath);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay archive: cannot create the archive folder", ex);
            return new ReplayMaintenanceResult(0, 0, 0, 0, files.Count);
        }

        foreach (var file in files)
        {
            try
            {
                var destination = UniqueArchiveDestination(store.ReplayArchivePath, file.Name);
                CompressReplay(file.FullName, destination, DateTime.UtcNow);
                archived++;
                bytes += SafeLength(new FileInfo(destination));
            }
            catch (Exception ex)
            {
                failed++;
                ErrorLog.Write($"Replay archive: {file.FullName}", ex);
            }
        }
        return new ReplayMaintenanceResult(archived, bytes, 0, 0, failed);
    }

    /// <summary>Compresses legacy .replay files already sitting in the archive folder, preserving their archive date.</summary>
    public static ReplayMaintenanceResult CompressArchive(Store store)
    {
        var compressed = 0;
        long bytes = 0;
        var failed = 0;
        foreach (var file in Enumerate(store.ReplayArchivePath, "*.replay"))
        {
            try
            {
                var createdAt = ArchivedAt(file);
                var destination = UniqueArchiveDestination(store.ReplayArchivePath, file.Name);
                CompressReplay(file.FullName, destination, createdAt);
                compressed++;
                bytes += SafeLength(new FileInfo(destination));
            }
            catch (Exception ex)
            {
                failed++;
                ErrorLog.Write($"Replay archive: cannot compress {file.FullName}", ex);
            }
        }
        return new ReplayMaintenanceResult(0, 0, 0, 0, failed, compressed, bytes);
    }

    public static ReplayMaintenanceResult DeleteExpired(Store store, int days)
    {
        days = NormalizeRetention(days);
        if (days <= 0)
        {
            return new ReplayMaintenanceResult(0, 0, 0, 0, 0);
        }
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var deleted = 0;
        long bytes = 0;
        var failed = 0;
        foreach (var file in EnumerateArchive(store.ReplayArchivePath))
        {
            if (ArchivedAt(file) > cutoff)
            {
                continue;
            }
            var length = SafeLength(file);
            try
            {
                File.Delete(file.FullName);
                deleted++;
                bytes += length;
            }
            catch (Exception ex)
            {
                failed++;
                ErrorLog.Write($"Replay archive: cannot delete {file.FullName}", ex);
            }
        }
        return new ReplayMaintenanceResult(0, 0, deleted, bytes, failed);
    }

    /// <summary>Automatic pass: compress legacy entries, archive analyzed replays when enabled, then apply retention.</summary>
    public static ReplayMaintenanceResult Maintain(Store store, Config config, TrackerData data)
    {
        var compressed = CompressArchive(store);
        var archive = config.ReplayArchiveEnabled
            ? ArchiveAnalyzed(store, config, data)
            : new ReplayMaintenanceResult(0, 0, 0, 0, 0);
        var delete = config.ReplayArchiveRetentionDays > 0
            ? DeleteExpired(store, config.ReplayArchiveRetentionDays)
            : new ReplayMaintenanceResult(0, 0, 0, 0, 0);
        return new ReplayMaintenanceResult(
            archive.Archived, archive.ArchivedBytes,
            delete.Deleted, delete.DeletedBytes,
            compressed.Failed + archive.Failed + delete.Failed,
            compressed.Compressed, compressed.CompressedBytes);
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

    private static List<FileInfo> EnumerateArchive(string folder) =>
        Enumerate(folder, "*.zip").Concat(Enumerate(folder, "*.replay")).ToList();

    private static (int Count, long Bytes) Measure(string folder, string pattern)
    {
        var files = Enumerate(folder, pattern);
        return (files.Count, files.Sum(SafeLength));
    }

    private static (int Count, long Bytes) MeasureArchive(string folder)
    {
        var files = EnumerateArchive(folder);
        return (files.Count, files.Sum(SafeLength));
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
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

    private static string UniqueArchiveDestination(string archive, string replayName)
    {
        var name = replayName + ".zip";
        var destination = Path.Combine(archive, name);
        if (!File.Exists(destination))
        {
            return destination;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; ; i++)
        {
            destination = Path.Combine(archive, $"{stem}-{i}.zip");
            if (!File.Exists(destination))
            {
                return destination;
            }
        }
    }

    /// <summary>
    /// Writes a temporary ZIP next to its destination, then removes the original replay only once the ZIP exists.
    /// A failed compression leaves the replay untouched.
    /// </summary>
    private static void CompressReplay(string source, string destination, DateTime archivedAt)
    {
        var temporary = destination + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(Path.GetFileName(source), CompressionLevel.SmallestSize);
                entry.LastWriteTime = DateTimeOffset.Now;
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open();
                input.CopyTo(target);
            }
            File.Move(temporary, destination);
            try { File.SetCreationTimeUtc(destination, archivedAt); } catch { /* retention falls back to the last write time */ }
            try
            {
                File.Delete(source);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
