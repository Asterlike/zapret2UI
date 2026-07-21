using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Engine state and the start/stop actions that drive winws2.
/// </summary>
public sealed partial class MainViewModel
{
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

    // Engine state for the manual environment check (Настройки → «Проверить окружение»), which lives
    // in the view: showing a dialog is a view concern, but only the VM owns the updater.
    public bool IsEngineInstalled => _updater.IsEngineInstalled;
    public bool IsEngineComplete => _updater.IsEngineComplete;

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

}
