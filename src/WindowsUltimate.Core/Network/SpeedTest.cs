using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Network;

public sealed class SpeedTestProgress
{
    public string Phase { get; init; } = "";
    public double Mbps { get; init; }
    public double? PingMs { get; init; }
    public double Fraction { get; init; }
}

/// <summary>One saved measurement, so results before and after a change can be compared.</summary>
public sealed class NetworkMeasurement
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "speed";
    public string Adapter { get; set; } = "";
    public string Server { get; set; } = "";
    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }
    public double? PingMs { get; set; }
    public double? JitterMs { get; set; }
    public double? LossPercent { get; set; }
    public double? LoadedPingMs { get; set; }
    public long BytesUsed { get; set; }

    /// <summary>Extra latency while the line is saturated ("bufferbloat"). Large values mean lag spikes when someone downloads.</summary>
    public double? Bufferbloat => LoadedPingMs is { } l && PingMs is { } p ? Math.Max(0, l - p) : null;
}

public static class NetworkHistory
{
    private static string File => Path.Combine(AppPaths.Root, "network-history.json");

    public static List<NetworkMeasurement> Load() => JsonStore.Load<List<NetworkMeasurement>>(File) ?? [];

    public static void Add(NetworkMeasurement m)
    {
        var all = Load();
        all.Add(m);
        JsonStore.Save(File, all.OrderByDescending(x => x.Time).Take(60).OrderBy(x => x.Time).ToList());
    }

    public static void Clear() => JsonStore.Save(File, new List<NetworkMeasurement>());
}

