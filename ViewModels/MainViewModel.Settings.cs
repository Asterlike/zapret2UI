using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Localization;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Settings toggles, UI scale, mode/tab navigation, updates, ipset and exclusions.
/// </summary>
public sealed partial class MainViewModel
{
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

    /// <summary>True only while the engine zip is actually being fetched/installed. The progress bar
    /// binds to THIS, not to <see cref="IsUpdating"/>: a routine "is there a newer engine?" check is
    /// also an update operation, and showing a progress bar (holding its previous value, no less) for
    /// a plain network check made it look like the engine re-downloads on every launch.</summary>
    private bool _isDownloadingEngine;
    public bool IsDownloadingEngine { get => _isDownloadingEngine; private set => SetField(ref _isDownloadingEngine, value); }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    private string _engineVersion = "—";
    public string EngineVersion { get => _engineVersion; private set => SetField(ref _engineVersion, value); }

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

    /// <summary>Verbose engine log (<c>--debug=1</c>) toggled from the Журнал tab. Relaunches a running
    /// engine, because winws2 only reads the flag at startup — without the relaunch the switch would
    /// look like it did nothing until the next manual restart.</summary>
    public bool DebugLog
    {
        get => Settings.DebugLog;
        set
        {
            if (value == Settings.DebugLog) return;
            Settings.DebugLog = value;
            _settingsSvc.Save();
            _engine.DebugLog = value;
            _tgProxy.Verbose = value; // the same switch governs both panes of the Журнал tab
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning)
            {
                AppendLog(value
                    ? Loc.T("Подробный режим включён (--debug=1), перезапуск движка…")
                    : Loc.T("Подробный режим выключен, перезапуск движка…"));
                _ = ApplyStrategyAsync(); // relaunch so the new log level applies
            }
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
        Notify?.Invoke("Zapret UI", Loc.T("Обход упал — переподбор…"));
        AppendLog(Loc.T("Авто-починка: обход не отвечает, переподбор."));
        await RunAutoSelectAsync(showWindow: false);
        if (IsRunning)
            Notify?.Invoke("Zapret UI", Loc.T("Обход восстановлен."));
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

    // ---- UI language (restart-to-apply) -----------------------------------
    // The RU|EN switch on Главная and Настройки binds these two read-only flags (so the active side is
    // filled) and calls SetLanguageCommand on click. Reflects the language THIS run was started in — it
    // only changes after the restart below.

    public bool LanguageIsRussian => !Loc.IsEnglish;
    public bool LanguageIsEnglish => Loc.IsEnglish;

    /// <summary>Switch the UI language. No-op if already active; otherwise confirm (it restarts the app,
    /// stopping any running bypass/proxy), persist, and relaunch — the strings resolve at parse time.</summary>
    private void SetLanguage(string? lang)
    {
        string norm = lang == Loc.English ? Loc.English : Loc.Russian;
        if (Settings.Language == norm) return;

        bool ok = ConfirmDialog.Show(
            Loc.T("Сменить язык интерфейса?"),
            Loc.T("Приложение перезапустится. Если обход или прокси запущены — они остановятся."),
            confirmText: Loc.T("Перезапустить"),
            danger: false);
        if (!ok)
        {
            // The clicked RadioButton toggled itself before the dialog; re-assert the bound state so the
            // active language stays highlighted after a cancel.
            OnUi(() =>
            {
                OnPropertyChanged(nameof(LanguageIsRussian));
                OnPropertyChanged(nameof(LanguageIsEnglish));
            });
            return;
        }

        Settings.Language = norm;
        _settingsSvc.Save();
        App.RelaunchSelf();
    }

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
        if (preset is null) { SimpleStatus = Loc.T("Движок ещё не установлен — дождитесь загрузки."); return; }
        SelectedPreset = preset;
        SimpleStatus = Loc.T("Стратегия: «{0}»", Loc.T(preset.Name));
        Start();
    }

    // ---- ipset (IP-based bypass) ------------------------------------------

    private bool _isBuildingIpset;
    public bool IsBuildingIpset
    {
        get => _isBuildingIpset;
        private set { if (SetField(ref _isBuildingIpset, value)) RaiseCommandStates(); }
    }

    private string _ipsetStatus = Loc.T("Соберите список IP Discord, чтобы включить обход по IP (для жёстких блоков).");
    public string IpsetStatus { get => _ipsetStatus; private set => SetField(ref _ipsetStatus, value); }

    // Static help texts that contain the literal token {IPSET}; the brace can't be wrapped in the XAML
    // {loc:Loc} markup extension, so they route through Loc.T here (which never reformats — the token
    // survives verbatim).
    public string IpsetHelpDiag => Loc.T(
        "Если Discord режут по IP-адресам, обход по доменам не помогает. Соберите актуальные подсети "
        + "Discord (резолв доменов) — список сохранится в ipset-discord.txt, его можно подключить в своём "
        + "пресете через токен {IPSET}.");

    public string IpsetHelpTelegram => Loc.T(
        "• Обход по IP — если ресурс режут по адресам, а не доменам: соберите подсети Discord "
        + "(резолв доменов), список подключается через токен {IPSET}.");

