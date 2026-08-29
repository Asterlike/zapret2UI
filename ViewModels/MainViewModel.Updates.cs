using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Engine updates and the app's own update notice — both ask GitHub what the newest release is,
/// and neither ever installs anything without being told to.
/// </summary>
public sealed partial class MainViewModel
{
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
}
