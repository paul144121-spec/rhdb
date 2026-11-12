using System.Security.Cryptography;

namespace NexusOptimizer.Core.Storage;

public sealed record LargeFileInfo(string Path, long Bytes, DateTime LastWriteUtc);

public sealed record DuplicateGroup(long FileSize, string Sha256, IReadOnlyList<string> Paths)
{
    /// <summary>Speicherplatz, der frei würde, wenn alle bis auf eine Kopie entfernt werden.</summary>
    public long WastedBytes => FileSize * (Paths.Count - 1);
}

public sealed record ScanProgress(int FilesSeen, string CurrentPath);

/// <summary>Sucht große Dateien und echte Duplikate in einem vom Benutzer gewählten Ordner. Rein lesend.</summary>
public static class FileAnalysis
{
    public static IReadOnlyList<LargeFileInfo> FindLargestFiles(string root, int count = 100, long minimumBytes = 50L * 1024 * 1024, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var queue = new PriorityQueue<LargeFileInfo, long>();
        var seen = 0;
        foreach (var file in EnumerateFilesSafe(root, cancellationToken))
        {
            if (++seen % 500 == 0) progress?.Report(new ScanProgress(seen, file.DirectoryName ?? ""));
            long length;
            try { length = file.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (length < minimumBytes) continue;

            queue.Enqueue(new LargeFileInfo(file.FullName, length, file.LastWriteTimeUtc), length);
            if (queue.Count > count) queue.Dequeue();
        }

        var result = new List<LargeFileInfo>(queue.Count);
        while (queue.Count > 0) result.Add(queue.Dequeue());
        result.Reverse();
        return result;
    }

    /// <summary>
    /// Dreistufiger Duplikatvergleich: gleiche Größe → gleicher Hash der ersten 64 KiB → gleicher SHA-256 der gesamten Datei.
    /// Leere Dateien und Dateien unter <paramref name="minimumBytes"/> werden ignoriert.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> FindDuplicates(string root, long minimumBytes = 1024 * 1024, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var bySize = new Dictionary<long, List<string>>();
        var seen = 0;
        foreach (var file in EnumerateFilesSafe(root, cancellationToken))
        {
            if (++seen % 500 == 0) progress?.Report(new ScanProgress(seen, file.DirectoryName ?? ""));
            long length;
            try { length = file.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (length < Math.Max(1, minimumBytes)) continue;
            if (!bySize.TryGetValue(length, out var list)) bySize[length] = list = new List<string>();
            list.Add(file.FullName);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (size, paths) in bySize.Where(p => p.Value.Count > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var partial in GroupByHash(paths, 64 * 1024, cancellationToken).Where(g => g.Value.Count > 1))
            {
                foreach (var full in GroupByHash(partial.Value, long.MaxValue, cancellationToken).Where(g => g.Value.Count > 1))
                    groups.Add(new DuplicateGroup(size, full.Key, full.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }
        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static Dictionary<string, List<string>> GroupByHash(IEnumerable<string> paths, long maxBytes, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = TryHash(path, maxBytes, cancellationToken);
            if (hash is null) continue;
            if (!result.TryGetValue(hash, out var list)) result[hash] = list = new List<string>();
            list.Add(path);
        }
        return result;
    }

    private static string? TryHash(string path, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long remaining = maxBytes;
            int read;
            while (remaining > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // gesperrte oder nicht lesbare Dateien werden nie als Duplikat gemeldet
        }
    }

    /// <summary>Durchläuft einen Ordnerbaum, ohne Verknüpfungspunkten zu folgen und ohne an Zugriffsfehlern zu scheitern.</summary>
    public static IEnumerable<FileInfo> EnumerateFilesSafe(string root, CancellationToken cancellationToken = default)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileInfo[] files;
            DirectoryInfo[] directories;
            try
            {
                files = directory.GetFiles();
                directories = directory.GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null) continue;
                yield return file;
            }
            foreach (var sub in directories)
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || sub.LinkTarget is not null) continue;
                pending.Push(sub);
            }
        }
    }
}
