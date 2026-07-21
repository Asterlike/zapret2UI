using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Picking a strategy: goal scope, the auto-selector and the per-service generator.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- auto-select scope -------------------------------------------------

    private AutoScope _scope = AutoScope.Both;
    public AutoScope SelectedScope
    {
        get => _scope;
        private set
        {
            if (SetField(ref _scope, value))
            {
                OnPropertyChanged(nameof(ScopeBoth));
                OnPropertyChanged(nameof(ScopeDiscord));
                OnPropertyChanged(nameof(ScopeYouTube));
                OnPropertyChanged(nameof(ScopeTitle));
                OnPropertyChanged(nameof(SimpleGoalHint));
            }
        }
    }

    public bool ScopeBoth { get => _scope == AutoScope.Both; set { if (value) SelectedScope = AutoScope.Both; } }
    public bool ScopeDiscord { get => _scope == AutoScope.Discord; set { if (value) SelectedScope = AutoScope.Discord; } }
    public bool ScopeYouTube { get => _scope == AutoScope.YouTube; set { if (value) SelectedScope = AutoScope.YouTube; } }
    public string ScopeTitle => SelectedScope.Title();

    public string SimpleGoalHint => SelectedScope switch
    {
        AutoScope.Discord => "Ищем стратегию под Discord.",
        AutoScope.YouTube => "Ищем стратегию под YouTube.",
        _ => "Ищем одну стратегию сразу под Discord и YouTube.",
    };

    // ---- auto-select (best strategy for the chosen scope) -----------------

    private bool _isAutoSelecting;
    public bool IsAutoSelecting
    {
        get => _isAutoSelecting;
        private set { if (SetField(ref _isAutoSelecting, value)) { RaiseCommandStates(); RaiseAutoRunState(); } }
    }

    /// <summary>True while EITHER the auto-selector or the generator is running — drives the review
    /// popup between its "running" and "done" states (the popup stays open when a run finishes).</summary>
    public bool IsAutoRunning => IsAutoSelecting || IsGenerating;

    public string AutoPopupTitle => IsAutoRunning
        ? (IsGenerating ? "Генерация стратегии" : "Подбор стратегии")
        : "Готово — выберите стратегию";

    public string AutoPopupSubtitle => IsAutoRunning
        ? "Слева — проверка целей текущей стратегией. Справа — стратегии, прошедшие проверку."
        : "Проверка завершена. Наведите на нужную и нажмите «Сохранить в пресеты» — окно не закроется само.";

    private void RaiseAutoRunState()
    {
        OnPropertyChanged(nameof(IsAutoRunning));
        OnPropertyChanged(nameof(AutoPopupTitle));
        OnPropertyChanged(nameof(AutoPopupSubtitle));
    }

    /// <summary>Measure the goal hosts' reachability WITHOUT any bypass (the engine is stopped at this
    /// point). Lets the final verdict distinguish "разблокировано обходом" from "и так открывалось",
    /// so the user sees what the strategy actually changed. Best-effort: failures leave it empty.</summary>
    private static async Task<Dictionary<string, bool>> MeasureBaselineAsync(IReadOnlyList<string> hosts, CancellationToken ct)
    {
        try
        {
            using var gate = new SemaphoreSlim(8);
            var rows = await Task.WhenAll(hosts.Select(async h =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { return await NetProbe.ProbeHostAsync(h, ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            }));
            return rows.ToDictionary(r => r.Host, r => r.Https == DiagStatus.Ok, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); }
    }

    private static bool IsDiscordHost(string h) =>
        h.Contains("discord", StringComparison.OrdinalIgnoreCase) ||
        h.Equals("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsYouTubeHost(string h) =>
        h.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
        h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        h.Contains("ytimg", StringComparison.OrdinalIgnoreCase) ||
        h.Contains("googlevideo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Build the visible per-service verdict from the CHOSEN strategy's real HTTP/2 results.</summary>
    private void BuildVerdicts(IReadOnlyList<AutoHostResult> rows)
    {
        Verdicts.Clear();
        AddVerdict("Discord", rows.Where(r => IsDiscordHost(r.Host)).ToList());
        AddVerdict("YouTube", rows.Where(r => IsYouTubeHost(r.Host)).ToList());
        AddVerdict("Ваши сайты", rows.Where(r => !IsDiscordHost(r.Host) && !IsYouTubeHost(r.Host)).ToList());
    }

    private void AddVerdict(string service, IReadOnlyList<AutoHostResult> rows)
    {
        if (rows.Count == 0) return;
        int total = rows.Count;
        int ok = rows.Count(r => r.Https == DiagStatus.Ok);       // real page-load signal
        int baseOk = rows.Count(r => _baseline.TryGetValue(r.Host, out var b) && b);

        var status = ok == total ? DiagStatus.Ok : ok > 0 ? DiagStatus.Timeout : DiagStatus.Fail;
        string label = ok == total ? "РАБОТАЕТ" : ok > 0 ? "ЧАСТИЧНО" : "НЕ РАБОТАЕТ";

        string note;
        if (ok == 0) note = "ни одна цель не открывается";
        else if (ok > baseOk) note = baseOk == 0 ? "разблокировано обходом" : "часть разблокирована обходом";
        else if (baseOk == total && _baseline.Count > 0) note = "открывается и без обхода";
        else note = "";

        Verdicts.Add(new ServiceVerdict
        {
            Service = service,
            StatusText = label,
            Status = status,
            Detail = $"{ok}/{total} целей грузятся" + (note.Length > 0 ? " · " + note : ""),
        });
    }

    private string _autoStatusText = "Выберите цель и нажмите «Подобрать лучшую» — найдём стратегию с наименьшим числом ошибок.";
    public string AutoStatusText { get => _autoStatusText; private set => SetField(ref _autoStatusText, value); }

    private void SetAutoStatus(string s) { AutoStatusText = s; SimpleStatus = s; }

    private double _autoProgress;
    public double AutoProgress { get => _autoProgress; private set => SetField(ref _autoProgress, value); }

    private string _autoProgressText = "";
    public string AutoProgressText { get => _autoProgressText; private set => SetField(ref _autoProgressText, value); }

    private string _currentCandidateName = "";
    public string CurrentCandidateName { get => _currentCandidateName; private set => SetField(ref _currentCandidateName, value); }

    // Index of the candidate currently being probed (set by CandidateStarted, used by ScoreReady).
    private int _autoCursor = -1;

    private void MarkCandidateRunning(string name)
    {
        CurrentCandidateName = name;
        // Reset the main-area target rows for this candidate (filled live by OnHostProbed).
        CheckTargets.Clear();
        foreach (var h in AutoTargets) CheckTargets.Add(new CheckTargetRow(h));

        for (int i = 0; i < AutoCandidates.Count; i++)
        {
            if (AutoCandidates[i].Name == name)
            {
                AutoCandidates[i].State = AutoCandidateState.Running;
                _autoCursor = i;
                return;
            }
        }
    }

    private void OnHostProbed(string host, DiagStatus tls12, DiagStatus tls13, DiagStatus https)
    {
        foreach (var t in CheckTargets)
        {
            if (t.Host == host) { t.Tls12 = tls12; t.Tls13 = tls13; t.Http = https; return; }
        }
    }

    private void ApplyCandidateScore(AutoScore score)
    {
        if (_autoCursor >= 0 && _autoCursor < AutoCandidates.Count)
            AutoCandidates[_autoCursor].Apply(score);

        // Sync the main-area rows from the final result (covers the engine-failed path
        // where OnHostProbed never fired).
        foreach (var h in score.HostList)
            foreach (var t in CheckTargets)
                if (t.Host == h.Host) { t.Tls12 = h.Tls12; t.Tls13 = h.Tls13; t.Http = h.Https; break; }

        // Accumulate strategies that worked, best first, into the right panel.
        if (score is { Ok: > 0, Strategy: not null })
        {
            int idx = 0;
            while (idx < PassedStrategies.Count && PassedStrategies[idx].Ratio >= score.Ratio) idx++;
            PassedStrategies.Insert(idx, score);
        }

        int done = AutoCandidates.Count(c => c.IsDone);
        int total = AutoCandidates.Count;
        AutoProgress = total > 0 ? (double)done / total : 0;
        AutoProgressText = $"{done} / {total}";
    }

    /// <summary>
    /// Try each combined strategy, score it against the chosen scope's endpoints,
    /// keep the best, save it as a preset and start it. Used by both the Диагностика
    /// tab and the Simple-mode «Переподобрать» button.
    /// </summary>
    /// <summary>Ordered auto-select candidate list: known-good strategies first (the one saved for this
    /// network, then the built-in "main" combos — each probed scoped, as it really runs), then the full
    /// global catalog. Deduped by argument signature. The order IS the probe order, so the early-exit
    /// lands on a known-good config before churning the whole catalog.</summary>
    private async Task<IReadOnlyList<ComboStrategy>> BuildAutoCandidatesAsync()
    {
        var list = new List<ComboStrategy>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, IReadOnlyList<string> args, bool bypassAll, Preset? src)
        {
            if (!seen.Add(string.Join("", args))) return;
            list.Add(new ComboStrategy(name, new List<string>(args)) { BypassAll = bypassAll, SourcePreset = src });
        }

        // 1) The strategy that last worked on THIS network — the most likely instant win.
        string? fp = await Task.Run(NetworkFingerprint.Current);
        if (fp is not null && Settings.NetworkStrategies.TryGetValue(fp, out var savedName)
            && _presets.FindByName(savedName) is { } saved)
            Add(saved.Name, saved.Args, bypassAll: false, src: saved);

        // 2) The built-in "main" combos (recommended first). Filter to the general combos that also
        //    cover YouTube — skips the special-purpose voice-only / adaptive presets, which aren't
        //    general strategies and would only waste a probe cycle.
        foreach (var p in _presets.All
                     .Where(p => p.IsBuiltIn && p.Args.Any(a => a.Contains("{HOSTLIST:youtube}", StringComparison.Ordinal)))
                     .OrderByDescending(p => p.IsRecommended))
            Add(p.Name, p.Args, bypassAll: false, src: p);

        // 3) The full global catalog — exhaustive fallback.
        foreach (var c in ComboStrategyCatalog.All)
            Add(c.Name, c.Args, bypassAll: true, src: null);

        return list;
    }

    /// <summary>Turn an auto-selected candidate into the active preset: a ready preset (saved-for-network
    /// or built-in) is selected in place — no duplicate; a global-catalog winner is re-scoped and saved
    /// as a new "Автоподбор: …" preset. Returns the preset that ended up selected.</summary>
    private Preset SaveOrSelectAutoWinner(ComboStrategy strategy)
    {
        var preset = AutoSelectService.ToPreset(strategy, SelectedScope);
        if (_presets.All.All(p => p.Name != preset.Name))
        {
            _presets.AddUser(preset);
            ReloadPresets();
        }
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name) ?? Presets.LastOrDefault();
        return preset;
    }

    private async Task RunAutoSelectAsync(bool showWindow = true)
    {
        if (IsAutoSelecting) return;
        if (!_updater.IsEngineInstalled) { SetAutoStatus("Движок ещё не установлен — дождитесь загрузки."); return; }

        if (IsRunning)
        {
            AppendLog("Остановка движка для авто-подбора…");
            _engine.Stop();
            await Task.Delay(700);
        }

        // Reset live state for the popup: one row per candidate + the goal endpoints.
        // Goal endpoints = the chosen scope's hosts + every custom-target domain (always included).
        var goalHosts = SelectedScope.GoalHosts().Concat(_targets.AllDomains())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Known-good strategies first (saved-for-this-network, then the built-in "main" combos —
        // probed scoped, as they really run), then the full global catalog. Early-exit on the first
        // perfect one makes "my saved strategy still works" near-instant instead of a full sweep.
        var candidates = await BuildAutoCandidatesAsync();

        AutoCandidates.Clear();
        foreach (var c in candidates) AutoCandidates.Add(new AutoCandidateRow(c.Name));
        AutoTargets.Clear();
        foreach (var h in goalHosts) AutoTargets.Add(h);
        CheckTargets.Clear();
        PassedStrategies.Clear();
        Verdicts.Clear();
        CurrentCandidateName = "";
        _autoCursor = -1;
        AutoProgress = 0;
        AutoProgressText = $"0 / {AutoCandidates.Count}";

        IsAutoSelecting = true;
        _autoCts = new CancellationTokenSource();
        if (showWindow) AutoCheckStarted?.Invoke();
        try
        {
            SetAutoStatus("Замер базовой доступности без обхода…");
            _baseline = await MeasureBaselineAsync(goalHosts, _autoCts.Token);

            SetAutoStatus($"Подбор лучшей стратегии для «{ScopeTitle}»…");
            var result = await _autoSelect.RunAsync(candidates, goalHosts, _autoCts.Token);
            if (result is null)
            {
                SetAutoStatus("Не удалось подобрать стратегию.");
                return;
            }

            var (strategy, score) = result.Value;
            BuildVerdicts(score.HostList);
            var preset = SaveOrSelectAutoWinner(strategy);
            SetAutoStatus($"Готово: «{preset.Name}» — {score.Detail}. Запуск.");
            if (CanStart) Start();
        }
        catch (OperationCanceledException) { SetAutoStatus("Подбор остановлен."); }
        catch (Exception ex) { SetAutoStatus("Ошибка подбора: " + ex.Message); }
        finally
        {
            IsAutoSelecting = false;
            _autoCts?.Dispose();
            _autoCts = null;
            if (showWindow) AutoCheckFinished?.Invoke();
        }
    }

    // ---- strategy generator (personal, assembled per-service) -------------

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set { if (SetField(ref _isGenerating, value)) { RaiseCommandStates(); RaiseAutoRunState(); } }
    }

    /// <summary>
    /// GENERATE a personal strategy (separate from auto-select): test a grid of TLS-desync bundles,
    /// optimise Discord and YouTube separately, and assemble the best of each into one combo preset
    /// marked ✨ Сгенерировано. Reuses the same live popup as auto-select.
    /// </summary>
    private async Task GenerateStrategyAsync()
    {
        if (IsGenerating || IsAutoSelecting) return;
        if (!_updater.IsEngineInstalled) { SetAutoStatus("Движок ещё не установлен — дождитесь загрузки."); return; }

        if (IsRunning)
        {
            AppendLog("Остановка движка для генерации стратегии…");
            _engine.Stop();
            await Task.Delay(700);
        }

        // Custom-target domains ride the catch-all (fallback) profile, so fold them into the Discord
        // group — the generator then picks a fallback bundle that also works for the user's targets.
        var discord = AutoScope.Discord.GoalHosts().Concat(_targets.AllDomains())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var youtube = AutoScope.YouTube.GoalHosts();

        AutoCandidates.Clear();
        foreach (var c in StrategyGeneratorService.Candidates) AutoCandidates.Add(new AutoCandidateRow(c.Name));
        AutoTargets.Clear();
        foreach (var h in discord.Concat(youtube).Distinct()) AutoTargets.Add(h);
        CheckTargets.Clear();
        PassedStrategies.Clear();
        Verdicts.Clear();
        CurrentCandidateName = "";
        _autoCursor = -1;
        AutoProgress = 0;
        AutoProgressText = $"0 / {AutoCandidates.Count}";

        IsGenerating = true;
        _genCts = new CancellationTokenSource();
        AutoCheckStarted?.Invoke();
        var genHosts = discord.Concat(youtube).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            SetAutoStatus("Замер базовой доступности без обхода…");
            _baseline = await MeasureBaselineAsync(genHosts, _genCts.Token);

            SetAutoStatus("Генерация персональной стратегии под провайдера…");
            var gen = await _generator.GenerateAsync(discord, youtube, Settings.GameFilter, _genCts.Token);
            if (gen is null) { SetAutoStatus("Не удалось сгенерировать стратегию."); return; }

            var (preset, finalRows) = gen;
            BuildVerdicts(finalRows);
            _presets.AddUser(preset);
            SaveTopLeaderboard();   // ★ auto-save the 3 best candidates of this run (with their scores)
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name) ?? Presets.LastOrDefault();
            SetAutoStatus($"Готово: «{preset.Name}». Запуск.");
            if (CanStart) Start();
        }
        catch (OperationCanceledException) { SetAutoStatus("Генерация остановлена."); }
        catch (Exception ex) { SetAutoStatus("Ошибка генерации: " + ex.Message); }
        finally
        {
            IsGenerating = false;
            _genCts?.Dispose();
            _genCts = null;
            AutoCheckFinished?.Invoke();
        }
    }

    /// <summary>Auto-save the 3 best candidate strategies from the just-finished generation as ★ presets
    /// named with their score (e.g. «★ Топ №1 · 9/10 · hostfakesplit vk.com»), replacing the previous
    /// trio so the saved leaderboard and its scores stay current. Candidates carry a full runnable combo
    /// (<see cref="AutoScore.Strategy"/>), so each saved preset is directly applicable.</summary>
    private void SaveTopLeaderboard()
    {
        var top = PassedStrategies
            .Where(s => s.CanApply && s.Ok > 0)
            .OrderByDescending(s => s.Ratio).ThenByDescending(s => s.Ok)
            .Take(3).ToList();
        if (top.Count == 0) return;

        var presets = new List<Preset>();
        for (int i = 0; i < top.Count; i++)
        {
            var s = top[i];
            int score10 = (int)Math.Round(s.Ratio * 10);
            presets.Add(new Preset
            {
                Name = $"★ Топ №{i + 1} · {score10}/10 · {s.Name}",
                Description = $"Автосохранено при генерации {DateTime.Now:dd.MM HH:mm}: набрал {s.Ok}/{s.Total} " +
                              $"проверок ({score10}/10). Тройка обновляется при каждой новой генерации.",
                Args = new List<string>(s.Strategy!.Args),
                IsBuiltIn = false,
            });
        }
        _presets.ReplaceAutoLeaderboard(presets);
    }

}
