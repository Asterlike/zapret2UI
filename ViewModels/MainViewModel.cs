using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

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
    private readonly StrategyGeneratorService _generator = new();
    private readonly TargetService _targets = new();
    private readonly IpsetService _ipset = new();
    private readonly ExclusionService _exclusions = new();
    private readonly MonitorService _monitor = new();
    private readonly TelegramProxyService _tgProxy = new();
    private CancellationTokenSource? _diagCts;
    private CancellationTokenSource? _dpiCts;
    private CancellationTokenSource? _autoCts;
    private CancellationTokenSource? _genCts;

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
        _tgProxy.LogLine += AppendProxyLog; // proxy output goes to its own log/tab, separate from the engine
        _tgProxy.StateChanged += () => OnUi(OnTelegramProxyStateChanged);

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart);
        StopCommand = new RelayCommand(_ => _engine.Stop(), _ => CanStop);
        ToggleCommand = new RelayCommand(_ => { if (IsRunning) _engine.Stop(); else Start(); },
                                         _ => !IsUpdating && (IsRunning || CanStart));
        CheckUpdateCommand = new RelayCommand(async _ => await CheckAndUpdateAsync(silent: false),
                                              _ => !IsUpdating);
        ClearLogCommand = new RelayCommand(_ => LogLines.Clear());
        ClearProxyLogCommand = new RelayCommand(_ => ProxyLogLines.Clear());
        CopyLogCommand = new RelayCommand(_ => CopyLinesToClipboard(LogLines, "Журнал движка"));
        CopyProxyLogCommand = new RelayCommand(_ => CopyLinesToClipboard(ProxyLogLines, "Журнал Telegram"));
        // Generic "open a URL in the browser" — used by the in-app documentation/help links (README,
        // manual, Telegram channel). The link itself is passed as the CommandParameter from XAML.
        OpenLinkCommand = new RelayCommand(p => { if (p is string url && url.Length > 0) OpenUrl(url); });

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
        // Simple-mode "Переподобрать под провайдера": build a PERSONAL per-service combo (generator),
        // not the quick catalog auto-select — the catalog applies one strategy to everything and can't
        // cover Discord + YouTube when they need different desyncs, which is the whole "нет пресета под всё".
        SmartPickCommand = new RelayCommand(async _ => await GenerateStrategyAsync(),
            _ => !IsAutoSelecting && !IsGenerating && !IsUpdating && _updater.IsEngineInstalled);
        SetSimpleModeCommand = new RelayCommand(_ => IsSimpleMode = true);
        SetAdvancedModeCommand = new RelayCommand(_ => IsSimpleMode = false);
        GoToSettingsCommand = new RelayCommand(_ => { IsSimpleMode = false; SelectedTabIndex = SettingsTabIndex; });
        // Single big button on the Home screen — routes to the right toggle per mode.
        HomeToggleCommand = new RelayCommand(
            _ => (IsSimpleMode ? SimpleToggleCommand : ToggleCommand).Execute(null),
            _ => (IsSimpleMode ? SimpleToggleCommand : ToggleCommand).CanExecute(null));

        RunDiagnosticsCommand = new RelayCommand(async _ => await RunDiagnosticsAsync(), _ => !IsDiagnosing && !IsAutoRunning);
        StopDiagnosticsCommand = new RelayCommand(_ => _diagCts?.Cancel(), _ => IsDiagnosing);
        RunDpiCheckCommand = new RelayCommand(async _ => await RunDpiCheckAsync(),
            _ => !IsDpiChecking && !IsDiagnosing && !IsAutoRunning);
        StopDpiCheckCommand = new RelayCommand(_ => _dpiCts?.Cancel(), _ => IsDpiChecking);
        ToggleDonateCommand = new RelayCommand(_ => DonateExpanded = !DonateExpanded);
        RunAutoSelectCommand = new RelayCommand(async _ => await RunAutoSelectAsync(),
            _ => !IsAutoSelecting && !IsDiagnosing && !IsUpdating && _updater.IsEngineInstalled);
        StopAutoSelectCommand = new RelayCommand(_ => { _autoCts?.Cancel(); _genCts?.Cancel(); },
            _ => IsAutoSelecting || IsGenerating);
        GenerateStrategyCommand = new RelayCommand(async _ => await GenerateStrategyAsync(),
            _ => !IsAutoSelecting && !IsGenerating && !IsDiagnosing && !IsUpdating && _updater.IsEngineInstalled);
        ApplyScoreCommand = new RelayCommand(p => ApplyScore(p as AutoScore),
            p => (p as AutoScore)?.CanApply == true);
        ApplyScoreAndStartCommand = new RelayCommand(async p => await ApplyScoreAndStartAsync(p as AutoScore),
            p => (p as AutoScore)?.CanApply == true && !IsAutoRunning);
        BuildDiscordIpsetCommand = new RelayCommand(async _ => await BuildIpsetAsync(),
            _ => !IsBuildingIpset && _updater.IsEngineInstalled);
        ApplyExclusionsCommand = new RelayCommand(async _ => await ApplyExclusionsAsync(),
            _ => !IsApplyingExclusions);
        TogglePresetArgsCommand = new RelayCommand(_ => ShowPresetArgs = !ShowPresetArgs);
        OpenHowItWorksCommand = new RelayCommand(_ => ShowHowItWorks = true);
        CloseHowItWorksCommand = new RelayCommand(_ => ShowHowItWorks = false);
        // Telegram card → built-in tg-ws-proxy (needs no winws2 engine and no admin rights).
        EnableTelegramCommand = new RelayCommand(_ => ToggleTelegramProxy());
        OpenTelegramProxyLinkCommand = new RelayCommand(_ => OpenUrl(_tgProxy.ProxyLink));
        CopyTelegramProxyLinkCommand = new RelayCommand(_ => CopyToClipboard(_tgProxy.ProxyLink));
        CheckTelegramProxyCommand = new RelayCommand(async _ => await CheckTelegramProxyAsync(),
                                                     _ => !_isCheckingTgProxy);
        OpenAppReleaseCommand = new RelayCommand(_ => OpenUrl(_appLatestUrl));

        // ---- custom targets (Диагностика tab) ----
        AddTargetCommand = new RelayCommand(_ => OpenTargetPopup(null));
        OpenTargetCommand = new RelayCommand(p => OpenTargetPopup(p as CustomTarget));
        DeleteTargetCommand = new RelayCommand(p => DeleteTarget(p as CustomTarget));
        ExpandTargetCommand = new RelayCommand(async _ => await ExpandTargetAsync(), _ => !IsExpandingTarget);
        SaveTargetCommand = new RelayCommand(_ => SaveTarget(), _ => !IsExpandingTarget);
        CloseTargetPopupCommand = new RelayCommand(_ => ShowTargetPopup = false);

        // Group the presets list into "Авторские (встроенные)" vs "Личные" (IsBuiltIn).
        PresetsView = CollectionViewSource.GetDefaultView(Presets);
        PresetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Preset.GroupTitle)));

        RebuildDiagRows();
        ReloadTargets();

        _diag.Status += s => OnUi(() => DiagStatusText = s);
        _autoSelect.Status += s => OnUi(() => SetAutoStatus(s));
        _autoSelect.CandidateStarted += name => OnUi(() => MarkCandidateRunning(name));
        _autoSelect.HostProbed += (host, t12, t13, https) => OnUi(() => OnHostProbed(host, t12, t13, https));
        _autoSelect.ScoreReady += sc => OnUi(() => ApplyCandidateScore(sc));
        _generator.Status += s => OnUi(() => SetAutoStatus(s));
        _generator.CandidateStarted += name => OnUi(() => MarkCandidateRunning(name));
        _generator.HostProbed += (host, t12, t13, https) => OnUi(() => OnHostProbed(host, t12, t13, https));
        _generator.ScoreReady += sc => OnUi(() => ApplyCandidateScore(sc));
        _monitor.ConnectivityLost += () => OnUi(() => _ = AutoHealAsync());
    }

    // ---- collections -------------------------------------------------------

    public ObservableCollection<Preset> Presets { get; } = new();
    /// <summary>Grouped view of <see cref="Presets"/> (Авторские/встроенные vs Личные) for the list.</summary>
    public ICollectionView PresetsView { get; }
    public ObservableCollection<string> Hostlists { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    /// <summary>Telegram-proxy output — kept separate from the engine log so the Журнал tab can show
    /// each on its own sub-tab (engine vs proxy) instead of one interleaved stream.</summary>
    public ObservableCollection<string> ProxyLogLines { get; } = new();
    public ObservableCollection<DiagRow> DiagRows { get; } = new();

    /// <summary>User-defined bypass targets, shown on the Диагностика tab (left column).</summary>
    public ObservableCollection<CustomTarget> CustomTargets { get; } = new();

    /// <summary>Live per-candidate rows, shown in the popup while a run is in progress.</summary>
    public ObservableCollection<AutoCandidateRow> AutoCandidates { get; } = new();

    /// <summary>Goal endpoints of the current run, shown as chips in the popup.</summary>
    public ObservableCollection<string> AutoTargets { get; } = new();

    /// <summary>The current candidate's per-target probe state (popup main area).</summary>
    public ObservableCollection<CheckTargetRow> CheckTargets { get; } = new();

    /// <summary>Strategies that passed (Ok &gt; 0), accumulating live (popup right panel).</summary>
    public ObservableCollection<AutoScore> PassedStrategies { get; } = new();

    /// <summary>Per-service «РАБОТАЕТ / ЧАСТИЧНО / НЕ РАБОТАЕТ» verdict for the CHOSEN strategy, shown
    /// as big chips when a run finishes — the visible validation (built from the real HTTP/2 reach
    /// of the applied strategy + the no-bypass baseline).</summary>
    public ObservableCollection<ServiceVerdict> Verdicts { get; } = new();

    /// <summary>No-bypass reachability of the goal hosts, measured once at the start of a run
    /// (host → reachable). Lets the verdict say "разблокировано обходом" vs "и так работал".</summary>
    private Dictionary<string, bool> _baseline = new(StringComparer.OrdinalIgnoreCase);

    // ---- commands ----------------------------------------------------------

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ClearProxyLogCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand CopyProxyLogCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
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
    public RelayCommand RunDpiCheckCommand { get; }
    public RelayCommand StopDpiCheckCommand { get; }
    public RelayCommand ToggleDonateCommand { get; }
    public RelayCommand RunAutoSelectCommand { get; }
    public RelayCommand StopAutoSelectCommand { get; }
    public RelayCommand GenerateStrategyCommand { get; }
    public RelayCommand ApplyScoreCommand { get; }
    public RelayCommand ApplyScoreAndStartCommand { get; }
    public RelayCommand BuildDiscordIpsetCommand { get; }
    public RelayCommand ApplyExclusionsCommand { get; }
    public RelayCommand TogglePresetArgsCommand { get; }
    public RelayCommand OpenHowItWorksCommand { get; }
    public RelayCommand CloseHowItWorksCommand { get; }
    public RelayCommand EnableTelegramCommand { get; }
    public RelayCommand OpenTelegramProxyLinkCommand { get; }
    public RelayCommand CopyTelegramProxyLinkCommand { get; }
    public RelayCommand CheckTelegramProxyCommand { get; }
    public RelayCommand OpenAppReleaseCommand { get; }
    public RelayCommand GoToSettingsCommand { get; }
    public RelayCommand HomeToggleCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand OpenTargetCommand { get; }
    public RelayCommand DeleteTargetCommand { get; }
    public RelayCommand ExpandTargetCommand { get; }
    public RelayCommand SaveTargetCommand { get; }
    public RelayCommand CloseTargetPopupCommand { get; }

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
                // Engine came up with a preset → remember it for this network (re-suggested next time).
                if (value == EngineState.Running) RememberNetworkStrategy();
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
                OnPropertyChanged(nameof(CanStart));
                RaiseCommandStates();
            }
        }
    }

    public bool SelectedPresetEditable => SelectedPreset is { IsBuiltIn: false };

    private bool _showPresetArgs;
    /// <summary>Whether the raw-args editor is revealed on the Стратегии tab (hidden by default to declutter).</summary>
    public bool ShowPresetArgs { get => _showPresetArgs; set => SetField(ref _showPresetArgs, value); }

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
            : EngineService.PreviewCommandLine(SelectedPreset, ActiveHostlistPath, Settings.GameFilter,
                                               Settings.BypassAllSites, Settings.DisableQuic, Settings.TgProxyCoverage);

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
        AutoScope.Discord => "Ищем стратегию под Discord.",
        AutoScope.YouTube => "Ищем стратегию под YouTube.",
        _ => "Ищем одну стратегию сразу под Discord и YouTube.",
    };

    // ---- settings toggles (bound to checkboxes) ----------------------------

    public bool AutoUpdateEngine
    {
        get => Settings.AutoUpdateEngine;
        set { Settings.AutoUpdateEngine = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    /// <summary>Show the app's own corner toasts (start/stop, auto-heal).</summary>
    public bool NotificationsEnabled
    {
        get => Settings.NotificationsEnabled;
        set { Settings.NotificationsEnabled = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    /// <summary>Play a soft sound with each toast notification.</summary>
    public bool NotificationSound
    {
        get => Settings.NotificationSound;
        set { Settings.NotificationSound = value; _settingsSvc.Save(); OnPropertyChanged(); }
    }

    /// <summary>Donate card shown expanded (with QR) vs collapsed to a compact button. Persisted.</summary>
    public bool DonateExpanded
    {
        get => !Settings.DonateCollapsed;
        set
        {
            if (value == !Settings.DonateCollapsed) return;
            Settings.DonateCollapsed = !value;
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }

    // ---- UI scale (DPI-independent zoom; applied by MainWindow via a ScaleTransform) -------
    public double UiScale
    {
        get => Settings.UiScale is >= 1.0 and <= 2.5 ? Settings.UiScale : 1.0;
        set
        {
            double v = Math.Clamp(Math.Round(value, 2), 1.0, 2.5);
            if (Math.Abs(UiScale - v) < 0.001) return;
            Settings.UiScale = v;
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UiScalePercentText));
            OnPropertyChanged(nameof(Scale100));
            OnPropertyChanged(nameof(Scale125));
            OnPropertyChanged(nameof(Scale150));
            OnPropertyChanged(nameof(Scale175));
            OnPropertyChanged(nameof(Scale200));
        }
    }

    public string UiScalePercentText => $"{(int)Math.Round(UiScale * 100)}%";

    // Discrete scale presets bound to themed chips (mutually exclusive RadioButtons).
    public bool Scale100 { get => NearScale(1.0); set { if (value) UiScale = 1.0; } }
    public bool Scale125 { get => NearScale(1.25); set { if (value) UiScale = 1.25; } }
    public bool Scale150 { get => NearScale(1.5); set { if (value) UiScale = 1.5; } }
    public bool Scale175 { get => NearScale(1.75); set { if (value) UiScale = 1.75; } }
    public bool Scale200 { get => NearScale(2.0); set { if (value) UiScale = 2.0; } }
    private bool NearScale(double v) => Math.Abs(UiScale - v) < 0.001;

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

    /// <summary>Flowseal-style game filter: widen capture to game ports (&gt;1023) when on.
    /// Pushes the value into the engine; a running engine is relaunched so it takes effect now.</summary>
    public bool GameFilter
    {
        get => Settings.GameFilter;
        set
        {
            if (value == Settings.GameFilter) return;
            Settings.GameFilter = value;
            _settingsSvc.Save();
            _engine.GameFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyStrategyAsync(); // relaunch so the new capture width applies
        }
    }

    /// <summary>Bypass every site (catch-all) vs allow-list (default off, like Flowseal). Off keeps
    /// games/apps not in any list untouched. Relaunches a running engine so it takes effect now.</summary>
    public bool BypassAllSites
    {
        get => Settings.BypassAllSites;
        set
        {
            if (value == Settings.BypassAllSites) return;
            Settings.BypassAllSites = value;
            _settingsSvc.Save();
            _engine.BypassAllSites = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyStrategyAsync(); // relaunch so the new scope applies
        }
    }

    /// <summary>"QUIC off": drop the desynced services' HTTP/3 so the browser falls back to TCP/H2.
    /// Turn on where the ISP/TSPU throttles QUIC. Relaunches a running engine so it takes effect now.</summary>
    public bool DisableQuic
    {
        get => Settings.DisableQuic;
        set
        {
            if (value == Settings.DisableQuic) return;
            Settings.DisableQuic = value;
            _settingsSvc.Save();
            _engine.DisableQuic = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyStrategyAsync(); // relaunch so QUIC handling changes now
        }
    }

    /// <summary>Let the engine also cover the built-in Telegram proxy's own 443 upstream so its tunnel
    /// survives mobile DPI that corrupts it mid-stream. Off by default — turn on only if the proxy
    /// connects but keeps dropping. Relaunches a running engine so it applies immediately.</summary>
    public bool TgProxyCoverage
    {
        get => Settings.TgProxyCoverage;
        set
        {
            if (value == Settings.TgProxyCoverage) return;
            Settings.TgProxyCoverage = value;
            _settingsSvc.Save();
            _engine.CoverTgProxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyStrategyAsync(); // relaunch so coverage applies now
        }
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
        if (IsAutoRunning || IsUpdating) return;
        Notify?.Invoke("Zapret UI", "Обход упал — переподбор…");
        AppendLog("Авто-починка: обход не отвечает, переподбор.");
        await RunAutoSelectAsync(showWindow: false);
        if (IsRunning)
            Notify?.Invoke("Zapret UI", "Обход восстановлен.");
    }

    /// <summary>Remember which strategy is running on the CURRENT network (local fingerprint) so it can
    /// be re-suggested next time we're on that network. Computed off the UI thread (ARP can block for a
    /// moment on a cache miss); the settings write is marshaled back. Best-effort — no fingerprint (e.g.
    /// offline) just skips it.</summary>
    private void RememberNetworkStrategy()
    {
        string? name = _engine.ActivePreset?.Name;
        if (string.IsNullOrEmpty(name)) return;
        _ = Task.Run(() =>
        {
            string? fp = NetworkFingerprint.Current();
            if (fp is null) return;
            OnUi(() =>
            {
                Settings.NetworkStrategies[fp] = name;
                // Cap growth: drop one old entry once the map gets large (rare).
                if (Settings.NetworkStrategies.Count > 40)
                    Settings.NetworkStrategies.Remove(Settings.NetworkStrategies.Keys.First());
                _settingsSvc.Save();
            });
        });
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
            if (value) SelectedTabIndex = 0; // simple mode shows only the Home tab
            HomeToggleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAdvancedMode => !IsSimpleMode;

    // ---- top-tab navigation (redesign) ------------------------------------
    // Bound to the TabControl; lets the Home gear jump to Настройки and lets
    // Simple mode lock the view to Главная (the tab strip is hidden there).
    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetField(ref _selectedTabIndex, value);
    }

    /// <summary>Index of the Настройки tab (Главная,Стратегии,Хостлисты,Диагностика,Журнал,Telegram,Настройки).</summary>
    private const int SettingsTabIndex = 6;

    /// <summary>The preset the Simple-mode one-click button applies (combined Discord+YouTube).</summary>
    public Preset? RecommendedPreset =>
        Presets.FirstOrDefault(p => p.IsRecommended) ?? Presets.FirstOrDefault();

    private string _simpleStatus = "";
    public string SimpleStatus { get => _simpleStatus; private set => SetField(ref _simpleStatus, value); }

    private void SimpleToggle()
    {
        if (IsRunning) { _engine.Stop(); SimpleStatus = ""; return; }

        var preset = RecommendedPreset;
        if (preset is null) { SimpleStatus = "Движок ещё не установлен — дождитесь загрузки."; return; }
        SelectedPreset = preset;
        SimpleStatus = $"Стратегия: «{preset.Name}»";
        Start();
    }

    // ---- built-in Telegram proxy (native tg-ws-proxy) ----------------------

    /// <summary>True while the local MTProto→WebSocket proxy is listening.</summary>
    public bool IsTelegramProxyRunning => _tgProxy.IsRunning;

    /// <summary>Two-way binding for the Telegram toggle switch next to the main button (shown in both
    /// modes): setting it starts/stops the proxy. The setter re-notifies so a start failure (busy port)
    /// flips the switch back to off instead of leaving it stuck on.</summary>
    public bool IsTelegramProxyEnabled
    {
        get => _tgProxy.IsRunning;
        set
        {
            if (value == _tgProxy.IsRunning) return;
            if (value) _tgProxy.Start(); else _tgProxy.Stop();
            OnPropertyChanged();
        }
    }

    /// <summary>Endpoint to enter in Telegram → Настройки → Прокси (server:port).</summary>
    public string TelegramProxyEndpoint => $"{_tgProxy.Host}:{_tgProxy.Port}";

    /// <summary>The MTProto secret (dd-prefixed) shown next to the endpoint.</summary>
    public string TelegramProxySecret => "dd" + _tgProxy.SecretHex;

    private string _telegramProxyStatus = "Выключено. Нажмите «Включить», затем «Открыть в Telegram».";
    public string TelegramProxyStatus { get => _telegramProxyStatus; private set => SetField(ref _telegramProxyStatus, value); }

    /// <summary>Label for the Telegram card's toggle button.</summary>
    public string TelegramProxyButtonText => _tgProxy.IsRunning ? "Выключить прокси" : "Включить прокси";

    /// <summary>Start/stop the built-in Telegram proxy from the Telegram card.</summary>
    private void ToggleTelegramProxy()
    {
        if (_tgProxy.IsRunning) _tgProxy.Stop();
        else _tgProxy.Start();
    }

    /// <summary>Local listener port for the built-in Telegram proxy (the «Telegram» tab). Persisted; a
    /// change is applied at once — if the proxy is running it's restarted so it rebinds to the new port
    /// (a busy port still falls back to the next free one at bind time).</summary>
    public int TelegramProxyPort
    {
        get => _tgProxy.Port;
        set
        {
            if (value is < 1 or > 65535 || value == _tgProxy.Port) return;
            Settings.TgProxyPort = value;
            _settingsSvc.Save();
            bool wasRunning = _tgProxy.IsRunning;
            if (wasRunning) _tgProxy.Stop();
            _tgProxy.Configure(value, Settings.TgProxySecret);
            if (wasRunning) _tgProxy.Start();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TelegramProxyEndpoint));
        }
    }

    /// <summary>Whether to auto-start the built-in Telegram proxy on app launch.</summary>
    public bool TelegramProxyAutostart
    {
        get => Settings.TgProxyAutostart;
        set
        {
            if (Settings.TgProxyAutostart == value) return;
            Settings.TgProxyAutostart = value;
            _settingsSvc.Save();
            OnPropertyChanged();
        }
    }

    private void RefreshTelegramProxyStatus()
    {
        TelegramProxyStatus = _tgProxy.IsRunning
            ? $"Запущен на {TelegramProxyEndpoint}. В Telegram: Настройки → Данные и память → Прокси, " +
              "или нажмите «Открыть в Telegram»."
            : _tgProxy.StartError ?? "Выключено. Нажмите «Включить», затем «Открыть в Telegram».";
        OnPropertyChanged(nameof(IsTelegramProxyRunning));
        OnPropertyChanged(nameof(IsTelegramProxyEnabled)); // keep the Home toggle (both modes) in sync with real state
        OnPropertyChanged(nameof(TelegramProxyButtonText));
        OnPropertyChanged(nameof(TelegramProxyEndpoint)); // the bound port can change on a busy-port fallback
        OnPropertyChanged(nameof(TelegramProxyPort));     // …so reflect that in the port box too
    }

    private void OnTelegramProxyStateChanged()
    {
        // Persist the auto-generated secret the first time so the tg:// link stays stable across runs.
        if (string.IsNullOrEmpty(Settings.TgProxySecret))
        {
            Settings.TgProxySecret = _tgProxy.SecretHex;
            _settingsSvc.Save();
        }
        RefreshTelegramProxyStatus();
        SimpleStatus = _tgProxy.IsRunning ? "Прокси Telegram запущен." : "Прокси Telegram остановлен.";
    }

    private bool _isCheckingTgProxy;

    /// <summary>True while the Telegram-proxy self-test runs (disables its button, swaps its label).</summary>
    public bool IsCheckingTelegramProxy
    {
        get => _isCheckingTgProxy;
        private set
        {
            if (!SetField(ref _isCheckingTgProxy, value)) return;
            OnPropertyChanged(nameof(CheckTelegramProxyButtonText));
            CheckTelegramProxyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Label for the Telegram self-test button.</summary>
    public string CheckTelegramProxyButtonText => _isCheckingTgProxy ? "Проверяю…" : "Проверить соединение";

    /// <summary>Run the built-in proxy's upstream self-test and show the verdict on the card; the
    /// step-by-step details go to the journal. Independent of the winws2 engine and needs no admin.</summary>
    private async Task CheckTelegramProxyAsync()
    {
        IsCheckingTelegramProxy = true;
        TelegramProxyStatus = "Проверяю соединение с Telegram…";
        try
        {
            TelegramProxyStatus = await _tgProxy.SelfTestAsync();
        }
        catch (Exception ex)
        {
            TelegramProxyStatus = "Проверка не удалась: " + ex.Message;
        }
        finally
        {
            IsCheckingTelegramProxy = false;
        }
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

    // ---- DPI check (does the provider actively block by SNI?) -------------
    // Distinct from the availability matrix: for each censored host it TCP-connects (proving the server
    // is up), then sends a TLS ClientHello with the real SNI — a reset (RST) or a freeze on THAT packet
    // is a middlebox injecting, not "the site is down". Most telling with the bypass OFF.

    private static readonly string[] DpiHosts =
    {
        "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "www.youtube.com",
    };

    private bool _isDpiChecking;
    public bool IsDpiChecking
    {
        get => _isDpiChecking;
        private set { if (SetField(ref _isDpiChecking, value)) RaiseCommandStates(); }
    }

    private string _dpiVerdictText =
        "«Проверка DPI» определяет, режет ли провайдер соединение по имени сайта (обрыв/заморозка), а не просто «сайт недоступен».";
    public string DpiVerdictText { get => _dpiVerdictText; private set => SetField(ref _dpiVerdictText, value); }

    private string _dpiVerdictDetail = "";
    public string DpiVerdictDetail { get => _dpiVerdictDetail; private set => SetField(ref _dpiVerdictDetail, value); }

    private DiagStatus _dpiVerdictStatus = DiagStatus.Pending;
    public DiagStatus DpiVerdictStatus { get => _dpiVerdictStatus; private set => SetField(ref _dpiVerdictStatus, value); }

    private async Task RunDpiCheckAsync()
    {
        if (IsDpiChecking) return;
        IsDpiChecking = true;
        DpiVerdictStatus = DiagStatus.Running;
        DpiVerdictText = "Проверяем, режет ли провайдер соединение по DPI…";
        DpiVerdictDetail = "";
        _dpiCts = new CancellationTokenSource();
        try
        {
            var ct = _dpiCts.Token;
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(DpiHosts.Select(async host =>
            {
                await gate.WaitAsync(ct);
                try { return (host, verdict: await NetProbe.DpiProbeAsync(host, ct)); }
                finally { gate.Release(); }
            }));

            int reset = results.Count(r => r.verdict == DpiVerdict.Reset);
            int freeze = results.Count(r => r.verdict == DpiVerdict.Freeze);
            int clean = results.Count(r => r.verdict == DpiVerdict.Clean);

            DpiVerdictDetail = string.Join("\n",
                results.Select(r => $"{DpiGlyph(r.verdict)}  {r.host} — {DpiText(r.verdict)}"));

            if (reset + freeze > 0)
            {
                DpiVerdictStatus = DiagStatus.Fail;
                string how = reset > 0 && freeze > 0 ? "обрыв (RST) и заморозка"
                           : reset > 0 ? "обрыв соединения (RST-инъекция)"
                           : "заморозка (пакеты дропаются)";
                DpiVerdictText = IsRunning
                    ? $"Провайдер режет по DPI: {how}. Обход включён, но эти хосты всё равно режутся — смените стратегию."
                    : $"Провайдер режет по DPI: {how}. Это снимается обходом — включите его.";
            }
            else if (clean > 0)
            {
                DpiVerdictStatus = DiagStatus.Ok;
                DpiVerdictText = IsRunning
                    ? "Признаков DPI-блокировки нет — либо провайдер не режет, либо обход уже её снимает."
                    : "Признаков DPI-блокировки по имени сайта не обнаружено.";
            }
            else
            {
                DpiVerdictStatus = DiagStatus.Timeout;
                DpiVerdictText = "Нет соединения с хостами — похоже на проблему сети или блокировку по IP, а не DPI по имени.";
            }
        }
        catch (OperationCanceledException) { DpiVerdictText = "Проверка DPI остановлена."; DpiVerdictStatus = DiagStatus.Pending; }
        catch (Exception ex) { DpiVerdictText = "Ошибка проверки DPI: " + ex.Message; DpiVerdictStatus = DiagStatus.Pending; }
        finally
        {
            IsDpiChecking = false;
            _dpiCts?.Dispose();
            _dpiCts = null;
        }
    }

    private static string DpiGlyph(DpiVerdict v) => v switch
    {
        DpiVerdict.Clean => "✓",
        DpiVerdict.Reset or DpiVerdict.Freeze => "✗",
        _ => "•",
    };

    private static string DpiText(DpiVerdict v) => v switch
    {
        DpiVerdict.Clean => "чисто",
        DpiVerdict.Reset => "обрыв DPI (RST)",
        DpiVerdict.Freeze => "заморозка DPI",
        _ => "нет соединения",
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

        AutoCandidates.Clear();
        foreach (var c in ComboStrategyCatalog.All) AutoCandidates.Add(new AutoCandidateRow(c.Name));
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
            var result = await _autoSelect.RunAsync(goalHosts, _autoCts.Token);
            if (result is null)
            {
                SetAutoStatus("Не удалось подобрать стратегию.");
                return;
            }

            var (strategy, score) = result.Value;
            BuildVerdicts(score.HostList);
            var preset = AutoSelectService.ToPreset(strategy, SelectedScope);
            _presets.AddUser(preset);
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name) ?? Presets.LastOrDefault();
            SetAutoStatus($"Готово: «{strategy.Name}» — {score.Detail}. Запуск.");
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
            IpsetStatus = "Определяю IP-подсети Discord…";
            var domains = _hostlists.Exists("discord")
                ? _hostlists.ReadDomains("discord")
                : new List<string> { "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "discord.media", "discordapp.net" };
            var res = await _ipset.BuildDiscordIpsetAsync(domains, CancellationToken.None);
            IpsetStatus = $"Готово: {res.Subnets} подсетей. Подключите список через {{IPSET}} в своей стратегии.";
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

    // ---- Defender / firewall exclusions -----------------------------------

    private bool _isApplyingExclusions;
    public bool IsApplyingExclusions
    {
        get => _isApplyingExclusions;
        private set { if (SetField(ref _isApplyingExclusions, value)) RaiseCommandStates(); }
    }

    private string _exclusionsStatus =
        "Добавит приложение и движок в исключения Защитника Windows и правила брандмауэра, чтобы их не блокировали.";
    public string ExclusionsStatus { get => _exclusionsStatus; private set => SetField(ref _exclusionsStatus, value); }

    private async Task ApplyExclusionsAsync()
    {
        if (IsApplyingExclusions) return;
        IsApplyingExclusions = true;
        try
        {
            ExclusionsStatus = "Добавление исключений…";
            var res = await _exclusions.ApplyAsync();
            ExclusionsStatus = (res.AllOk
                ? "Готово — всё добавлено:\n"
                : "Готово частично (что-то не удалось — нужны права администратора / сторонний антивирус):\n") + res.Summary;
        }
        catch (Exception ex)
        {
            ExclusionsStatus = "Не удалось добавить исключения: " + ex.Message;
        }
        finally
        {
            IsApplyingExclusions = false;
        }
    }

    private bool _showHowItWorks;
    /// <summary>Whether the "how it works / app tour" instruction modal is shown.</summary>
    public bool ShowHowItWorks { get => _showHowItWorks; set => SetField(ref _showHowItWorks, value); }

    /// <summary>Save a specific tried candidate as a preset and make it active.</summary>
    private void ApplyScore(AutoScore? score)
    {
        if (score?.Strategy is null) return;
        var preset = AutoSelectService.ToPreset(score.Strategy, SelectedScope);
        _presets.AddUser(preset);
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name) ?? Presets.LastOrDefault();
        SetAutoStatus($"Сохранено как стратегия «{preset.Name}». Нажмите «Запустить».");
    }

    /// <summary>Save a candidate as a preset AND start it (or restart with it if already running) —
    /// the one-click "use this one" action from the review popup.</summary>
    private async Task ApplyScoreAndStartAsync(AutoScore? score)
    {
        if (score?.Strategy is null) return;
        ApplyScore(score);
        if (IsRunning) await ApplyStrategyAsync();
        else if (CanStart) Start();
    }

    // ---- lifecycle ---------------------------------------------------------

    public async Task InitializeAsync()
    {
        ReloadPresets();
        _hostlists.SeedDefaults();
        ReloadHostlists();
        _engine.GameFilter = Settings.GameFilter;
        _engine.BypassAllSites = Settings.BypassAllSites;
        _engine.DisableQuic = Settings.DisableQuic;
        _engine.CoverTgProxy = Settings.TgProxyCoverage;

        // Built-in Telegram proxy: restore the persisted port/secret (or persist a fresh one) so the
        // tg:// link is stable across runs; the proxy itself is started on demand from the card.
        _tgProxy.Configure(Settings.TgProxyPort, Settings.TgProxySecret);
        if (string.IsNullOrEmpty(Settings.TgProxySecret))
        {
            Settings.TgProxySecret = _tgProxy.SecretHex;
            _settingsSvc.Save();
        }
        RefreshTelegramProxyStatus();
        OnPropertyChanged(nameof(TelegramProxyEndpoint));
        OnPropertyChanged(nameof(TelegramProxySecret));
        if (Settings.TgProxyAutostart) _tgProxy.Start();

        // Per-network memory: prefer the strategy that last worked on THIS network (local fingerprint),
        // then the persisted last-active preset, then the first available.
        string? fp = await Task.Run(NetworkFingerprint.Current);
        var recalledPreset = fp is not null && Settings.NetworkStrategies.TryGetValue(fp, out var rn)
            ? Presets.FirstOrDefault(p => p.Name == rn) : null;
        SelectedPreset = recalledPreset
                         ?? Presets.FirstOrDefault(p => p.Name == Settings.ActivePresetName)
                         ?? Presets.FirstOrDefault();
        if (recalledPreset is not null)
            SimpleStatus = $"Стратегия для этой сети: «{recalledPreset.Name}»";
        if (Settings.ActiveHostlist is not null && _hostlists.Exists(Settings.ActiveHostlist))
            SelectedHostlist = Settings.ActiveHostlist;
        else
            SelectedHostlist = Hostlists.FirstOrDefault();

        EngineVersion = _updater.InstalledVersion ?? "не установлен";

        // A missing/incomplete engine must be resolved before anything can run → await (it downloads).
        // A routine "is there a newer engine?" check when the engine is already present runs in the
        // BACKGROUND so a slow or blocked GitHub request never delays the rest of startup — EXCEPT when
        // we're about to auto-start the engine, where the old await ordering is kept so an install can't
        // churn the launch (CheckAndUpdateAsync would otherwise stop→update→restart it mid-startup).
        if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete
            || (Settings.AutoUpdateEngine && Settings.AutostartEngine))
            await CheckAndUpdateAsync(silent: true);
        else if (Settings.AutoUpdateEngine)
            _ = CheckAndUpdateAsync(silent: true);

        if (Settings.AutostartEngine && CanStart && SelectedPreset is not null)
            Start();

        _ = CheckAppUpdateAsync(); // notify (don't block startup) if a newer ZapretUI release exists
    }

    // ---- app self-update notification -------------------------------------

    /// <summary>This app's version, shown in the caption bar (e.g. "v0.1.0").</summary>
    public string AppVersion => "v" + UpdaterService.AppVersion;

    private bool _appUpdateAvailable;
    public bool AppUpdateAvailable { get => _appUpdateAvailable; private set => SetField(ref _appUpdateAvailable, value); }

    private string _appUpdateText = "";
    public string AppUpdateText { get => _appUpdateText; private set => SetField(ref _appUpdateText, value); }

    private string _appLatestUrl = "https://github.com/Asterlike/zapret2UI/releases/latest";

    private async Task CheckAppUpdateAsync()
    {
        var latest = await _updater.FetchAppLatestAsync(CancellationToken.None);
        if (latest is null || !UpdaterService.IsAppUpdate(latest.Value.Tag)) return;
        // Show the clean numeric version (e.g. "0.3.0"), not the raw tag ("Zapret2UI-0.3.0").
        string ver = UpdaterService.ParseTagVersion(latest.Value.Tag)?.ToString() ?? latest.Value.Tag;
        OnUi(() =>
        {
            _appLatestUrl = latest.Value.Url;
            AppUpdateText = $"Новая версия {ver} — скачать";
            AppUpdateAvailable = true;
            Notify?.Invoke("Доступно обновление",
                $"Вышла новая версия Zapret2UI {ver}. Откройте страницу релиза, чтобы скачать.");
        });
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }

    private void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); SimpleStatus = "Ссылка на прокси скопирована."; }
        catch { /* clipboard busy — ignore */ }
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

    // ---- custom targets (Диагностика tab) ---------------------------------

    private bool _showTargetPopup;
    public bool ShowTargetPopup { get => _showTargetPopup; set => SetField(ref _showTargetPopup, value); }

    private bool _targetIsNew = true;
    public bool TargetIsNew { get => _targetIsNew; private set => SetField(ref _targetIsNew, value); }

    private string _targetRootInput = "";
    public string TargetRootInput { get => _targetRootInput; set => SetField(ref _targetRootInput, value); }

    private string _targetDomainsText = "";
    public string TargetDomainsText { get => _targetDomainsText; set => SetField(ref _targetDomainsText, value); }

    private string _targetStatus = "";
    public string TargetStatus { get => _targetStatus; private set => SetField(ref _targetStatus, value); }

    private bool _isExpandingTarget;
    public bool IsExpandingTarget
    {
        get => _isExpandingTarget;
        private set
        {
            if (!SetField(ref _isExpandingTarget, value)) return;
            ExpandTargetCommand.RaiseCanExecuteChanged();
            SaveTargetCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _editingTargetName;

    private void ReloadTargets()
    {
        CustomTargets.Clear();
        foreach (var t in _targets.GetTargets()) CustomTargets.Add(t);
    }

    /// <summary>Diagnostics rows = the built-in service matrix + a "Свои цели" group (capped).</summary>
    private void RebuildDiagRows()
    {
        DiagRows.Clear();
        foreach (var r in DiagnosticsService.BuildRows()) DiagRows.Add(r);
        foreach (var d in _targets.AllDomains().Take(30))
            DiagRows.Add(new DiagRow { Group = "Свои цели", Name = d, Host = d });
    }

    private void OpenTargetPopup(CustomTarget? existing)
    {
        if (existing is null)
        {
            _editingTargetName = null;
            TargetIsNew = true;
            TargetRootInput = "";
            TargetDomainsText = "";
            TargetStatus = "Введите домен (например yandex.ru) и нажмите «Найти домены».";
        }
        else
        {
            _editingTargetName = existing.Name;
            TargetIsNew = false;
            TargetRootInput = existing.Name;
            TargetDomainsText = string.Join('\n', _targets.ReadDomains(existing.Name));
            TargetStatus = $"{existing.DomainCount} домен(ов). Можно отредактировать список или найти ещё.";
        }
        ShowTargetPopup = true;
    }

    private async Task ExpandTargetAsync()
    {
        string root = TargetService.Normalize(TargetRootInput);
        if (root.Length == 0) { TargetStatus = "Сначала укажите корневой домен."; return; }
        IsExpandingTarget = true;
        TargetStatus = $"Ищу домены для «{root}»…";
        try
        {
            // Progress<T> marshals back to this (UI) thread, so live status updates are safe to bind.
            var progress = new Progress<string>(msg => TargetStatus = msg);
            var found = await _targets.ExpandAsync(root, 40, progress, CancellationToken.None);
            var have = TargetDomainsText.Split('\n').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0);
            var merged = have.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            TargetDomainsText = string.Join('\n', merged);
            TargetStatus = $"Найдено: {found.Count}. Всего в списке: {merged.Count}. Проверьте и сохраните.";
        }
        catch (Exception ex) { TargetStatus = "Не удалось получить домены: " + ex.Message; }
        finally { IsExpandingTarget = false; }
    }

    private void SaveTarget()
    {
        string root = TargetService.Normalize(TargetRootInput);
        if (root.Length == 0) { TargetStatus = "Укажите корневой домен."; return; }
        var domains = TargetDomainsText.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (domains.Count == 0) domains.Add(root);

        // root renamed → drop the previous file so it isn't orphaned
        if (_editingTargetName is not null &&
            !string.Equals(_editingTargetName, root, StringComparison.OrdinalIgnoreCase))
            _targets.Delete(_editingTargetName);

        _targets.Save(root, domains);
        _editingTargetName = root;
        TargetIsNew = false;
        ReloadTargets();
        RebuildDiagRows();
        ShowTargetPopup = false;
        Notify?.Invoke("Цель сохранена",
            $"«{root}»: {domains.Count} домен(ов). Учитывается в диагностике, подборе и обходе.");
        if (IsRunning) _ = ApplyStrategyAsync(); // relaunch so the bypass covers the new domains
    }

    private void DeleteTarget(CustomTarget? t)
    {
        if (t is null) return;
        _targets.Delete(t.Name);
        ReloadTargets();
        RebuildDiagRows();
        if (IsRunning) _ = ApplyStrategyAsync();
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
                    // Wait for winws2 to FULLY exit before overwriting engine files (a File.Copy over a
                    // running winws2.exe throws) and before CanStart lets us relaunch — poll instead of a
                    // fixed delay, like ApplyStrategyAsync does.
                    for (int i = 0; i < 60 && State != EngineState.Stopped; i++)
                        await Task.Delay(50);
                    await Task.Delay(200);
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
        if (!ConfirmDialog.Show("Удалить список?",
                $"Список доменов «{SelectedHostlist}» будет удалён без возможности восстановления."))
            return;
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
        if (!ConfirmDialog.Show("Удалить пресет?",
                $"Пресет «{p.Name}» будет удалён без возможности восстановления."))
            return;
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
        try { _dpiCts?.Cancel(); } catch { }
        try { _autoCts?.Cancel(); } catch { }
        try { _genCts?.Cancel(); } catch { }
        try { _monitor.Stop(); } catch { }
        try { _autoSelect.Dispose(); } catch { }
        try { _generator.Dispose(); } catch { }
        try { _tgProxy.Stop(); } catch { }
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

    private void AppendProxyLog(string line)
    {
        OnUi(() =>
        {
            ProxyLogLines.Add(line);
            while (ProxyLogLines.Count > MaxLogLines) ProxyLogLines.RemoveAt(0);
        });
    }

    /// <summary>Copy a whole log to the clipboard (the «Копировать» button on the Журнал tab). Joined
    /// with newlines so it pastes as plain text — the support workflow of "скопируйте последние строки".</summary>
    private void CopyLinesToClipboard(IEnumerable<string> lines, string what)
    {
        string text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) { SimpleStatus = $"{what}: пусто, нечего копировать."; return; }
        try { Clipboard.SetText(text); SimpleStatus = $"{what} скопирован в буфер обмена."; }
        catch { /* clipboard busy — ignore */ }
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
        RunDpiCheckCommand.RaiseCanExecuteChanged();
        StopDpiCheckCommand.RaiseCanExecuteChanged();
        RunAutoSelectCommand.RaiseCanExecuteChanged();
        StopAutoSelectCommand.RaiseCanExecuteChanged();
        GenerateStrategyCommand.RaiseCanExecuteChanged();
        BuildDiscordIpsetCommand.RaiseCanExecuteChanged();
        ApplyExclusionsCommand.RaiseCanExecuteChanged();
        HomeToggleCommand.RaiseCanExecuteChanged();
        ApplyScoreAndStartCommand.RaiseCanExecuteChanged();
    }

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.Invoke(a);
    }
}
