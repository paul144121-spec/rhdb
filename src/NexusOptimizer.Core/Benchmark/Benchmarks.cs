using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NexusOptimizer.Core.Benchmark;

public static class Statistics
{
    public static double Mean(IReadOnlyList<double> values) => values.Count == 0 ? double.NaN : values.Average();

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        return Percentile(sorted, 50);
    }

    /// <summary>Stichprobenstandardabweichung (n − 1).</summary>
    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    /// <summary>Variationskoeffizient in Prozent – Maß für die Wiederholgenauigkeit.</summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        var mean = Mean(values);
        return values.Count < 2 || mean == 0 ? 0 : 100.0 * StandardDeviation(values) / Math.Abs(mean);
    }

    /// <summary>Perzentil mit linearer Interpolation; erwartet aufsteigend sortierte Werte.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        var rank = Math.Clamp(percentile, 0, 100) / 100.0 * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    /// <summary>Welch-t-Statistik und Freiheitsgrade für zwei unabhängige Stichproben mit ungleicher Varianz.</summary>
    public static (double T, double DegreesOfFreedom) WelchT(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var varianceA = Math.Pow(StandardDeviation(a), 2) / a.Count;
        var varianceB = Math.Pow(StandardDeviation(b), 2) / b.Count;
        var denominator = Math.Sqrt(varianceA + varianceB);
        if (denominator == 0) return (Mean(a) == Mean(b) ? 0 : double.PositiveInfinity, a.Count + b.Count - 2);
        var t = (Mean(b) - Mean(a)) / denominator;
        var df = Math.Pow(varianceA + varianceB, 2) / (Math.Pow(varianceA, 2) / (a.Count - 1) + Math.Pow(varianceB, 2) / (b.Count - 1));
        return (t, double.IsNaN(df) ? a.Count + b.Count - 2 : df);
    }

    /// <summary>Kritischer t-Wert für ein zweiseitiges 95-%-Niveau (Tabelle, zwischen Stützstellen konservativ aufgerundet).</summary>
    public static double CriticalT95(double degreesOfFreedom)
    {
        (double Df, double T)[] table =
        {
            (1, 12.706), (2, 4.303), (3, 3.182), (4, 2.776), (5, 2.571), (6, 2.447), (7, 2.365), (8, 2.306), (9, 2.262), (10, 2.228),
            (12, 2.179), (15, 2.131), (20, 2.086), (30, 2.042), (60, 2.000), (120, 1.980),
        };
        foreach (var (df, t) in table)
            if (degreesOfFreedom <= df) return t;
        return 1.96;
    }
}

public enum ComparisonVerdict
{
    InsufficientData,
    NoSignificantChange,
    Improved,
    Regressed,
}

public sealed record MeasurementComparison(ComparisonVerdict Verdict, double BaselineMean, double CandidateMean, double ChangePercent, double NoisePercent, string Explanation);

/// <summary>
/// Vergleicht Vorher-/Nachher-Messreihen. Eine Veränderung gilt nur dann als echt, wenn sie statistisch signifikant ist (Welch-Test, 95 %)
/// UND eine Mindestgröße überschreitet, die über dem gemessenen Rauschen liegt. Zufallsschwankungen werden so nicht als Verbesserung gedeutet.
/// </summary>
public static class MeasurementComparer
{
    public const int MinimumSamples = 3;
    public const double MinimumEffectPercent = 2.0;

