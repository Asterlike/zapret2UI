using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
    // Signalled when the running combo's winws2 reports WinDivert is attached ("…capture is started"),
    // so probing starts the moment the engine is ready instead of after a fixed delay. Recreated per
    // StartEngine; each process's handlers capture their own instance.
    private TaskCompletionSource? _ready;

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
        // Актуальная YouTube-связка сообщества (июль 2026): fake с dupsid-fooling + богатый
        // multidisorder по маркерам SNI. Вербы/маркеры сверены с движком (fake tls_mod=…,dupsid — ok,
        // маркеры sniext/host/midsld/endhost — resolve_pos). Голый seqovl тут не нужен (на multidisorder
        // он «позиционный» и самоотменяется при seqovl≥первой pos).
        new("YT актуальный: fake dupsid + дизордер SNI", new[] {
            "--lua-desync=fake:blob=tls_google:tls_mod=rnd,dupsid:repeats=6",
            "--lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1" }),
        new("Фейк md5 + сплит", new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=6",
                                        "--lua-desync=multisplit:pos=1,midsld" }),
        new("Фейк ts + дизордер", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                          "--lua-desync=multidisorder:pos=1,midsld" }),
        new("seqovl-перекрытие", new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=681:seqovl_pattern=tls_google:optional" }),
        new("Фейк autottl + сплит", new[] { "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
                                            "--lua-desync=multisplit:pos=1,midsld" }),
        // fakeddisorder: split at the SNI midpoint (pos=1 is FORBIDDEN — the engine logs "cannot split"
        // and does nothing), badseq fooling on the FAKE segments only (tcp_ack=-66000 needs tcp_ts_up),
        // real parts stay clean. NB: fake data is `pattern=`, NOT `blob=` (blob= would replace the real
        // ClientHello). Reverse-order split → size-adaptive (marker pos) → gateway-friendly.
        new("fakeddisorder badseq midsld", new[] { "--lua-desync=fakeddisorder:pos=midsld:tcp_ack=-66000:tcp_ts_up:repeats=6" }),
        // tcpseg (new in nfqws2, absent from nfqws1): emit the whole ClientHello as one segment with a
        // SMALL size-independent seqovl overlay (5 bytes ≈ a TLS record header), then drop the original.
        // Size-adaptive → gateway-friendly; the classic Flowseal small-seqovl idea as a single segment.
        new("tcpseg seqovl 5 + drop", new[] {
            "--lua-desync=tcpseg:pos=0,-1:seqovl=5:seqovl_pattern=tls_google",
            "--lua-desync=drop" }),
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
        // Отечественный фейк (тренд 2026: sonicdpi ozon.ru, Flowseal dbankcloud_ru). ТСПУ вайтлистит
        // домашний трафик → фейк «под vk/ozon» выживает там, где google-фейк режется. tls_vk = реальный
        // ClientHello vk.com из поставки движка; host= — просто строка-домен (blob не нужен).
        new("Отеч.: fake VK-CH + дизордер", new[] {
            "--lua-desync=fake:blob=tls_vk:tcp_md5:ip_autottl=-2,3-20:repeats=6",
            "--lua-desync=multidisorder:pos=1,midsld" }),
        new("Отеч.: fake Sber-CH + дизордер", new[] {
            "--lua-desync=fake:blob=tls_sber:tcp_md5:ip_autottl=-2,3-20:repeats=6",
            "--lua-desync=multidisorder:pos=1,midsld" }),
        new("hostfakesplit vk.com", new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" }),
        new("hostfakesplit ozon.ru", new[] { "--lua-desync=hostfakesplit:host=ozon.ru:tcp_md5:repeats=6" }),
        // Больше отечественных SNI под gateway-safe hostfakesplit (для Discord — адаптируется к размеру
        // ClientHello гейтвея) + фейки под vk/gosuslugi с иным рычагом (перебираем «все варианты»).
        new("hostfakesplit sberbank.ru", new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:tcp_ts=-1000:tcp_md5:repeats=4" }),
        new("hostfakesplit gosuslugi.ru", new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:tcp_ts=-1000:tcp_md5:repeats=4" }),
        // Усиленный hostfakesplit (новые параметры nfqws2, есть в движке): midhost=midsld — доп. разрез
        // ВНУТРИ имени; disorder_after — хвост после имени двумя сегментами в обратном порядке. Домен-
        // приманка vk (ТСПУ вайтлистит). Gateway-friendly (hostfakesplit по маркеру SNI).
        new("hostfakesplit vk + midhost + disorder", new[] {
            "--lua-desync=hostfakesplit:host=vk.com:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
        new("Отеч.: fake VK-CH ts + сплит", new[] {
            "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6",
            "--lua-desync=multisplit:pos=1,midsld" }),
        new("Отеч.: fake Gos-CH + дизордер", new[] {
            "--lua-desync=fake:blob=tls_gos:tcp_md5:ip_autottl=-2,3-20:repeats=6",
            "--lua-desync=multidisorder:pos=1,midsld" }),
        // Flowseal ALT10: ЧИСТЫЙ двойной fake (google + отечественный vk) с ts-fooling, без сплита. У
        // сообщества «пускает вообще всё» (гейтвей+медиа+голос), когда сплиты дают только логин. Gateway-
        // friendly (fake:tcp_ts прайм). Голос комбо всё равно ставит через отеч. QUIC при сборке пресета.
        new("Flowseal ALT10: двойной fake + ts", new[] {
            "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
            "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" }),
        // badseq (дефолт sonicdpi) + ip_id=zero (Flowseal): фейк с «плохим» seq + обнулённый IP-ID сплита.
        new("badseq + ip_id=zero сплит", new[] {
            "--lua-desync=fake:blob=tls_google:tcp_seq=-10000:ip_autottl=-2,3-20:repeats=6",
            "--lua-desync=multisplit:pos=1,midsld:ip_id=zero" }),
        // (убран "Фейк badsum + сплит": фейк с битой L4-контрольной суммой за домашним NAT дропается
        //  ДО DPI и не отравляет его; надёжный эквивалент — "Фейк md5 + сплит" выше, md5 NAT-безопасен.)
        new("Фейк ts + fakedsplit", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                            "--lua-desync=fakedsplit:tcp_ts=-1000" }),
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
        new("ALT11/12: fake ts → seqovl 681", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=8",
                                                      "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
        new("ALT: fake ts → seqovl 664", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=8",
                                                 "--lua-desync=multisplit:pos=1,midsld:seqovl=664:seqovl_pattern=tls_google:optional" }),
    };

    /// <summary>
    /// Generate a personal preset in TWO passes:
    ///   1) score every candidate bundle for Discord and for YouTube separately;
    ///   2) take the strongest few per service and ACTUALLY TEST the assembled combos together,
    ///      keeping the one that works best for BOTH at once.
    /// Pass 2 is the fix for "each strategy works alone but no combination works fully": the old code
    /// shipped an untested best-Discord + best-YouTube guess, which could underperform its parts when
    /// the two bundles interfere inside one winws2 instance. Now the assembled artifact is validated.
    /// The caller MUST stop the main engine first.
    /// </summary>
    /// <summary>Result of a generation run: the assembled preset plus the real per-host results of that
    /// FINAL combo (used to render the visible per-service «РАБОТАЕТ/НЕ РАБОТАЕТ» verdict).</summary>
    public sealed record GenResult(Preset Preset, IReadOnlyList<AutoHostResult> FinalRows);

    public async Task<GenResult?> GenerateAsync(
        IReadOnlyList<string> discordHosts, IReadOnlyList<string> youtubeHosts,
        bool gameFilter, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.WinwsExe))
            throw new FileNotFoundException("Движок не установлен.");

        var allHosts = discordHosts.Concat(youtubeHosts).Distinct().ToList();

        // Raw hit count → the honest number shown in the popup. Weighted score → ranking only, where a
        // completed HTTPS GET (the page loads) counts far more than a bare handshake, so a bundle that
        // "handshakes OK but resets" can't win.
        static int Hits(AutoHostResult r) => (r.Tls12 == DiagStatus.Ok ? 1 : 0)
            + (r.Tls13 == DiagStatus.Ok ? 1 : 0) + (r.Https == DiagStatus.Ok ? 1 : 0);
        // Weighted score used only for ranking: a completed HTTPS GET (the page loads) counts far more
        // than a bare handshake, so a bundle that "handshakes OK but resets" can't win.
        static int Weight(AutoHostResult r) => (r.Tls12 == DiagStatus.Ok ? 1 : 0)
            + (r.Tls13 == DiagStatus.Ok ? 1 : 0) + (r.Https == DiagStatus.Ok ? 3 : 0);

        // ---- Pass 1: score each candidate bundle per service ----
        var scored = new List<(GenBundle bundle, int dScore, int yScore)>();
        GenBundle? perfect = null; // a single bundle that clears everything → short-circuits the assembly search
        for (int i = 0; i < Candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cand = Candidates[i];
            CandidateStarted?.Invoke(cand.Name);
            Status?.Invoke($"[{i + 1}/{Candidates.Count}] Генерирую и тестирую: {cand.Name}…");

            var args = PresetService.BuildComboArgs(cand.Tls, cand.Tls, cand.Tls, voiceDesync: VoiceDesync);
            var rows = await ProbeStrategyAsync(args, allHosts, gameFilter, reportHosts: true, ct);

            int total = allHosts.Count * 3;
            int okAll = rows.Sum(Hits);
            // Carry the candidate as a full, saveable combo so the popup lists it under "прошли проверку"
            // and the user can save ANY of them as a preset (not just the final assembled best).
            ScoreReady?.Invoke(new AutoScore(cand.Name, okAll, total - okAll, total,
                new ComboStrategy(cand.Name, args), rows));

            int dScore = rows.Where(r => discordHosts.Contains(r.Host)).Sum(Weight);
            int yScore = rows.Where(r => youtubeHosts.Contains(r.Host)).Sum(Weight);
            scored.Add((cand, dScore, yScore));

            // Early exit (mirrors the auto-selector's Fail==0 stop): a bundle that clears EVERY goal host
            // (TLS 1.2/1.3 + HTTP) for BOTH services AND is gateway-friendly is already the whole answer —
            // assembling two different bundles can't beat one that covers everything. Stop scanning the pool.
            // Kept strict (okAll == total) so we never ship a merely-good bundle early. Gateway-friendliness
            // is required because the probe can't see Discord's native gateway ClientHello (see Pass 2).
            if (okAll == total && IsGatewayFriendly(cand.Tls)) { perfect = cand; break; }
        }
        if (scored.Count == 0) return null;

        // ---- Pass 2: assemble the strongest few per service and test the combos TOGETHER ----
        // Discord slot: the native client opens its GATEWAY/voice/media with a DIFFERENT-sized ClientHello
        // than the login web view, so a strategy that only reaches login stalls the gateway ("logs in but
        // won't connect"). The reliable levers (confirmed by the user's working presets) are a fake:tcp_ts
        // PRIME before any split (Flowseal ALT10/ALT11) or hostfakesplit — see IsGatewayFriendly. So the
        // Discord shortlist is built from gateway-friendly bundles whenever any showed a Discord signal in
        // Pass 1; unprimed big-seqovl bundles are used only as a last resort.
        var friendlyD = scored.Where(s => s.dScore > 0 && IsGatewayFriendly(s.bundle.Tls))
                              .OrderByDescending(s => s.dScore).Take(ShortlistK).Select(s => s.bundle).ToList();
        // If Pass 1 found a bundle that covers everything, both slots are that bundle — the loop below then
        // runs a single confirming assembly test instead of the full shortlist grid.
        var topD = perfect is not null
            ? new List<GenBundle> { perfect }
            : friendlyD.Count > 0
                ? friendlyD
                : scored.OrderByDescending(s => s.dScore).Take(ShortlistK).Select(s => s.bundle).ToList();
        var topY = perfect is not null
            ? new List<GenBundle> { perfect }
            : scored.OrderByDescending(s => s.yScore).Take(ShortlistK).Select(s => s.bundle).ToList();

        GenBundle finalD = topD[0];
        GenBundle finalY = topY[0];
        double bestMin = -1, bestSum = -1, bestRD = 0, bestRY = 0;
        int maxD = Math.Max(1, discordHosts.Count) * 5; // weight per host = 1 + 1 + 3
        int maxY = Math.Max(1, youtubeHosts.Count) * 5;

        int attempts = 0;
        bool done = false;
        foreach (var d in topD)
        {
            foreach (var y in topY)
            {
                if (attempts >= MaxAssemblyTests) { done = true; break; }
                attempts++;
                ct.ThrowIfCancellationRequested();
                Status?.Invoke($"Проверяю сборку [{attempts}]: Discord «{d.Name}» + YouTube «{y.Name}»…");

                var asmArgs = PresetService.BuildComboArgs(d.Tls, y.Tls, d.Tls);
                var rows = await ProbeStrategyAsync(asmArgs, allHosts, gameFilter, reportHosts: false, ct);
                double rD = rows.Where(r => discordHosts.Contains(r.Host)).Sum(Weight) / (double)maxD;
                double rY = rows.Where(r => youtubeHosts.Contains(r.Host)).Sum(Weight) / (double)maxY;
                double jMin = Math.Min(rD, rY), jSum = rD + rY;

                // Prefer the combo that lifts the WEAKER service most (min), then overall (sum) — i.e.
                // "works for everything", not "great for one and broken for the other". The Discord slot
                // is already gateway-safe (curated above), so no fragile-vs-safe tiebreak is needed here.
                if (jMin > bestMin || (jMin == bestMin && jSum > bestSum))
                {
                    bestMin = jMin; bestSum = jSum; bestRD = rD; bestRY = rY;
                    finalD = d; finalY = y;
                }
                if (jMin >= 1.0) { done = true; break; }
            }
            if (done) break;
        }

        // Discord bundle also serves as the catch-all fallback (covers arbitrary SNIs best for this net).
        var finalArgs = PresetService.BuildComboArgs(finalD.Tls, finalY.Tls, finalD.Tls, voiceDesync: VoiceDesync);

        // Final validation pass: run the assembled combo once more and report the rows live, so the
        // popup's target panel + the per-service verdict reflect the actual artifact we're shipping.
        Status?.Invoke("Проверяю итоговую сборку…");
        CandidateStarted?.Invoke("Итоговая сборка");
        var finalRows = await ProbeStrategyAsync(finalArgs, allHosts, gameFilter, reportHosts: true, ct);

        string jointNote = bestMin < 0 ? ""
            : $" Проверено вместе: Discord {(int)Math.Round(bestRD * 100)}%, YouTube {(int)Math.Round(bestRY * 100)}%.";
        var preset = new Preset
        {
            Name = $"✨ Сгенерировано {DateTime.Now:dd.MM HH:mm}",
            Description = "Персональная стратегия под вашего провайдера, собрана и ПРОВЕРЕНА как единое " +
                          $"комбо: Discord ← «{finalD.Name}», YouTube ← «{finalY.Name}».{jointNote}",
            Args = new List<string>(finalArgs),
            IsBuiltIn = false,
            IsGenerated = true,
        };
        return new GenResult(preset, finalRows);
    }

    // Pass-2 budget: try up to MaxAssemblyTests assemblies from the top-ShortlistK bundles per service.
    private const int ShortlistK = 3;
    private const int MaxAssemblyTests = 6;

    // Voice can't be probed (UDP), so it's a fixed choice: use the domestic QUIC-blob voice (Flowseal
    // ALT10/11) rather than the google anti-drop default — it's what fixes 5k-ping / «не слышно» when the
    // default voice doesn't. Applied to every saved/assembled generated preset.
    private static readonly string[] VoiceDesync = { "--lua-desync=fake:blob=quic_vk:repeats=6" };

    /// <summary>
    /// A Discord desync bundle is "gateway-friendly" when it reliably reaches the native client's
    /// gateway/voice/media, not just the login web view. The user's confirmed-working presets show the
    /// levers: a <c>fake:…tcp_ts</c> PRIME (Flowseal ALT10/ALT11) or <c>hostfakesplit</c>. A big fixed
    /// <c>seqovl=&lt;bytes&gt;</c> overlap (or bare large <c>pos=</c> offset) is fine WHEN preceded by such
    /// a prime (that's ALT11, seqovl=681 — and it works); it's the UNPRIMED big-seqovl bundle that only
    /// reaches login and stalls the differently-sized gateway ClientHello. Marker positions
    /// (sniext/midsld/sld±n, small ints) and tiny seqovl are size-independent → friendly on their own.
    /// </summary>
    private static bool IsGatewayFriendly(IReadOnlyList<string> tls)
    {
        // A hostfakesplit or a fake:…tcp_ts prime carries even a big-seqovl split to the gateway (ALT10/11).
        bool primed = tls.Any(a =>
            a.Contains("hostfakesplit") ||
            (a.Contains("--lua-desync=fake:") && a.Contains("tcp_ts")));
        if (primed) return true;

        foreach (var a in tls)
        {
            // Unprimed: a LARGE fixed byte-count overlap is tuned to one ClientHello size and overshoots a
            // smaller (native gateway) one; a tiny seqovl=1 is a minimal, size-independent overlap → ok.
            var s = Regex.Match(a, @"seqovl=(\d+)");
            if (s.Success && int.TryParse(s.Groups[1].Value, out int ov) && ov >= 100) return false;
            // Fixed absolute byte split positions (e.g. pos=88,176,…) — marker positions (sniext/midsld/
            // sld±n, small ints) adapt; large bare integers don't.
            var m = Regex.Match(a, @"pos=([A-Za-z0-9,+\-]+)");
            if (m.Success && m.Groups[1].Value.Split(',')
                    .Any(t => int.TryParse(t, out int v) && v >= 40)) return false;
        }
        return true;
    }

    /// <summary>Start the engine with a combo, probe every host (TLS 1.2 / 1.3 + a browser-like HTTP/2 reach) in
    /// parallel, then stop. <paramref name="reportHosts"/> gates the live <see cref="HostProbed"/>
    /// events so the silent assembly-validation pass doesn't overwrite the per-bundle rows in the popup.
    /// Probing is bounded by the slowest host, not the sum, so an "everything times out" combo is ~5s.</summary>
    private async Task<List<AutoHostResult>> ProbeStrategyAsync(
        List<string> comboArgs, IReadOnlyList<string> hosts, bool gameFilter, bool reportHosts, CancellationToken ct)
    {
        StartEngine(comboArgs, gameFilter);
        try
        {
            await WaitEngineReadyAsync(ct); // wait until WinDivert is actually attached (capped at the old 1500ms)
            bool up = _proc is { HasExited: false };
            using var gate = new SemaphoreSlim(8);
            var probes = hosts.Select(async host =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var r = up
                        ? await NetProbe.ProbeHostAsync(host, ct).ConfigureAwait(false)
                        : new AutoHostResult(host, DiagStatus.Fail, DiagStatus.Fail, DiagStatus.Fail);
                    if (reportHosts) HostProbed?.Invoke(host, r.Tls12, r.Tls13, r.Https);
                    return r;
                }
                finally { gate.Release(); }
            });
            return (await Task.WhenAll(probes).ConfigureAwait(false)).ToList();
        }
        finally
        {
            StopEngine();
            await Task.Delay(300, CancellationToken.None);
        }
    }

    private void StartEngine(List<string> comboArgs, bool gameFilter)
    {
        StopEngine(); // defensive: never leave the previous candidate's winws2 alive (two engines
                      // would fight over WinDivert and poison every subsequent probe).
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
        foreach (var a in EngineService.BuildArguments(preset, null, gameFilter, forLaunch: true)) psi.ArgumentList.Add(a);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready = ready;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Signal readiness on the WinDivert "capture is started" line; also unblock on early exit
        // (bad args) so a combo whose engine dies instantly fails fast instead of waiting the cap.
        p.OutputDataReceived += (_, e) => { if (IsWinDivertReady(e.Data)) ready.TrySetResult(); };
        p.ErrorDataReceived += (_, e) => { if (IsWinDivertReady(e.Data)) ready.TrySetResult(); };
        p.Exited += (_, _) => ready.TrySetResult();
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

    /// <summary>True for a winws2 log line that means WinDivert is attached and capturing.</summary>
    private static bool IsWinDivertReady(string? line) =>
        line is not null &&
        (line.Contains("capture is started", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("windivert initialized", StringComparison.OrdinalIgnoreCase));

    /// <summary>Wait until the engine reports WinDivert is attached, capped at 1500 ms (the old fixed
    /// delay) so an absent/changed log line falls back to today's behaviour. A short settle follows so
    /// the filter is live before probing.</summary>
    private async Task WaitEngineReadyAsync(CancellationToken ct)
    {
        var ready = _ready;
        if (ready is null)
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1500); // cap = the old fixed delay → exact fallback when no ready line appears
        try
        {
            await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            // Ready line seen: let WinDivert go live before probing (skipped on timeout — already waited the cap).
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* no ready line within cap → behave like the old fixed 1500ms delay */ }
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
