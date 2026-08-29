using Zapret2UI.Localization;
using Zapret2UI.Models;
using Zapret2UI.Services.Strategies;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Where the user is: simple or advanced, which tab, and the first-run walkthrough.
/// </summary>
public sealed partial class MainViewModel
{
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

    // Tab order: Главная, Стратегии, Хостлисты, Диагностика, Журнал, Telegram, WARP, Настройки.
    // These indexes and the TabItem order in MainWindow.xaml must agree: inserting a tab means bumping
    // every index after it, or the Home shortcuts land on the wrong page.

    /// <summary>Index of the WARP tab.</summary>
    internal const int WarpTabIndex = 6;

    /// <summary>Index of the Настройки tab.</summary>
    internal const int SettingsTabIndex = 7;

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

    // ---- first-run walkthrough --------------------------------------------

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
}