    public static MeasurementComparison Compare(IReadOnlyList<double> baseline, IReadOnlyList<double> candidate, bool higherIsBetter)
    {
        var baseMean = Statistics.Mean(baseline);
        var candMean = Statistics.Mean(candidate);
        if (baseline.Count < MinimumSamples || candidate.Count < MinimumSamples || double.IsNaN(baseMean) || baseMean == 0)
            return new MeasurementComparison(ComparisonVerdict.InsufficientData, baseMean, candMean, 0, 0,
                $"Für einen belastbaren Vergleich sind mindestens {MinimumSamples} Messungen je Reihe nötig.");

        var change = 100.0 * (candMean - baseMean) / Math.Abs(baseMean);
        var noise = Math.Max(Statistics.CoefficientOfVariation(baseline), Statistics.CoefficientOfVariation(candidate));
        var (t, df) = Statistics.WelchT(baseline, candidate);
        var significant = Math.Abs(t) > Statistics.CriticalT95(df);
        var relevant = Math.Abs(change) >= Math.Max(MinimumEffectPercent, noise);

        if (!significant || !relevant)
        {
            return new MeasurementComparison(ComparisonVerdict.NoSignificantChange, baseMean, candMean, change, noise,
                $"Die Abweichung von {change:+0.0;-0.0} % liegt innerhalb der Messunsicherheit (Streuung der Messungen: ±{noise:0.0} %). Es ist keine echte Veränderung nachweisbar.");
        }

        var better = higherIsBetter ? change > 0 : change < 0;
        return new MeasurementComparison(better ? ComparisonVerdict.Improved : ComparisonVerdict.Regressed, baseMean, candMean, change, noise,
            better
                ? $"Messbare Verbesserung um {Math.Abs(change):0.0} % (Streuung ±{noise:0.0} %, statistisch signifikant)."
                : $"Messbare Verschlechterung um {Math.Abs(change):0.0} % (Streuung ±{noise:0.0} %, statistisch signifikant). Es wird empfohlen, die Änderung rückgängig zu machen.");
    }
}

public enum BenchmarkKind
{
    CpuSingleThread,
    CpuMultiThread,
    MemoryThroughput,
    DiskSequentialWrite,
    DiskSequentialRead,
    DiskRandomRead,
    SchedulerLatency,
}

public sealed record BenchmarkResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required BenchmarkKind Kind { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required IReadOnlyList<double> Samples { get; init; }
    public required string Unit { get; init; }
    public bool HigherIsBetter { get; init; } = true;
    public string? Label { get; init; }
    public string? Conditions { get; init; }
    public string? Target { get; init; }
    public double Score => Statistics.Median(Samples);
    public double NoisePercent => Statistics.CoefficientOfVariation(Samples);
}

public sealed record BenchmarkDescription(BenchmarkKind Kind, string Name, string Unit, bool HigherIsBetter, string Methodology, string Limitations);