    private async Task BuildIpsetAsync()
    {
        if (IsBuildingIpset) return;
        IsBuildingIpset = true;
        try
        {
            IpsetStatus = Loc.T("Определяю IP-подсети Discord…");
            var domains = _hostlists.Exists("discord")
                ? _hostlists.ReadDomains("discord")
                : new List<string> { "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "discord.media", "discordapp.net" };
            var res = await _ipset.BuildDiscordIpsetAsync(domains, CancellationToken.None);
            IpsetStatus = Loc.T("Готово: {0} подсетей. Подключите список через {{IPSET}} в своей стратегии.", res.Subnets);
            OnPropertyChanged(nameof(CommandPreview));
        }
        catch (Exception ex)
        {
            IpsetStatus = Loc.T("Не удалось собрать IP-список: ") + ex.Message;
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
            ExclusionsStatus = Loc.T("Добавление исключений…");
            var res = await _exclusions.ApplyAsync();
            ExclusionsStatus = (res.AllOk
                ? Loc.T("Готово — всё добавлено:\n")
                : Loc.T("Готово частично (что-то не удалось — нужны права администратора / сторонний антивирус):\n")) + res.Summary;
        }
        catch (Exception ex)
        {
            ExclusionsStatus = Loc.T("Не удалось добавить исключения: ") + ex.Message;
        }
        finally
        {
            IsApplyingExclusions = false;
        }
    }

    private bool _showHowItWorks;
    /// <summary>Whether the "how it works / app tour" instruction modal is shown.</summary>
    public bool ShowHowItWorks { get => _showHowItWorks; set => SetField(ref _showHowItWorks, value); }

    /// <summary>How long the confirm button is held on the very first launch, so the steps get read
    /// instead of the modal being dismissed reflexively.</summary>
    private const int WelcomeDelaySeconds = 6;

    private bool _showWelcome;
    /// <summary>Whether the first-run walkthrough is shown. Opened once on a fresh install (see
    /// <see cref="MarkWelcomeSeen"/>) and on demand from Настройки → «Показать вводную».</summary>
    public bool ShowWelcome { get => _showWelcome; set => SetField(ref _showWelcome, value); }

    private int _welcomeCountdown;
    /// <summary>Seconds left before the walkthrough can be dismissed; 0 = dismissible right away.</summary>
    public int WelcomeCountdown
    {
        get => _welcomeCountdown;
        private set
        {
            if (!SetField(ref _welcomeCountdown, value)) return;
            OnPropertyChanged(nameof(WelcomeButtonText));
            CloseWelcomeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Confirm-button caption, carrying the countdown while it runs.</summary>
    public string WelcomeButtonText =>
        WelcomeCountdown > 0 ? Loc.T("Понятно, начать ({0})", WelcomeCountdown) : Loc.T("Понятно, начать");

    /// <summary>True until the first-run walkthrough has been dismissed once.</summary>
    public bool NeedsWelcome => !Settings.WelcomeShown;

    /// <summary>
    /// Show the walkthrough. <paramref name="withCountdown"/> only on the genuine first launch —
    /// someone reopening it from Настройки has already been through it and shouldn't be made to wait.
    /// </summary>
    public void OpenWelcome(bool withCountdown)
    {
        ShowWelcome = true;
        WelcomeCountdown = 0;
        if (withCountdown) _ = RunWelcomeCountdownAsync();
    }

    /// <summary>Tick the confirm button's countdown down to zero (UI thread; stops if closed early).</summary>
    private async Task RunWelcomeCountdownAsync()
    {
        WelcomeCountdown = WelcomeDelaySeconds;
        while (WelcomeCountdown > 0)
        {
            await Task.Delay(1000);
            if (!ShowWelcome) { WelcomeCountdown = 0; return; }
            WelcomeCountdown--;
        }
    }

    /// <summary>Remember that the walkthrough was seen, so it doesn't reopen on every launch.</summary>
    private void MarkWelcomeSeen()
    {
        ShowWelcome = false;
        WelcomeCountdown = 0;
        if (Settings.WelcomeShown) return;
        Settings.WelcomeShown = true;
        _settingsSvc.Save();
    }

    /// <summary>Save a specific tried candidate as a preset and make it active.</summary>
    private void ApplyScore(AutoScore? score)
    {
        if (score?.Strategy is null) return;
        var preset = SaveOrSelectAutoWinner(score.Strategy);
        SetAutoStatus(Loc.T("Сохранено как стратегия «{0}». Нажмите «Запустить».", Loc.T(preset.Name)));
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
            AppUpdateText = Loc.T("Новая версия {0} — скачать", ver);
            AppUpdateAvailable = true;
            Notify?.Invoke(Loc.T("Доступно обновление"),
                Loc.T("Вышла новая версия Zapret2UI {0}. Откройте страницу релиза, чтобы скачать.", ver));
        });
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }

    private void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); SimpleStatus = Loc.T("Ссылка на прокси скопирована."); }
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

}
