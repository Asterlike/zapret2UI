using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
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
        // Reopened from Настройки → no countdown; the first-run path passes true (MainWindow.OnLoaded).
        OpenWelcomeCommand = new RelayCommand(_ => OpenWelcome(withCountdown: false));
        CloseWelcomeCommand = new RelayCommand(_ => MarkWelcomeSeen(), _ => WelcomeCountdown == 0);
        // "Подробнее" in the walkthrough → hand off to the full reference modal.
        WelcomeToHowItWorksCommand = new RelayCommand(_ => { MarkWelcomeSeen(); ShowHowItWorks = true; });
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
    public RelayCommand OpenWelcomeCommand { get; }
    public RelayCommand CloseWelcomeCommand { get; }
    public RelayCommand WelcomeToHowItWorksCommand { get; }
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

    // ---- helpers -----------------------------------------------------------

    // Journal lines carry the wall-clock time they were PRODUCED (stamped before the OnUi post, which
    // is asynchronous now — stamping inside the lambda would record dispatch time instead). Without it
    // a line like "соединение закрыто через 383,8с" is unverifiable: the journal accumulates from app
    // start (the proxy keeps running while the window is in the tray), so durations legitimately exceed
    // how long the window has been open.
    private void AppendLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        OnUi(() =>
        {
            LogLines.Add(stamped);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });
    }

    private void AppendProxyLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        OnUi(() =>
        {
            ProxyLogLines.Add(stamped);
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

    /// <summary>Marshal an action onto the UI thread. Cross-thread calls POST asynchronously
    /// (BeginInvoke) instead of blocking: a synchronous Invoke from a background thread that also holds
    /// a lock the UI thread wants (EngineService._lock in OnProcessExited) deadlocks the whole UI — the
    /// "app freezes, kill it in Task Manager" bug. On the UI thread it still runs inline.</summary>
    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }
}