public sealed record BenchmarkOptions
{
    public int Runs { get; init; } = 5;
    public TimeSpan RunDuration { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Größe der temporären Testdatei für Datenträgertests.</summary>
    public long DiskFileBytes { get; init; } = 256L * 1024 * 1024;
    public string? DiskDirectory { get; init; }
}

public interface IBenchmarkStore
{
    void Save(BenchmarkResult result);
    IReadOnlyList<BenchmarkResult> GetHistory(BenchmarkKind? kind = null, int limit = 200);
    void DeleteAll();
}

/// <summary>
/// Integrierte, wiederholbare Leistungstests mit dokumentierter Methodik. Alle Werte sind tatsächlich gemessen;
/// es gibt bewusst keinen zusammengefassten „Optimierungs-Score“.
/// </summary>
public sealed class BenchmarkRunner
{
    public static IReadOnlyList<BenchmarkDescription> Descriptions { get; } = new[]
    {
        new BenchmarkDescription(BenchmarkKind.CpuSingleThread, "CPU – ein Thread", "MB/s", true,
            "SHA-256 über einen 4-MiB-Puffer im Arbeitsspeicher auf einem Thread. Nach einer Aufwärmphase werden mehrere Läufe fester Dauer gemessen; Ergebnis ist der Median des Durchsatzes.",
            "Misst nur Ganzzahl-/Kryptografieleistung eines Kerns (mit Hardwarebeschleunigung, falls vorhanden). Hintergrundlast, Energieplan und Temperatur beeinflussen das Ergebnis."),
        new BenchmarkDescription(BenchmarkKind.CpuMultiThread, "CPU – alle Threads", "MB/s", true,
            "Wie der Ein-Thread-Test, jedoch parallel auf allen logischen Prozessoren; Ergebnis ist der Median des summierten Durchsatzes.",
            "Längere Läufe können durch thermische Drosselung abfallen – das ist dann eine Eigenschaft des Systems, kein Messfehler."),
        new BenchmarkDescription(BenchmarkKind.MemoryThroughput, "Arbeitsspeicher – Kopierdurchsatz", "MB/s", true,
            "Wiederholtes Kopieren eines 128-MiB-Blocks (größer als übliche CPU-Caches) auf einem Thread; Ergebnis ist der Median des Durchsatzes.",
            "Einzelthread-Wert; hängt von RAM-Takt, Kanalbestückung und CPU ab. Kein Ersatz für einen Speichertest auf Fehler."),
        new BenchmarkDescription(BenchmarkKind.DiskSequentialWrite, "Datenträger – sequenzielles Schreiben", "MB/s", true,
            "Schreibt eine temporäre Testdatei in 1-MiB-Blöcken mit Durchschreiben auf den Datenträger (ohne Betriebssystem-Cache). Die Datei wird anschließend gelöscht.",
            "Verursacht Schreiblast auf dem Datenträger (bei SSDs Abnutzung in Höhe der Testdateigröße je Lauf). SSDs mit SLC-Cache zeigen bei kleinen Testdateien höhere Werte als im Dauerbetrieb."),
        new BenchmarkDescription(BenchmarkKind.DiskSequentialRead, "Datenträger – sequenzielles Lesen", "MB/s", true,
            "Liest die zuvor geschriebene Testdatei in 1-MiB-Blöcken. Es wird versucht, den Dateicache zu umgehen.",
            "Wenn der Dateicache nicht umgangen werden kann, misst der Test teilweise Arbeitsspeicher statt Datenträger; ungewöhnlich hohe Werte deuten darauf hin."),
        new BenchmarkDescription(BenchmarkKind.DiskRandomRead, "Datenträger – zufälliges Lesen (4 KiB)", "IOPS", true,
            "Liest für eine feste Dauer 4-KiB-Blöcke an zufälligen, ausgerichteten Positionen der Testdatei (Warteschlangentiefe 1).",
            "Warteschlangentiefe 1 auf einem Thread – nicht vergleichbar mit Herstellerangaben, die hohe Warteschlangentiefen verwenden."),
        new BenchmarkDescription(BenchmarkKind.SchedulerLatency, "System – Zeitgeber-/Scheduler-Latenz", "ms", false,
            "Fordert wiederholt eine Wartezeit von 1 ms an und misst die tatsächliche Dauer mit dem hochauflösenden Zähler. Ergebnis je Lauf ist das 99. Perzentil der Abweichung.",
            "Zeigt, wie pünktlich das System einen wartenden Thread weckt. Abhängig von Zeitgeberauflösung, Energiezuständen und Hintergrundlast; kein direktes Maß für Eingabelatenz."),
    };

    public static BenchmarkDescription Describe(BenchmarkKind kind) => Descriptions.First(d => d.Kind == kind);