/// <summary>
/// Throughput and latency test against Cloudflare's public speed-test endpoints (speed.cloudflare.com), run only when the
/// user asks. Several parallel streams for a fixed time, the first second discarded (TCP slow start), latency sampled
/// idle and under load. Data use is capped.
/// </summary>
public static class SpeedTest
{
    public const string Host = "speed.cloudflare.com";
    public const long MaxDownloadBytes = 250_000_000, MaxUploadBytes = 100_000_000;
    private static readonly TimeSpan PhaseLength = TimeSpan.FromSeconds(8), Warmup = TimeSpan.FromSeconds(1);
    private const int Streams = 4;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = Streams + 2 })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsUltimate-SpeedTest/1.0");
        return c;
    }

    public static async Task<NetworkMeasurement> RunAsync(string adapter, IProgress<SpeedTestProgress>? progress = null, CancellationToken ct = default)
    {
        var m = new NetworkMeasurement { Kind = "speed", Adapter = adapter };

        // 1 · idle latency
        var idle = await NetworkDiagnostics.PingAsync("1.1.1.1", "idle", 20, TimeSpan.FromMilliseconds(100), null, ct).ConfigureAwait(false);
        m.PingMs = idle.Average;
        m.JitterMs = idle.Jitter;
        m.LossPercent = idle.LossPercent;
        progress?.Report(new SpeedTestProgress { Phase = "ping", PingMs = idle.Average, Fraction = 0.1 });

        // 2 · download, 3 · upload — each with latency sampled under load
        var loaded = new List<double>();
        var (down, downBytes, colo) = await PhaseAsync(true, loaded, 0.1, progress, ct).ConfigureAwait(false);
        var (up, upBytes, _) = await PhaseAsync(false, loaded, 0.55, progress, ct).ConfigureAwait(false);
        m.DownloadMbps = down;
        m.UploadMbps = up;
        m.LoadedPingMs = loaded.Count > 0 ? loaded.OrderBy(x => x).ElementAt(loaded.Count / 2) : null;
        m.BytesUsed = downBytes + upBytes;
        m.Server = colo.Length > 0 ? $"Cloudflare {colo}" : "Cloudflare";
        progress?.Report(new SpeedTestProgress { Phase = "done", Fraction = 1 });
        Log.Info($"Speed test: down {down:0.0} Mbps, up {up:0.0} Mbps, ping {m.PingMs:0.0} ms, loaded {m.LoadedPingMs:0.0} ms, {m.BytesUsed / 1e6:0} MB");
        return m;
    }

    private static async Task<(double Mbps, long Bytes, string Colo)> PhaseAsync(bool download, List<double> loaded, double startFraction,
        IProgress<SpeedTestProgress>? progress, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;
        long bytes = 0;
        var colo = "";
        var cap = download ? MaxDownloadBytes : MaxUploadBytes;
        var sw = Stopwatch.StartNew();

        async Task DownloadStream()
        {
            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested && Interlocked.Read(ref bytes) < cap)
            {
                using var resp = await Http.GetAsync($"https://{Host}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                if (colo.Length == 0 && resp.Headers.TryGetValues("cf-ray", out var ray))
                    colo = ray.FirstOrDefault()?.Split('-').LastOrDefault() ?? "";
                await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int n;
                while ((n = await s.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    if (Interlocked.Add(ref bytes, n) >= cap) break;
            }
        }

        async Task UploadStream()
        {
            while (!ct.IsCancellationRequested && Interlocked.Read(ref bytes) < cap)
            {
                using var content = new CountingContent(2_000_000, n => Interlocked.Add(ref bytes, n));
                using var resp = await Http.PostAsync($"https://{Host}/__up", content, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
            }
        }

        var workers = Enumerable.Range(0, Streams).Select(_ => download ? DownloadStream() : UploadStream()).ToList();
        long warmBytes = -1;
        var warmTime = TimeSpan.Zero;
        using var ping = new Ping();
        while (sw.Elapsed < PhaseLength && Interlocked.Read(ref bytes) < cap && workers.Any(w => !w.IsCompleted))
        {
            await Task.Delay(250, outer).ConfigureAwait(false);
            if (warmBytes < 0 && sw.Elapsed >= Warmup) { warmBytes = Interlocked.Read(ref bytes); warmTime = sw.Elapsed; }
            double? rtt = null;
            try
            {
                var r = await ping.SendPingAsync(IPAddress.Parse("1.1.1.1"), 1000).ConfigureAwait(false);
                if (r.Status == IPStatus.Success) { rtt = r.RoundtripTime; loaded.Add(r.RoundtripTime); }
            }
            catch (PingException) { }
            var now = Rate(Interlocked.Read(ref bytes) - Math.Max(0, warmBytes), sw.Elapsed - (warmBytes < 0 ? TimeSpan.Zero : warmTime));
            progress?.Report(new SpeedTestProgress
            {
                Phase = download ? "download" : "upload", Mbps = now, PingMs = rtt,
                Fraction = startFraction + 0.45 * Math.Min(1, sw.Elapsed / PhaseLength),
            });
        }
        var total = Interlocked.Read(ref bytes);
        var elapsed = sw.Elapsed;
        cts.Cancel();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested) { }
        catch (HttpRequestException) when (total > 0) { /* a stream cut short at the end of the phase */ }
        outer.ThrowIfCancellationRequested();
        if (total == 0) throw new HttpRequestException(L.T("The speed-test server could not be reached."));
        var mbps = warmBytes >= 0 ? Rate(total - warmBytes, elapsed - warmTime) : Rate(total, elapsed);
        return (mbps, total, colo);
    }

    private static double Rate(long bytes, TimeSpan time) => time.TotalSeconds <= 0.05 ? 0 : bytes * 8 / time.TotalSeconds / 1e6;

    /// <summary>Request body that reports how many bytes have been handed to the socket.</summary>
    private sealed class CountingContent(int length, Action<int> written) : HttpContent
    {
        private static readonly byte[] Chunk = CreateChunk();

        private static byte[] CreateChunk()
        {
            var b = new byte[64 * 1024];
            Random.Shared.NextBytes(b); // incompressible
            return b;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var left = length;
            while (left > 0)
            {
                var n = Math.Min(left, Chunk.Length);
                await stream.WriteAsync(Chunk.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                written(n);
                left -= n;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long l)
        {
            l = length;
            return true;
        }
    }
}
