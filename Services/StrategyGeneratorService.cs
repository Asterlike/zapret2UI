using System.Diagnostics;
using System.IO;
using System.Security.Authentication;
using System.Text;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Personal-strategy GENERATOR (distinct from <see cref="AutoSelectService"/>, which picks a whole
/// catalog entry that already works for everything). The generator composes a tailored strategy:
/// it tests a curated grid of TLS-desync bundles, scores each PER SERVICE (Discord vs YouTube), and
/// assembles the best Discord bundle + best YouTube bundle into one combo via
/// <see cref="PresetService.BuildComboArgs"/>. The caller MUST stop the main engine first.
/// </summary>
public sealed class StrategyGeneratorService : IDisposable
{
    public event Action<string>? Status;
    /// <summary>Fired with the candidate name right before it starts being probed.</summary>
    public event Action<string>? CandidateStarted;
    /// <summary>Fired after each goal host is probed (host, TLS1.2, TLS1.3).</summary>
    public event Action<string, DiagStatus, DiagStatus, DiagStatus>? HostProbed;
    /// <summary>Fired with the candidate's overall score (for the live popup).</summary>
    public event Action<AutoScore>? ScoreReady;

    private Process? _proc;

    public void Dispose() => StopEngine();

    /// <summary>One candidate building block: a named TLS-desync bundle.</summary>
    public sealed record GenBundle(string Name, string[] Tls);

    /// <summary>Curated grid of TLS-desync bundles to compose from (stock verbs + tls_google blob).</summary>
    public static IReadOnlyList<GenBundle> Candidates { get; } = new GenBundle[]
    {
        new("Сплит по имени", new[] { "--lua-desync=multisplit:pos=1,sniext,midsld,endhost" }),
        new("Дизордер по имени", new[] { "--lua-desync=multidisorder:pos=1,sniext,midsld,endhost" }),
        new("Дизордер 88×5", new[] { "--lua-desync=multidisorder:pos=88,176,264,352,440" }),
        // Multi-position multidisorder around host/sld/sni markers — on reassembling DPIs (drop fakes,
        // beaten by reordering) this scored best in field tests. Was only in autoselect; now in generation.
        new("Дизордер по host/sld/sni", new[] { "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2" }),
        new("Фейк md5 + сплит", new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=6",
                                        "--lua-desync=multisplit:pos=1,midsld" }),
        new("Фейк ts + дизордер", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=6",
                                          "--lua-desync=multidisorder:pos=1,midsld" }),
        new("seqovl-перекрытие", new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=681:seqovl_pattern=tls_google:optional" }),
        new("Фейк autottl + сплит", new[] { "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
                                            "--lua-desync=multisplit:pos=1,midsld" }),
        new("fakeddisorder md5", new[] { "--lua-desync=fakeddisorder:pos=1:blob=tls_google:tcp_md5:repeats=6" }),
        new("Фейк md5/seq + дизордер", new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
                                               "--lua-desync=multidisorder:pos=1,midsld" }),
        // hostfakesplit = the unique lever: splits the ClientHello around the SNI and injects a FAKE
        // host segment (www.google.com), so the DPI sees a google connection and can't reassemble the
        // real Discord SNI. On DPIs that reassemble fragments (where plain split/seqovl fail) this is
        // often the ONLY thing that passes login — so we give the pool several variants of it.
        new("hostfakesplit", new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" }),
        new("hostfakesplit md5 ×6", new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_md5:repeats=6" }),
        new("hostfakesplit + wssize", new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4",
                                              "--lua-desync=wssize:wsize=1:scale=6" }),
        new("hostfakesplit MS-host", new[] { "--lua-desync=hostfakesplit:host=www.microsoft.com:tcp_ts=-1000:tcp_md5:repeats=4" }),
        new("Фейк badsum + сплит", new[] { "--lua-desync=fake:blob=tls_google:badsum:repeats=6",
                                           "--lua-desync=multisplit:pos=1,midsld" }),
        new("Фейк ts + fakedsplit", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=6",
                                            "--lua-desync=fakedsplit:tcp_ts=1000" }),
        // Окно (wssize) — заставляет сервер дробить ответ; ключевой рычаг под упрямый вход Discord
        // (в сообществе «помогает в 99% случаев»). Раньше был только в автоподборе — добавлен в генерацию.
        new("Окно wssize + seqovl", new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                                            "--lua-desync=wssize:wsize=1:scale=6" }),
        // Сильный «пробойник» входа: fake с badack(-66000)+ts_up (ts_up обязателен при tcp_ack) и
        // большой seqovl с реальным ClientHello как паттерном.
        new("Фейк ack66k ts_up + seqovl", new[] { "--lua-desync=fake:blob=tls_google:tcp_ack=-66000:tcp_ts_up:tls_mod=rnd:repeats=2",
                                                  "--lua-desync=multisplit:pos=2,midsld-2:seqovl=700:seqovl_pattern=tls_google:optional" }),
        // Flowseal ALT11/ALT12 («fooling ts»): fake (ts, repeats=8) ПЕРЕД multisplit с большим seqovl
        // и реальным google-ClientHello как паттерном. Стэкнутый комбо, которого в пуле не было.
        new("ALT11/12: fake ts → seqovl 681", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=8",
                                                      "--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google:optional" }),
        new("ALT: fake ts → seqovl 664", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=8",
                                                 "--lua-desync=multisplit:pos=1:seqovl=664:seqovl_pattern=tls_google:optional" }),
    };

    /// <summary>
    /// Generate a personal preset: test every candidate bundle against the Discord and YouTube hosts,
    /// keep the best bundle per service, and assemble them into one combo preset.
    /// </summary>
    public async Task<Preset?> GenerateAsync(
        IReadOnlyList<string> discordHosts, IReadOnlyList<string> youtubeHosts,
        bool gameFilter, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.WinwsExe))
            throw new FileNotFoundException("Движок не установлен.");

        var allHosts = discordHosts.Concat(youtubeHosts).Distinct().ToList();
        GenBundle? bestDiscord = null; int bestDiscordOk = -1;
        GenBundle? bestYoutube = null; int bestYoutubeOk = -1;

        for (int i = 0; i < Candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cand = Candidates[i];
            CandidateStarted?.Invoke(cand.Name);
            Status?.Invoke($"[{i + 1}/{Candidates.Count}] Генерирую и тестирую: {cand.Name}…");

            var rows = new List<AutoHostResult>(allHosts.Count);

            var args = PresetService.BuildComboArgs(cand.Tls, cand.Tls, cand.Tls);
            StartEngine(args, gameFilter);
            try
            {
                await Task.Delay(1500, ct); // let WinDivert attach
                bool up = _proc is { HasExited: false };
                // Probe all hosts in parallel (both TLS versions concurrently) — bounded by the
                // slowest host, not the sum, so a "everything times out" candidate is ~5s not ~50s.
                using var gate = new SemaphoreSlim(8);
                var probes = allHosts.Select(async host =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        DiagStatus t12 = DiagStatus.Fail, t13 = DiagStatus.Fail, https = DiagStatus.Fail;
                        if (up)
                        {
                            var p12 = NetProbe.TlsAsync(host, SslProtocols.Tls12, ct);
                            var p13 = NetProbe.TlsAsync(host, SslProtocols.Tls13, ct);
                            var ph = NetProbe.HttpsAsync(host, ct);
                            await Task.WhenAll(p12, p13, ph).ConfigureAwait(false);
                            t12 = p12.Result; t13 = p13.Result; https = ph.Result;
                        }
                        HostProbed?.Invoke(host, t12, t13, https);
                        return new AutoHostResult(host, t12, t13, https);
                    }
                    finally { gate.Release(); }
                });
                rows = (await Task.WhenAll(probes).ConfigureAwait(false)).ToList();
            }
            finally
            {
                StopEngine();
                await Task.Delay(300, CancellationToken.None);
            }

            // 3 signals per host now: TLS 1.2 + TLS 1.3 + full HTTPS GET (the request must complete,
            // not just the handshake — so a candidate that connects but resets ranks below one that loads).
            static int Hits(AutoHostResult r) => (r.Tls12 == DiagStatus.Ok ? 1 : 0)
                + (r.Tls13 == DiagStatus.Ok ? 1 : 0) + (r.Https == DiagStatus.Ok ? 1 : 0);
            int discordOk = rows.Where(r => discordHosts.Contains(r.Host)).Sum(Hits);
            int youtubeOk = rows.Where(r => youtubeHosts.Contains(r.Host)).Sum(Hits);
            int total = allHosts.Count * 3;
            int okAll = rows.Sum(Hits);
            // Carry the candidate as a full, saveable combo so the popup lists it under "прошли проверку"
            // and the user can save ANY of them as a preset (not just the final assembled best).
            var candStrategy = new ComboStrategy(cand.Name, args);
            ScoreReady?.Invoke(new AutoScore(cand.Name, okAll, total - okAll, total, candStrategy, rows));

            if (discordOk > bestDiscordOk) { bestDiscordOk = discordOk; bestDiscord = cand; }
            if (youtubeOk > bestYoutubeOk) { bestYoutubeOk = youtubeOk; bestYoutube = cand; }
        }

        if (bestDiscord is null || bestYoutube is null) return null;

        // Discord bundle also serves as the catch-all fallback (it covers arbitrary SNIs best for this net).
        var finalArgs = PresetService.BuildComboArgs(bestDiscord.Tls, bestYoutube.Tls, bestDiscord.Tls);
        return new Preset
        {
            Name = $"✨ Сгенерировано {DateTime.Now:dd.MM HH:mm}",
            Description = $"Персональная стратегия, собрана генератором под вашего провайдера: " +
                          $"Discord ← «{bestDiscord.Name}», YouTube ← «{bestYoutube.Name}». " +
                          "Каждый сервис оптимизирован отдельно и собран в одно комбо.",
            Args = new List<string>(finalArgs),
            IsBuiltIn = false,
            IsGenerated = true,
        };
    }

    private void StartEngine(List<string> comboArgs, bool gameFilter)
    {
        var preset = new Preset { Name = "generator", Args = comboArgs };
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.WinwsExe,
            WorkingDirectory = AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in EngineService.BuildArguments(preset, null, gameFilter)) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        _proc = p;
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            StopEngine();
            throw;
        }
    }

    private void StopEngine()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(4000);
            }
        }
        catch { }
        finally
        {
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }
    }
}