    public async Task<BenchmarkResult> RunAsync(BenchmarkKind kind, BenchmarkOptions? options = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new BenchmarkOptions();
        var description = Describe(kind);
        var samples = await Task.Factory.StartNew(() => kind switch
        {
            BenchmarkKind.CpuSingleThread => RunRepeated(options, progress, cancellationToken, duration => CpuHashThroughput(1, duration, cancellationToken)),
            BenchmarkKind.CpuMultiThread => RunRepeated(options, progress, cancellationToken, duration => CpuHashThroughput(Environment.ProcessorCount, duration, cancellationToken)),
            BenchmarkKind.MemoryThroughput => RunRepeated(options, progress, cancellationToken, duration => MemoryCopyThroughput(duration, cancellationToken)),
            BenchmarkKind.SchedulerLatency => RunRepeated(options, progress, cancellationToken, duration => SchedulerLatencyP99(duration, cancellationToken)),
            BenchmarkKind.DiskSequentialWrite or BenchmarkKind.DiskSequentialRead or BenchmarkKind.DiskRandomRead => RunDisk(kind, options, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);

        return new BenchmarkResult
        {
            Kind = kind,
            Timestamp = DateTimeOffset.UtcNow,
            Samples = samples,
            Unit = description.Unit,
            HigherIsBetter = description.HigherIsBetter,
            Target = kind is BenchmarkKind.DiskSequentialWrite or BenchmarkKind.DiskSequentialRead or BenchmarkKind.DiskRandomRead ? Path.GetPathRoot(Path.GetFullPath(options.DiskDirectory ?? Path.GetTempPath())) : null,
        };
    }

    private static List<double> RunRepeated(BenchmarkOptions options, IProgress<string>? progress, CancellationToken cancellationToken, Func<TimeSpan, double> run)
    {
        progress?.Report("Aufwärmphase …");
        if (options.WarmUp > TimeSpan.Zero) run(options.WarmUp);
        var samples = new List<double>(options.Runs);
        for (var i = 0; i < options.Runs; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Lauf {i + 1} von {options.Runs} …");
            samples.Add(run(options.RunDuration));
        }
        return samples;
    }

    private static double CpuHashThroughput(int threads, TimeSpan duration, CancellationToken cancellationToken)
    {
        const int BufferSize = 4 * 1024 * 1024;
        long totalBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(index => new Thread(() =>
        {
            var buffer = new byte[BufferSize];
            new Random(1234 + index).NextBytes(buffer);
            Span<byte> hash = stackalloc byte[32];
            long local = 0;
            // Mindestens ein Durchlauf – auch wenn der Thread unter hoher Systemlast erst spät startet.
            do
            {
                SHA256.HashData(buffer, hash);
                buffer[0] = hash[0]; // verhindert, dass der Lauf wegoptimiert wird
                local += BufferSize;
            } while (stopwatch.Elapsed < duration && !cancellationToken.IsCancellationRequested);
            Interlocked.Add(ref totalBytes, local);
        }) { IsBackground = true }).ToList();

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        return totalBytes / (1024.0 * 1024.0) / stopwatch.Elapsed.TotalSeconds;
    }

    private static double MemoryCopyThroughput(TimeSpan duration, CancellationToken cancellationToken)
    {
        const int BlockSize = 128 * 1024 * 1024;
        var source = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: false);
        var destination = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: false);
        // Seiten einmal berühren, damit die Messung keine Seitenfehler enthält.
        for (var i = 0; i < BlockSize; i += 4096) { source[i] = (byte)i; destination[i] = 1; }

        long copied = 0;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            Buffer.BlockCopy(source, 0, destination, 0, BlockSize);
            copied += BlockSize;
        } while (stopwatch.Elapsed < duration && !cancellationToken.IsCancellationRequested);
        stopwatch.Stop();
        GC.KeepAlive(destination);
        return copied / (1024.0 * 1024.0) / stopwatch.Elapsed.TotalSeconds;
    }

    private static double SchedulerLatencyP99(TimeSpan duration, CancellationToken cancellationToken)
    {
        var deviations = new List<double>(4096);
        var total = Stopwatch.StartNew();
        do
        {
            var start = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            deviations.Add(Math.Max(0, elapsedMs - 1.0));
        } while (total.Elapsed < duration && !cancellationToken.IsCancellationRequested);
        deviations.Sort();
        return Statistics.Percentile(deviations, 99);
    }

