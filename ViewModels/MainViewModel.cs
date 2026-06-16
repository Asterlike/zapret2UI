using System.Collections.ObjectModel;
using System.Windows;
using ZapretUI.Models;
using ZapretUI.Mvvm;
using ZapretUI.Services;

namespace ZapretUI.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 3000;

    private readonly UpdaterService _updater = new();
    private readonly EngineService _engine = new();
    private readonly PresetService _presets = new();
    private readonly HostlistService _hostlists = new();
    private readonly SettingsService _settingsSvc = new();
    private readonly AutostartService _autostart = new();
    private readonly DiagnosticsService _diag = new();
    private readonly AutoSelectService _autoSelect = new();
    private readonly IpsetService _ipset = new();
    private readonly MonitorService _monitor = new();
    private CancellationTokenSource? _diagCts;
    private CancellationTokenSource? _autoCts;

    /// <summary>Raised to show a soft tray notification (title, message).</summary>
    public event Action<string, string>? Notify;

    /// <summary>Raised when a user-initiated auto-select starts — the View shows the popup.</summary>
    public event Action? AutoCheckStarted;
    /// <summary>Raised when the auto-select finishes/cancels — the View closes the popup.</summary>
    public event Action? AutoCheckFinished;

    public AppSettings Settings => _settingsSvc.Settings;

    public MainViewModel()
    {
        _engine.StateChanged += s => OnUi(() => State = s);
        _engine.LogLine += AppendLog;

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart);
        StopCommand = new RelayCommand(_ => _engine.Stop(), _ => CanStop);
        ToggleCommand = new RelayCommand(_ => { if (IsRunning) _engine.Stop(); else Start(); },
                                         _ => !IsUpdating && (IsRunning || CanStart));
        CheckUpdateCommand = new RelayCommand(async _ => await CheckAndUpdateAsync(silent: false),
                                              _ => !IsUpdating);
        ClearLogCommand = new RelayCommand(_ => LogLines.Clear());

        NewHostlistCommand = new RelayCommand(_ => NewHostlist());
        DeleteHostlistCommand = new RelayCommand(_ => DeleteHostlist(), _ => SelectedHostlist is not null);
        SaveHostlistCommand = new RelayCommand(_ => SaveHostlist(), _ => SelectedHostlist is not null);
        AddDomainCommand = new RelayCommand(_ => AddDomain(), _ => SelectedHostlist is not null);

        ApplyStrategyCommand = new RelayCommand(async _ => await ApplyStrategyAsync(),
                                                _ => IsStrategyChangePending && !IsUpdating);
        DuplicatePresetCommand = new RelayCommand(_ => DuplicatePreset(), _ => SelectedPreset is not null);
        DeletePresetCommand = new RelayCommand(_ => DeletePreset(),
                                               _ => SelectedPreset is { IsBuiltIn: false });
        SavePresetCommand = new RelayCommand(_ => SavePreset(),
                                             _ => SelectedPreset is { IsBuiltIn: false });

        SimpleToggleCommand = new RelayCommand(_ => SimpleToggle(),
            _ => !IsUpdating && !IsAutoSelecting && (IsRunning || CanStart));
        SmartPickCommand = new RelayCommand(async _ => await RunAutoSelectAsync(),
            _ => !IsAutoSelecting && !IsUpdating && _updater.IsEngineInstalled);
        SetSimpleModeCommand = new RelayCommand(_ => IsSimpleMode = true);
        SetAdvancedModeCommand = new RelayCommand(_ => IsSimpleMode = false);

        RunDiagnosticsCommand = new RelayCommand(async _ => await RunDiagnosticsAsync(), _ => !IsDiagnosing);
        StopDiagnosticsCommand = new RelayCommand(_ => _diagCts?.Cancel(), _ => IsDiagnosing);
        RunAutoSelectCommand = new RelayCommand(async _ => await RunAutoSelectAsync(),
            _ => !IsAutoSelecting && !IsDiagnosing && !IsUpdating && _updater.IsEngineInstalled);
        StopAutoSelectCommand = new RelayCommand(_ => _autoCts?.Cancel(), _ => IsAutoSelecting);
        ApplyScoreCommand = new RelayCommand(p => ApplyScore(p as AutoScore),
            p => (p as AutoScore)?.CanApply == true);
        BuildDiscordIpsetCommand = new RelayCommand(async _ => await BuildIpsetAsync(),
            _ => !IsBuildingIpset && _updater.IsEngineInstalled);

        foreach (var r in DiagnosticsService.BuildRows()) DiagRows.Add(r);

        _diag.Status += s => OnUi(() => DiagStatusText = s);
        _autoSelect.Status += s => OnUi(() => SetAutoStatus(s));
        _autoSelect.CandidateStarted += name => OnUi(() => MarkCandidateRunning(name));
        _autoSelect.HostProbed += (host, t12, t13) => OnUi(() => OnHostProbed(host, t12, t13));
        _autoSelect.ScoreReady += sc => OnUi(() => ApplyCandidateScore(sc));
        _monitor.ConnectivityLost += () => OnUi(() => _ = AutoHealAsync());
    }

    // ---- collections -------------------------------------------------------

    public ObservableCollection<Preset> Presets { get; } = new();
    public ObservableCollection<string> Hostlists { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<DiagRow> DiagRows { get; } = new();

    /// <summary>Successful strategies, shown in the Проверка tab after a run finishes.</summary>
    public ObservableCollection<AutoScore> AutoScores { get; } = new();

    /// <summary>Live per-candidate rows, shown in the popup while a run is in progress.</summary>
    public ObservableCollection<AutoCandidateRow> AutoCandidates { get; } = new();

    /// <summary>Goal endpoints of the current run, shown as chips in the popup.</summary>
    public ObservableCollection<string> AutoTargets { get; } = new();

    /// <summary>The current candidate's per-target probe state (popup main area).</summary>
    public ObservableCollection<CheckTargetRow> CheckTargets { get; } = new();

    /// <summary>Strategies that passed (Ok &gt; 0), accumulating live (popup right panel).</summary>
    public ObservableCollection<AutoScore> PassedStrategies { get; } = new();

    // ---- commands ----------------------------------------------------------

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand NewHostlistCommand { get; }
    public RelayCommand DeleteHostlistCommand { get; }
    public RelayCommand SaveHostlistCommand { get; }
    public RelayCommand AddDomainCommand { get; }
    public RelayCommand ApplyStrategyCommand { get; }
    public RelayCommand DuplicatePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand SimpleToggleCommand { get; }
    public RelayCommand SmartPickCommand { get; }
    public RelayCommand SetSimpleModeCommand { get; }
    public RelayCommand SetAdvancedModeCommand { get; }
    public RelayCommand RunDiagnosticsCommand { get; }
    public RelayCommand StopDiagnosticsCommand { get; }
    public RelayCommand RunAutoSelectCommand { get; }
    public RelayCommand StopAutoSelectCommand { get; }
    public RelayCommand ApplyScoreCommand { get; }
    public RelayCommand BuildDiscordIpsetCommand { get; }

    // ---- engine state ------------------------------------------------------

    private EngineState _state = EngineState.Stopped;
    public EngineState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(DiagEngineNote));
                // Once the engine is fully stopped, nothing is running anymore.
                if (value == EngineState.Stopped) RunningPreset = null;
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                UpdateMonitor();
                RaiseCommandStates();
            }
        }
    }

    public bool IsRunning => State == EngineState.Running;
    public bool CanStart => State == EngineState.Stopped && !IsUpdating && _updater.IsEngineInstalled;
    public bool CanStop => State is EngineState.Running or EngineState.Starting;

    // ---- presets -----------------------------------------------------------

    private Preset? _selectedPreset;
    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value))
            {
                Settings.ActivePresetName = value?.Name;
                _settingsSvc.Save();
                OnPropertyChanged(nameof(PresetArgsText));
                OnPropertyChanged(nameof(CommandPreview));
                OnPropertyChanged(nameof(SelectedPresetEditable));
                OnPropertyChanged(nameof(DiagEngineNote));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                RaiseCommandStates();
            }
        }
    }

    public bool SelectedPresetEditable => SelectedPreset is { IsBuiltIn: false };

    // The preset the engine is ACTUALLY running right now (captured at Start),
    // as opposed to SelectedPreset which is merely highlighted in the UI. A
    // strategy change needs an engine restart, so these can diverge until the
    // user confirms with ApplyStrategyCommand.
    private Preset? _runningPreset;
    public Preset? RunningPreset
    {
        get => _runningPreset;
        private set
        {
            if (SetField(ref _runningPreset, value))
            {
                OnPropertyChanged(nameof(RunningPresetName));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                RaiseCommandStates();
            }
        }
    }

    public string RunningPresetName => RunningPreset?.Name ?? "—";

    /// <summary>True when the engine runs one preset but the user has selected a different one.</summary>
    public bool IsStrategyChangePending =>
        IsRunning && RunningPreset is not null && SelectedPreset is not null
        && !ReferenceEquals(RunningPreset, SelectedPreset);

    /// <summary>Sub-line under the state badge: what is ENABLED (running), not just selected.</summary>
    public string RunStatusText =>
        IsRunning
            ? $"Включён: {RunningPresetName}"
            : SelectedPreset is null ? "пресет не выбран" : $"Выбран: {SelectedPreset.Name}";

    /// <summary>Args of the selected preset, one per line, for editing.</summary>
    public string PresetArgsText
    {
        get => SelectedPreset is null ? "" : string.Join('\n', SelectedPreset.Args);
        set
        {
            if (SelectedPreset is { IsBuiltIn: false } p)
            {
                p.Args = value.Replace("\r\n", "\n").Split('\n')
                              .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                OnPropertyChanged(nameof(CommandPreview));
            }
        }
    }

    public string CommandPreview =>
        SelectedPreset is null
            ? ""
            : EngineService.PreviewCommandLine(SelectedPreset, ActiveHostlistPath);

    // ---- hostlists ---------------------------------------------------------

    private string? _selectedHostlist;
    public string? SelectedHostlist
    {
        get => _selectedHostlist;
        set
        {
            if (SetField(ref _selectedHostlist, value))
            {
                Settings.ActiveHostlist = value;
                _settingsSvc.Save();
                HostlistContent = value is null ? "" : _hostlists.Read(value);
                OnPropertyChanged(nameof(CommandPreview));
                RaiseCommandStates();
            }
        }
    }

    private string _hostlistContent = "";
    public string HostlistContent
    {
        get => _hostlistContent;
        set => SetField(ref _hostlistContent, value);
    }

    private string _newDomain = "";
    public string NewDomain { get => _newDomain; set => SetField(ref _newDomain, value); }

    private string? ActiveHostlistPath =>
        SelectedHostlist is not null && _hostlists.Exists(SelectedHostlist)
            ? _hostlists.GetPath(SelectedHostlist)
            : null;

    // ---- updates -----------------------------------------------------------

    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (SetField(ref _isUpdating, value))
            {
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();
            }
        }
    }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    private string _engineVersion = "—";
    public string EngineVersion { get => _engineVersion; private set => SetField(ref _engineVersion, value); }

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
        AutoScope.Discord => "Подбор оптимизируется под Discord.",
        AutoScope.YouTube => "Подбор оптимизируется под YouTube.",
        _ => "Подбор ищет одну стратегию, рабочую сразу для Discord и YouTube.",
    };

    // ---- settings toggles (bound to checkboxes) ----------------------------

    public bool AutoUpdateEngine
    {
        get => Settings.AutoUpdateEngine;
        set { Settings.AutoUpdateEngine = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool AutostartEnabled
    {
        get => Settings.Autostart;
        set
        {
            Settings.Autostart = value;
            if (value) _autostart.Enable(); else _autostart.Disable();
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }

    public bool AutostartEngine
    {
        get => Settings.AutostartEngine;
        set { Settings.AutostartEngine = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set { Settings.MinimizeToTray = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    public bool AutoHeal
    {
        get => Settings.AutoHeal;
        set { Settings.AutoHeal = value; _settingsSvc.Save(); OnPropertyChanged(); UpdateMonitor(); }
    }

    /// <summary>Run the watchdog only while the engine is up and auto-heal is on.</summary>
    private void UpdateMonitor()
    {
        if (IsRunning && Settings.AutoHeal) { if (!_monitor.IsRunning) _monitor.Start(); }
        else _monitor.Stop();
    }

    /// <summary>Watchdog tripped: silently re-pick the best strategy and restart.</summary>
    private async Task AutoHealAsync()
    {
        if (IsAutoSelecting || IsUpdating) return;
        Notify?.Invoke("Zapret UI", "Обход перестал работать — подбираю заново…");
        AppendLog("Авто-починка: обход перестал отвечать, перезапускаю подбор.");
        await RunAutoSelectAsync(showWindow: false);
        if (IsRunning)
            Notify?.Invoke("Zapret UI", "Обход восстановлен автоматически.");
    }

    // ---- simple / advanced mode -------------------------------------------

    public bool IsSimpleMode
    {
        get => Settings.SimpleMode;
        set
        {
            if (Settings.SimpleMode == value) return;
            Settings.SimpleMode = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAdvancedMode));
        }
    }

    public bool IsAdvancedMode => !IsSimpleMode;

    /// <summary>The preset the Simple-mode one-click button applies (combined Discord+YouTube).</summary>
    public Preset? RecommendedPreset =>
        Presets.FirstOrDefault(p => p.IsRecommended) ?? Presets.FirstOrDefault();

    private string _simpleStatus = "Нажмите «Включить обход» — приложение применит рекомендуемый набор и запустит DPI-обход.";
    public string SimpleStatus { get => _simpleStatus; private set => SetField(ref _simpleStatus, value); }

    private void SimpleToggle()
    {
        if (IsRunning) { _engine.Stop(); SimpleStatus = "Обход остановлен."; return; }

        var preset = RecommendedPreset;
        if (preset is null) { SimpleStatus = "Движок ещё не установлен — дождитесь загрузки."; return; }
        SelectedPreset = preset;
        SimpleStatus = $"Запускаю обход: «{preset.Name}».";
        Start();
    }

    // ---- diagnostics (endpoint matrix) ------------------------------------

    private bool _isDiagnosing;
    public bool IsDiagnosing
    {
        get => _isDiagnosing;
        private set { if (SetField(ref _isDiagnosing, value)) RaiseCommandStates(); }
    }

    private string _diagStatusText = "Запустите диагностику, чтобы увидеть, что открывается, а что режется.";
    public string DiagStatusText { get => _diagStatusText; private set => SetField(ref _diagStatusText, value); }

    private string _diagSummary = "";
    public string DiagSummary { get => _diagSummary; private set => SetField(ref _diagSummary, value); }

    /// <summary>Reminds the user that the matrix reflects whatever is on the wire right now.</summary>
    public string DiagEngineNote => IsRunning
        ? $"Тест идёт с активным обходом: «{SelectedPreset?.Name}». Сравните с выключенным."
        : "Обход выключен — это базовая проверка. Включите обход и перезапустите, чтобы увидеть разницу.";

    private async Task RunDiagnosticsAsync()
    {
        if (IsDiagnosing) return;
        IsDiagnosing = true;
        DiagSummary = "";
        _diagCts = new CancellationTokenSource();
        try
        {
            await _diag.RunAsync(DiagRows.ToList(), _diagCts.Token);
            ComputeDiagSummary();
        }
        catch (OperationCanceledException) { DiagStatusText = "Диагностика остановлена."; }
        catch (Exception ex) { DiagStatusText = "Ошибка диагностики: " + ex.Message; }
        finally
        {
            IsDiagnosing = false;
            _diagCts?.Dispose();
            _diagCts = null;
        }
    }

    private void ComputeDiagSummary()
    {
        int ok = 0, bad = 0, to = 0;
        foreach (var r in DiagRows)
        {
            if (r.PingOnly) continue;
            foreach (var s in new[] { r.Http, r.Tls12, r.Tls13 })
            {
                if (s == DiagStatus.Ok) ok++;
                else if (s == DiagStatus.Fail) bad++;
                else if (s == DiagStatus.Timeout) to++;
            }
        }
        DiagSummary = $"OK: {ok}   ·   Ошибки: {bad}   ·   Таймауты: {to}";
    }

    // ---- auto-select (best strategy for the chosen scope) -----------------

    private bool _isAutoSelecting;
    public bool IsAutoSelecting
    {
        get => _isAutoSelecting;
        private set { if (SetField(ref _isAutoSelecting, value)) RaiseCommandStates(); }
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

    private void OnHostProbed(string host, DiagStatus tls12, DiagStatus tls13)
    {
        foreach (var t in CheckTargets)
        {
            if (t.Host == host) { t.Tls12 = tls12; t.Tls13 = tls13; return; }
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
                if (t.Host == h.Host) { t.Tls12 = h.Tls12; t.Tls13 = h.Tls13; break; }

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
    /// keep the best, save it as a preset and start it. Used by both the Проверка
    /// tab and the Simple-mode «Переподобрать» button.
    /// </summary>
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
        AutoScores.Clear();
        AutoCandidates.Clear();
        foreach (var c in ComboStrategyCatalog.All) AutoCandidates.Add(new AutoCandidateRow(c.Name));
        AutoTargets.Clear();
        foreach (var h in SelectedScope.GoalHosts()) AutoTargets.Add(h);
        CheckTargets.Clear();
        PassedStrategies.Clear();
        CurrentCandidateName = "";
        _autoCursor = -1;
        AutoProgress = 0;
        AutoProgressText = $"0 / {AutoCandidates.Count}";

        IsAutoSelecting = true;
        _autoCts = new CancellationTokenSource();
        if (showWindow) AutoCheckStarted?.Invoke();
        try
        {
            SetAutoStatus($"Подбираю лучшую стратегию для «{ScopeTitle}»…");
            var result = await _autoSelect.RunAsync(SelectedScope.GoalHosts(), _autoCts.Token);
            if (result is null)
            {
                SetAutoStatus("Не удалось подобрать стратегию.");
                return;
            }

            var (strategy, score) = result.Value;
            var preset = AutoSelectService.ToPreset(strategy, SelectedScope);
            _presets.AddUser(preset);
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name) ?? Presets.LastOrDefault();
            SetAutoStatus($"Лучшая: «{strategy.Name}» — {score.Detail}. Применил и запускаю обход.");
            if (CanStart) Start();
        }
        catch (OperationCanceledException) { SetAutoStatus("Подбор остановлен."); }
        catch (Exception ex) { SetAutoStatus("Ошибка подбора: " + ex.Message); }
        finally
        {
            // Surface only the strategies that worked in the main tab, best first.
            PublishSuccessfulScores();
            IsAutoSelecting = false;
            _autoCts?.Dispose();
            _autoCts = null;
            if (showWindow) AutoCheckFinished?.Invoke();
        }
    }

    /// <summary>After a run, fill the tab's list with the candidates that worked (best first).</summary>
    private void PublishSuccessfulScores()
    {
        AutoScores.Clear();
        var ok = AutoCandidates
            .Where(c => c.IsDone && c.Score is { Ok: > 0 })
            .Select(c => c.Score!)
            .OrderByDescending(s => s.Ratio)
            .ThenBy(s => s.Fail)
            .ToList();
        foreach (var s in ok) AutoScores.Add(s);
    }

    // ---- ipset (IP-based bypass) ------------------------------------------

    private bool _isBuildingIpset;
    public bool IsBuildingIpset
    {
        get => _isBuildingIpset;
        private set { if (SetField(ref _isBuildingIpset, value)) RaiseCommandStates(); }
    }

    private string _ipsetStatus = "Соберите список IP Discord, чтобы включить обход по IP (для жёстких блоков).";
    public string IpsetStatus { get => _ipsetStatus; private set => SetField(ref _ipsetStatus, value); }

    private async Task BuildIpsetAsync()
    {
        if (IsBuildingIpset) return;
        IsBuildingIpset = true;
        try
        {
            IpsetStatus = "Резолвлю домены Discord и собираю IP-подсети…";
            var domains = _hostlists.Exists("discord")
                ? _hostlists.ReadDomains("discord")
                : new List<string> { "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "discord.media", "discordapp.net" };
            var res = await _ipset.BuildDiscordIpsetAsync(domains, CancellationToken.None);
            IpsetStatus = $"Готово: собрано {res.Subnets} подсетей. Теперь работает пресет «Discord — по IP (ipset)».";
            OnPropertyChanged(nameof(CommandPreview));
        }
        catch (Exception ex)
        {
            IpsetStatus = "Не удалось собрать IP-список: " + ex.Message;
        }
        finally
        {
            IsBuildingIpset = false;
        }
    }

    /// <summary>Save a specific tried candidate as a preset and make it active.</summary>
    private void ApplyScore(AutoScore? score)
    {
        if (score?.Strategy is null) return;
        var preset = AutoSelectService.ToPreset(score.Strategy, SelectedScope);
        _presets.AddUser(preset);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name) ?? Presets.LastOrDefault();
        SetAutoStatus($"Сохранено как пресет «{preset.Name}» и выбрано активным. Нажмите «Запустить».");
    }

    // ---- lifecycle ---------------------------------------------------------

    public async Task InitializeAsync()
    {
        ReloadPresets();
        _hostlists.SeedDefaults();
        ReloadHostlists();

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == Settings.ActivePresetName)
                         ?? Presets.FirstOrDefault();
        if (Settings.ActiveHostlist is not null && _hostlists.Exists(Settings.ActiveHostlist))
            SelectedHostlist = Settings.ActiveHostlist;
        else
            SelectedHostlist = Hostlists.FirstOrDefault();

        EngineVersion = _updater.InstalledVersion ?? "не установлен";

        if (Settings.AutoUpdateEngine || !_updater.IsEngineInstalled || !_updater.IsEngineComplete)
            await CheckAndUpdateAsync(silent: true);

        if (Settings.AutostartEngine && CanStart && SelectedPreset is not null)
            Start();
    }

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var p in _presets.All) Presets.Add(p);
        OnPropertyChanged(nameof(RecommendedPreset));
    }

    private void ReloadHostlists()
    {
        Hostlists.Clear();
        foreach (var h in _hostlists.GetLists()) Hostlists.Add(h);
    }

    // ---- actions -----------------------------------------------------------

    private void Start()
    {
        if (SelectedPreset is null)
        {
            AppendLog("Не выбран пресет.");
            return;
        }
        try
        {
            _engine.Start(SelectedPreset, SelectedPreset.UsesHostlist ? ActiveHostlistPath : null);
            RunningPreset = SelectedPreset;
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            MessageBox.Show(ex.Message, "Не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Apply the currently selected strategy to a running engine. Since a strategy
    /// change can't happen in-place, this stops the engine and relaunches it on the
    /// selected preset. No-op (just Start) when the engine is idle.
    /// </summary>
    private async Task ApplyStrategyAsync()
    {
        if (SelectedPreset is null) return;
        if (!IsRunning) { if (CanStart) Start(); return; }

        AppendLog($"Смена стратегии → «{SelectedPreset.Name}». Перезапуск движка…");
        _engine.Stop();
        // Wait for the process to release WinDivert before relaunching.
        for (int i = 0; i < 60 && State != EngineState.Stopped; i++)
            await Task.Delay(50);
        await Task.Delay(250);
        if (CanStart) Start();
    }

    public async Task CheckAndUpdateAsync(bool silent)
    {
        if (IsUpdating) return;
        IsUpdating = true;
        try
        {
            UpdateStatus = "Проверка обновлений…";
            ReleaseInfo latest;
            try
            {
                latest = await _updater.FetchLatestAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus = _updater.IsEngineInstalled
                    ? $"Не удалось проверить обновления ({ex.Message}). Работаем на установленной версии."
                    : $"Нет связи с GitHub: {ex.Message}";
                return;
            }

            if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete || _updater.IsUpdateAvailable(latest))
            {
                bool wasRunning = IsRunning;
                if (wasRunning)
                {
                    AppendLog("Остановка движка для обновления…");
                    _engine.Stop();
                    await Task.Delay(800);
                }

                var progress = new Progress<UpdateProgress>(p =>
                {
                    UpdateProgress = p.Fraction;
                    UpdateStatus = p.Message;
                });
                try
                {
                    await _updater.InstallAsync(latest, progress);
                }
                catch (Exception ex)
                {
                    UpdateStatus = $"Не удалось установить движок: {ex.Message}";
                    AppendLog("Ошибка загрузки движка: " + ex.Message);
                    return;
                }
                EngineVersion = _updater.InstalledVersion ?? "—";
                UpdateStatus = $"Движок обновлён: {latest.Tag}";
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();

                if (wasRunning && CanStart) Start();
            }
            else
            {
                UpdateStatus = $"Актуальная версия: {latest.Tag}";
            }
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private void NewHostlist()
    {
        string name = "list";
        int i = 1;
        while (_hostlists.Exists(name)) name = $"list{++i}";
        _hostlists.Create(name);
        ReloadHostlists();
        SelectedHostlist = name;
    }

    private void DeleteHostlist()
    {
        if (SelectedHostlist is null) return;
        if (MessageBox.Show($"Удалить список «{SelectedHostlist}»?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _hostlists.Delete(SelectedHostlist);
        ReloadHostlists();
        SelectedHostlist = Hostlists.FirstOrDefault();
    }

    private void SaveHostlist()
    {
        if (SelectedHostlist is null) return;
        _hostlists.Write(SelectedHostlist, HostlistContent);
        AppendLog($"Список «{SelectedHostlist}» сохранён.");
    }

    private void AddDomain()
    {
        if (SelectedHostlist is null || string.IsNullOrWhiteSpace(NewDomain)) return;
        _hostlists.AddDomain(SelectedHostlist, NewDomain);
        HostlistContent = _hostlists.Read(SelectedHostlist);
        NewDomain = "";
    }

    private void DuplicatePreset()
    {
        if (SelectedPreset is null) return;
        var copy = SelectedPreset.Clone();
        copy.Name = SelectedPreset.Name + " (моя копия)";
        _presets.AddUser(copy);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == copy.Name);
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p) return;
        if (MessageBox.Show($"Удалить пресет «{p.Name}»?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _presets.DeleteUser(p);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault();
    }

    private void SavePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } p) return;
        _presets.UpdateUser(p);
        OnPropertyChanged(nameof(CommandPreview));
        AppendLog($"Пресет «{p.Name}» сохранён.");
    }

    public void StopEngine() => _engine.Stop();

    /// <summary>Stop everything and release resources on application exit.</summary>
    public void Shutdown()
    {
        try { _diagCts?.Cancel(); } catch { }
        try { _autoCts?.Cancel(); } catch { }
        try { _monitor.Stop(); } catch { }
        try { _autoSelect.Dispose(); } catch { }
        try { _engine.Dispose(); } catch { }
    }

    // ---- helpers -----------------------------------------------------------

    private void AppendLog(string line)
    {
        OnUi(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });
    }

    private void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ToggleCommand.RaiseCanExecuteChanged();
        CheckUpdateCommand.RaiseCanExecuteChanged();
        DeleteHostlistCommand.RaiseCanExecuteChanged();
        SaveHostlistCommand.RaiseCanExecuteChanged();
        AddDomainCommand.RaiseCanExecuteChanged();
        ApplyStrategyCommand.RaiseCanExecuteChanged();
        DuplicatePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        SimpleToggleCommand.RaiseCanExecuteChanged();
        SmartPickCommand.RaiseCanExecuteChanged();
        RunDiagnosticsCommand.RaiseCanExecuteChanged();
        StopDiagnosticsCommand.RaiseCanExecuteChanged();
        RunAutoSelectCommand.RaiseCanExecuteChanged();
        StopAutoSelectCommand.RaiseCanExecuteChanged();
        BuildDiscordIpsetCommand.RaiseCanExecuteChanged();
    }

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.Invoke(a);
    }
}
