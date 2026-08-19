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
/// Strategy list (built-in + user) and hostlist management.
/// </summary>
public sealed partial class MainViewModel
{
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
                OnPropertyChanged(nameof(SelectedPresetOrHint));
                OnPropertyChanged(nameof(ApplyPillText));
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
                OnPropertyChanged(nameof(RunningPresetLine));
                OnPropertyChanged(nameof(IsStrategyChangePending));
                OnPropertyChanged(nameof(RunStatusText));
                RaiseCommandStates();
            }
        }
    }

    public string RunningPresetName => RunningPreset?.Name ?? "—";

    // Localized text for XAML spots that can't use the {loc:Loc} markup extension directly: a binding
    // StringFormat / FallbackValue carrying Russian, or a literal that contains a brace ({0}, {IPSET}).
    // These route through Loc.T instead. Change-notified alongside the values they depend on.

    /// <summary>Caption-bar apply pill: "Применить: &lt;preset&gt;". The name is translated too.</summary>
    public string ApplyPillText => Loc.T("Применить: {0}", Loc.T(SelectedPreset?.Name ?? ""));

    /// <summary>Selected preset name (translated for display), or the hint shown when none is selected.</summary>
    public string SelectedPresetOrHint =>
        SelectedPreset is { } p ? Loc.T(p.Name) : Loc.T("Выберите пресет");

    /// <summary>Running-strategy line under the state badge on the Стратегии tab.</summary>
    public string RunningPresetLine =>
        Loc.T("Сейчас включён: {0}. Смена стратегии требует перезапуска движка.",
              RunningPreset is { } rp ? Loc.T(rp.Name) : RunningPresetName);

    /// <summary>Selected hostlist name, or the hint shown when none is selected.</summary>
    public string SelectedHostlistOrHint => SelectedHostlist ?? Loc.T("Выберите или создайте список");

    /// <summary>True when the engine runs one preset but the user has selected a different one.</summary>
    public bool IsStrategyChangePending =>
        IsRunning && RunningPreset is not null && SelectedPreset is not null
        && !ReferenceEquals(RunningPreset, SelectedPreset);

    /// <summary>Sub-line under the state badge: what is ENABLED (running), not just selected.</summary>
    public string RunStatusText =>
        IsRunning
            ? Loc.T("Включён: {0}", Loc.T(RunningPresetName))
            : SelectedPreset is null ? Loc.T("пресет не выбран") : Loc.T("Выбран: {0}", Loc.T(SelectedPreset.Name));

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
                                               Settings.BypassAllSites, Settings.DisableQuic, Settings.TgProxyCoverage,
                                               Settings.DebugLog);

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
                OnPropertyChanged(nameof(SelectedHostlistOrHint));
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

}