    private static List<double> RunDisk(BenchmarkKind kind, BenchmarkOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        const int Block = 1024 * 1024;
        var directory = options.DiskDirectory ?? Path.GetTempPath();
        var size = Math.Clamp(options.DiskFileBytes, 16L * Block, 4096L * Block) / Block * Block;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        if (drive.AvailableFreeSpace < size * 2 + 512L * Block)
            throw new IOException("Auf dem Ziellaufwerk ist nicht genug Speicherplatz für den Test frei.");

        var path = Path.Combine(directory, $"nexus-benchmark-{Guid.NewGuid():N}.tmp");
        var buffer = AlignedBuffer.Allocate(Block);
        try
        {
            new Random(42).NextBytes(buffer.Span); // nicht komprimierbare Daten
            var samples = new List<double>();
            // Sequenzielles Schreiben wird je Lauf neu ausgeführt; für Lesetests genügt eine einmal geschriebene Datei.
            var writeRuns = kind == BenchmarkKind.DiskSequentialWrite ? options.Runs : 1;
            for (var run = 0; run < writeRuns; run++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(kind == BenchmarkKind.DiskSequentialWrite ? $"Schreiblauf {run + 1} von {writeRuns} …" : "Testdatei wird vorbereitet …");
                var stopwatch = Stopwatch.StartNew();
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.WriteThrough | NoBuffering))
                {
                    for (long written = 0; written < size; written += Block)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Write(buffer.Span);
                    }
                    stream.Flush(flushToDisk: true);
                }
                stopwatch.Stop();
                if (kind == BenchmarkKind.DiskSequentialWrite) samples.Add(size / (double)Block / stopwatch.Elapsed.TotalSeconds);
            }

            if (kind == BenchmarkKind.DiskSequentialRead)
            {
                for (var run = 0; run < options.Runs; run++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"Leselauf {run + 1} von {options.Runs} …");
                    var stopwatch = Stopwatch.StartNew();
                    long read = 0;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 0, FileOptions.SequentialScan | NoBuffering))
                    {
                        int count;
                        while ((count = stream.Read(buffer.Span)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            read += count;
                        }
                    }
                    stopwatch.Stop();
                    samples.Add(read / (double)Block / stopwatch.Elapsed.TotalSeconds);
                }
            }
            else if (kind == BenchmarkKind.DiskRandomRead)
            {
                const int Small = 4096;
                var blocks = size / Small;
                var random = new Random(7);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 0, FileOptions.RandomAccess | NoBuffering);
                var small = buffer.Span[..Small];
                for (var run = 0; run < options.Runs; run++)
                {
                    progress?.Report($"Zufallslauf {run + 1} von {options.Runs} …");
                    long operations = 0;
                    var stopwatch = Stopwatch.StartNew();
                    while (stopwatch.Elapsed < options.RunDuration)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Position = random.NextInt64(blocks) * Small;
                        stream.ReadExactly(small);
                        operations++;
                    }
                    samples.Add(operations / stopwatch.Elapsed.TotalSeconds);
                }
            }
            return samples;
        }
        finally
        {
            buffer.Dispose();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>FILE_FLAG_NO_BUFFERING – umgeht unter Windows den Dateicache. Auf anderen Plattformen (Tests) ohne Wirkung.</summary>
    private static FileOptions NoBuffering => OperatingSystem.IsWindows() ? (FileOptions)0x20000000 : FileOptions.None;

    /// <summary>Für ungepufferte E/A muss der Puffer auf die Sektorgröße ausgerichtet sein.</summary>
    private sealed unsafe class AlignedBuffer : IDisposable
    {
        private void* _pointer;
        private readonly int _length;
        private AlignedBuffer(void* pointer, int length) { _pointer = pointer; _length = length; }
        public static AlignedBuffer Allocate(int length) => new(NativeMemory.AlignedAlloc((nuint)length, 4096), length);
        public Span<byte> Span => new(_pointer, _length);
        public void Dispose()
        {
            if (_pointer is null) return;
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}
