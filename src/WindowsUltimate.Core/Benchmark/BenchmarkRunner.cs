using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Benchmark;

public sealed class BenchmarkResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public double IdleCpuPercent { get; set; }
    public double CpuSingleScore { get; set; }
    public double CpuMultiScore { get; set; }
    public double MemoryCopyGBs { get; set; }
    public double DiskWriteMBs { get; set; }
    public double DiskReadMBs { get; set; }
    public double WakeLatencyP50Us { get; set; }
    public double WakeLatencyP99Us { get; set; }
    public double WakeLatencyMaxUs { get; set; }
    public string PowerPlan { get; set; } = "";
    public List<string> Notes { get; set; } = [];
}

public sealed record BenchmarkMetric(string Name, string Unit, bool HigherIsBetter, double NoisePercent, Func<BenchmarkResult, double> Value, string Explanation);

/// <summary>
/// A short, repeatable system benchmark used to verify that optimizations actually changed something.
/// Each test runs several times and reports the median. It measures the system, not game FPS: for FPS use an
/// in-game benchmark or a frame-time tool such as PresentMon on the same scene before and after.
/// </summary>
public static class BenchmarkRunner
{
    public static readonly IReadOnlyList<BenchmarkMetric> Metrics =
    [
        new("Background CPU at idle", "%", false, 50, r => r.IdleCpuPercent, "CPU used by other programs while nothing is running. Lower leaves more for games."),
        new("CPU single-thread", "pts", true, 3, r => r.CpuSingleScore, "Integer + floating-point workload on one core. Reflects game main-thread speed."),
        new("CPU multi-thread", "pts", true, 4, r => r.CpuMultiScore, "Same workload on all threads."),
        new("Memory copy bandwidth", "GB/s", true, 5, r => r.MemoryCopyGBs, "Large buffer copy. Sensitive to dual-channel and XMP/EXPO."),
        new("Disk sequential write", "MB/s", true, 8, r => r.DiskWriteMBs, "512 MB unbuffered write to the temp folder's drive."),
        new("Disk sequential read", "MB/s", true, 8, r => r.DiskReadMBs, "512 MB unbuffered read of the same file."),
        new("Thread wake latency (median)", "µs", false, 15, r => r.WakeLatencyP50Us, "Time for a sleeping thread to run after being signalled. Reflects scheduler/power-state responsiveness."),
        new("Thread wake latency (99th pct.)", "µs", false, 25, r => r.WakeLatencyP99Us, "Worst 1% of wake-ups. Spikes here correlate with stutter and input-lag complaints."),
    ];

    public static List<BenchmarkResult> History() => JsonStore.Load<List<BenchmarkResult>>(AppPaths.BenchmarksFile) ?? [];

    public static void Save(BenchmarkResult r)
    {
        var all = History();
        all.Add(r);
        JsonStore.Save(AppPaths.BenchmarksFile, all.TakeLast(100).ToList());
    }

    public static void Delete(Guid id)
    {
        var all = History();
        all.RemoveAll(r => r.Id == id);
        JsonStore.Save(AppPaths.BenchmarksFile, all);
    }

    public static async Task<BenchmarkResult> RunAsync(string label, IProgress<(string Step, double Fraction)>? progress = null, CancellationToken ct = default)
    {
        var r = new BenchmarkResult { Label = label, PowerPlan = SystemChecks.PowerPlan()?.Name ?? "" };

        progress?.Report(("Measuring background activity", 0.02));
        r.IdleCpuPercent = await SystemChecks.SampleCpuAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (r.IdleCpuPercent > 15) r.Notes.Add($"Background CPU was {r.IdleCpuPercent:0}% during the run; close other programs for comparable results.");

        await Task.Run(() =>
        {
            progress?.Report(("CPU single-thread", 0.12));
            r.CpuSingleScore = Median(3, () => CpuScore(1, TimeSpan.FromSeconds(1.5), ct));
            ct.ThrowIfCancellationRequested();
            progress?.Report(("CPU multi-thread", 0.32));
            r.CpuMultiScore = Median(3, () => CpuScore(Environment.ProcessorCount, TimeSpan.FromSeconds(1.5), ct));
            ct.ThrowIfCancellationRequested();
            progress?.Report(("Memory bandwidth", 0.52));
            r.MemoryCopyGBs = Median(3, () => MemoryCopy(TimeSpan.FromSeconds(1)));
            ct.ThrowIfCancellationRequested();
            progress?.Report(("Disk", 0.65));
            try
            {
                (r.DiskWriteMBs, r.DiskReadMBs) = Disk(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                r.Notes.Add("Disk test skipped: " + ex.Message);
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(("Thread wake latency", 0.85));
            (r.WakeLatencyP50Us, r.WakeLatencyP99Us, r.WakeLatencyMaxUs) = WakeLatency(3000, ct);
        }, ct).ConfigureAwait(false);

        progress?.Report(("Done", 1));
        Save(r);
        Log.Info($"Benchmark '{label}': single {r.CpuSingleScore:0}, multi {r.CpuMultiScore:0}, mem {r.MemoryCopyGBs:0.0} GB/s, disk {r.DiskReadMBs:0}/{r.DiskWriteMBs:0} MB/s, wake p99 {r.WakeLatencyP99Us:0} µs");
        return r;
    }

    private static double Median(int runs, Func<double> f)
    {
        var values = Enumerable.Range(0, runs).Select(_ => f()).OrderBy(v => v).ToList();
        return values[values.Count / 2];
    }

    /// <summary>Fixed mixed integer/floating-point kernel. Score = millions of iterations per second per test.</summary>
    private static double CpuScore(int threads, TimeSpan duration, CancellationToken ct)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, t =>
        {
            ulong x = 0x9E3779B97F4A7C15UL ^ (ulong)t;
            double acc = 1.0;
            long iterations = 0;
            while (sw.Elapsed < duration)
            {
                for (var i = 0; i < 100_000; i++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    acc = acc * 1.0000001 + (x & 0xFF) * 1e-9;
                }
                iterations += 100_000;
            }
            if (acc < 0) Console.Write(""); // keep the loop observable to the JIT
            Interlocked.Add(ref total, iterations);
        });
        return total / sw.Elapsed.TotalSeconds / 1e6;
    }

