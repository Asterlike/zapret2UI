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

    public AppSettings Settings => _settingsSvc.Settings;

    public MainViewModel()
    {
        _engine.StateChanged += s => OnUi(() => State = s);
        _engine.LogLine += AppendLog;

        StartCommand = new RelayCommand(_ => Start(), _ => CanStart);
        StopCommand = new RelayCommand(_ => _engine.Stop(), _ => CanStop);
        ToggleCommand = new RelayCommand(_ => { if (IsRunning) _engine.Stop(); else Start(); },
                                         _ => !IsUpdating);
        CheckUpdateCommand = new RelayCommand(async _ => await CheckAndUpdateAsync(silent: false),
                                              _ => !IsUpdating);
        ClearLogCommand = new RelayCommand(_ => LogLines.Clear());

        NewHostlistCommand = new RelayCommand(_ => NewHostlist());
        DeleteHostlistCommand = new RelayCommand(_ => DeleteHostlist(), _ => SelectedHostlist is not null);
        SaveHostlistCommand = new RelayCommand(_ => SaveHostlist(), _ => SelectedHostlist is not null);
        AddDomainCommand = new RelayCommand(_ => AddDomain(), _ => SelectedHostlist is not null);

        DuplicatePresetCommand = new RelayCommand(_ => DuplicatePreset(), _ => SelectedPreset is not null);
        DeletePresetCommand = new RelayCommand(_ => DeletePreset(),
                                               _ => SelectedPreset is { IsBuiltIn: false });
        SavePresetCommand = new RelayCommand(_ => SavePreset(),
                                             _ => SelectedPreset is { IsBuiltIn: false });
    }

    // ---- collections -------------------------------------------------------

    public ObservableCollection<Preset> Presets { get; } = new();
    public ObservableCollection<string> Hostlists { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

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
    public RelayCommand DuplicatePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }

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
                RaiseCommandStates();
            }
        }
    }

    public bool SelectedPresetEditable => SelectedPreset is { IsBuiltIn: false };

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
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            MessageBox.Show(ex.Message, "Не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
                await _updater.InstallAsync(latest, progress);
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
        DuplicatePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
    }

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.Invoke(a);
    }
}
