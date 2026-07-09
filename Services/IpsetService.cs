using System.Diagnostics;
using System.IO;
using System.Text;

namespace Zapret2UI.Services;

/// <summary>
/// Builds an IP-set for IP-based bypass (the lever for hard IP-blocks where
/// domain/SNI matching is useless). It resolves the Discord domains with the
/// bundled <c>mdig.exe</c> and aggregates the addresses into CIDR subnets with
/// <c>ip2net.exe</c> — both userspace tools, no admin needed.
/// </summary>
public sealed class IpsetService
{
    public sealed record IpsetResult(string Path, int Subnets);

    /// <summary>Resolve <paramref name="domains"/> and write an aggregated ipset file. Returns the path + subnet count.</summary>
    public async Task<IpsetResult> BuildDiscordIpsetAsync(IEnumerable<string> domains, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.MdigExe) || !File.Exists(AppPaths.Ip2NetExe))
            throw new FileNotFoundException("mdig.exe / ip2net.exe не найдены — дождитесь загрузки движка.");

        var list = domains.Select(d => d.Trim())
                          .Where(d => d.Length > 0 && !d.StartsWith('#'))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
        if (list.Count == 0) throw new InvalidOperationException("Список доменов Discord пуст.");

        string ips = await ResolveAsync(list, ct);
        if (string.IsNullOrWhiteSpace(ips))
            throw new InvalidOperationException("Не удалось разрезолвить ни одного домена (DNS-блокировка?).");

        string subnets = await AggregateAsync(ips, ct);
        var lines = subnets.Replace("\r\n", "\n").Split('\n')
                           .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        Directory.CreateDirectory(AppPaths.ListsDir);
        await File.WriteAllTextAsync(AppPaths.IpsetDiscordFile, string.Join("\n", lines) + "\n", ct);
        return new IpsetResult(AppPaths.IpsetDiscordFile, lines.Count);
    }

    private static Task<string> ResolveAsync(IReadOnlyList<string> domains, CancellationToken ct) =>
        RunPipeAsync(AppPaths.MdigExe, "--family=4", string.Join("\n", domains) + "\n", ct);

    private static Task<string> AggregateAsync(string ips, CancellationToken ct) =>
        RunPipeAsync(AppPaths.Ip2NetExe, "-4", ips, ct);

    /// <summary>Run a tool feeding <paramref name="stdin"/> and returning its stdout.</summary>
    private static async Task<string> RunPipeAsync(string exe, string args, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.ASCII,
            StandardInputEncoding = Encoding.ASCII, // match stdout: domains/IPs are ASCII, no code-page mojibake
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        try
        {
            // Drain BOTH pipes concurrently. Reading only stdout while stderr fills its ~4 KB OS
            // buffer deadlocks (a chatty mdig/ip2net blocks writing stderr while we block on stdout).
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
            string outp = await outTask;
            string err = await errTask;
            await p.WaitForExitAsync(ct);
            // Surface a real tool failure (non-zero exit AND no usable output) instead of silently
            // writing an empty/partial ipset. Partial output (some domains resolved) is still returned.
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(outp))
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exe)} завершился с кодом {p.ExitCode}" +
                    (err.Trim().Length > 0 ? $": {err.Trim()}" : "."));
            return outp;
        }
        finally
        {
            // Disposing a Process doesn't terminate it: on cancellation/exception kill the child
            // (mdig/ip2net) so it can't linger after we've stopped reading its output.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