    private static double MemoryCopy(TimeSpan duration)
    {
        const int size = 256 * 1024 * 1024;
        var src = GC.AllocateUninitializedArray<byte>(size);
        var dst = GC.AllocateUninitializedArray<byte>(size);
        Random.Shared.NextBytes(src.AsSpan(0, 4096));
        src.AsSpan().Fill(0x5A);
        long copied = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            src.AsSpan().CopyTo(dst);
            copied += size;
        }
        return copied / sw.Elapsed.TotalSeconds / 1e9;
    }

    private static unsafe (double WriteMBs, double ReadMBs) Disk(CancellationToken ct)
    {
        const int chunk = 4 * 1024 * 1024;
        const long total = 512L * 1024 * 1024;
        var dir = Path.GetTempPath();
        var drive = new DriveInfo(Path.GetPathRoot(dir)!);
        if (drive.AvailableFreeSpace < 4L * 1024 * 1024 * 1024) throw new IOException("less than 4 GB free on the temp drive");

        var path = Path.Combine(dir, $"wu-bench-{Guid.NewGuid():N}.tmp");
        var buffer = (byte*)NativeMemory.AlignedAlloc(chunk, 4096);
        try
        {
            var span = new Span<byte>(buffer, chunk);
            Random.Shared.NextBytes(span); // incompressible data
            const FileOptions noBuffering = (FileOptions)0x20000000;

            double write, read;
            using (var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | noBuffering, total))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < total; off += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, span, off);
                }
                write = total / sw.Elapsed.TotalSeconds / 1e6;
            }
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, noBuffering | FileOptions.SequentialScan))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < total; off += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, span, off);
                }
                read = total / sw.Elapsed.TotalSeconds / 1e6;
            }
            return (write, read);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>Signal-to-wake latency of a blocked thread using a kernel event, sampled thousands of times.</summary>
    private static (double P50, double P99, double Max) WakeLatency(int samples, CancellationToken ct)
    {
        using var signal = new AutoResetEvent(false);
        using var done = new AutoResetEvent(false);
        long signalledAt = 0;
        var results = new double[samples];
        var i = 0;
        var worker = new Thread(() =>
        {
            for (var n = 0; n < samples; n++)
            {
                signal.WaitOne();
                var now = Stopwatch.GetTimestamp();
                results[n] = (now - Interlocked.Read(ref signalledAt)) * 1e6 / Stopwatch.Frequency;
                done.Set();
            }
        }) { IsBackground = true, Priority = ThreadPriority.Normal };
        worker.Start();

        for (; i < samples; i++)
        {
            if (ct.IsCancellationRequested) break;
            Thread.Sleep(1); // let the worker block in the kernel before signalling
            Interlocked.Exchange(ref signalledAt, Stopwatch.GetTimestamp());
            signal.Set();
            done.WaitOne();
        }
        if (i < samples)
        {
            worker.Interrupt();
            ct.ThrowIfCancellationRequested();
        }
        var sorted = results.OrderBy(v => v).ToArray();
        return (sorted[sorted.Length / 2], sorted[(int)(sorted.Length * 0.99)], sorted[^1]);
    }

    public static (double DeltaPercent, string Verdict) Compare(BenchmarkMetric m, BenchmarkResult before, BenchmarkResult after)
    {
        var a = m.Value(before);
        var b = m.Value(after);
        if (a == 0) return (0, "—");
        var delta = (b - a) / a * 100;
        if (Math.Abs(delta) < m.NoisePercent) return (delta, "Within noise");
        var better = m.HigherIsBetter ? delta > 0 : delta < 0;
        return (delta, better ? "Improved" : "Worse");
    }
}
