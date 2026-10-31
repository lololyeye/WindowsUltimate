using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WindowsUltimate.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs built-in Windows tools (never downloaded scripts) and captures their output.</summary>
public static class ProcessRunner
{
    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>OEM code page used by classic console tools such as DISM, chkdsk and netsh.</summary>
    public static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        Action<string>? onLine = null,
        Encoding? encoding = null,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? OemEncoding,
            StandardErrorEncoding = encoding ?? OemEncoding,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.Start();

        async Task Pump(StreamReader reader, StringBuilder sink)
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                line = line.Replace("\0", string.Empty); // sfc pads its output with NULs
                lock (sink) sink.AppendLine(line);
                if (line.Trim().Length > 0) onLine?.Invoke(line);
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput, stdout), Pump(process.StandardError, stderr));
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts?.Token ?? CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await pumps.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static ProcessResult Run(string fileName, string arguments, TimeSpan? timeout = null) =>
        RunAsync(fileName, arguments, timeout: timeout ?? TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    /// <summary>Opens a URI, settings page or folder through the shell (ms-settings:, https:, paths).</summary>
    public static void ShellOpen(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, args ?? string.Empty) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"ShellOpen failed for {target}", ex);
        }
    }

    public static void RevealInExplorer(string path)
    {
        if (File.Exists(path)) ShellOpen("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) ShellOpen("explorer.exe", $"\"{path}\"");
    }
}
