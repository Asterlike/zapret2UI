using Zapret2UI.Localization;
using Zapret2UI.Services.Network;
using Zapret2UI.Views;

namespace Zapret2UI.ViewModels;

/// <summary>
/// The toggles, the UI scale and the language — everything that is simply a value in settings.json.
/// </summary>
public sealed partial class MainViewModel
{
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
            if (IsRunning) _ = ApplyEngineOptionsAsync(); // relaunch so the new capture width applies
        }
    }

    /// <summary>Bypass every site (catch-all) vs allow-list (default off, like Flowseal). Off keeps
    /// games/apps not in any list untouched. Relaunches a running engine so it takes effect now.
    ///
    /// <para>This is the user's answer, not necessarily the engine's: while the WARP proxy is up the
    /// engine runs wide regardless — see <see cref="EffectiveBypassAllSites"/>. Turning the preference
    /// off in that state records it and leaves the engine alone, because restarting it would change
    /// nothing.</para></summary>
    public bool BypassAllSites
    {
        get => Settings.BypassAllSites;
        set
        {
            if (value == Settings.BypassAllSites) return;
            bool wasEffective = EffectiveBypassAllSites;
            Settings.BypassAllSites = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            if (EffectiveBypassAllSites == wasEffective) return;
            _engine.BypassAllSites = EffectiveBypassAllSites;
            OnPropertyChanged(nameof(CommandPreview));
            if (IsRunning) _ = ApplyEngineOptionsAsync(); // relaunch so the new scope applies
        }
    }

    /// <summary>The scope the engine actually runs with: the user's <see cref="BypassAllSites"/>, or
    /// every site while the WARP proxy is up.
    ///
    /// <para>WARP is not a tunnel that swallows the machine's traffic — it is a SOCKS proxy on
    /// loopback, and only the applications pointed at it use it. What needs the bypass is therefore
    /// one connection: usque's own link to Cloudflare's MASQUE entry point. Measured on a censored
    /// Russian ISP, that link stood up only with the scope widened to every site — i.e. only when the
    /// strategy's full pipeline was the one handling it, rather than a narrower profile aimed at those
    /// addresses. So WARP raises the scope for as long as it is on, and the saved preference is left
    /// untouched: a setting silently rewritten by another tab is a setting the user no longer
    /// owns.</para></summary>
    private bool EffectiveBypassAllSites => Settings.BypassAllSites || IsMasqueOn;

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
            if (IsRunning) _ = ApplyEngineOptionsAsync(); // relaunch so QUIC handling changes now
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
                _ = ApplyEngineOptionsAsync(); // relaunch so the new log level applies
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
            if (IsRunning) _ = ApplyEngineOptionsAsync(); // relaunch so coverage applies now
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
}
