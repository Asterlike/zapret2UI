using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;
using Zapret2UI.Views;

namespace Zapret2UI.ViewModels;

/// <summary>
/// The action cards at the bottom of Настройки. Unlike the toggles above them each of these DOES
/// something — builds an IP list, touches Defender, rewrites our own files — so every one of them
/// reports what happened in its own status line.
/// </summary>
public sealed partial class MainViewModel
{
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

    // ---- reset to defaults ------------------------------------------------

    /// <summary>Return every setting on the Настройки screen to its default. Keeps the user's strategies,
    /// host lists, interface language, view mode, current selection, the Telegram-proxy link and the
    /// per-network memory (see <see cref="SettingsService.ResetToDefaults"/>). A running bypass/proxy is
    /// stopped first, since the reset switches their toggles off; the engine/proxy state that a plain
    /// settings write wouldn't touch is re-applied by hand here.</summary>
    private void ResetSettings()
    {
        bool ok = ConfirmDialog.Show(
            Loc.T("Сбросить настройки?"),
            Loc.T("Настройки вернутся к значениям по умолчанию. Стратегии, хостлисты, язык и ссылка "
                + "Telegram-прокси сохранятся. Если обход или прокси запущены — они остановятся."),
            confirmText: Loc.T("Сбросить"),
            danger: true);
        if (!ok) return;

        bool wasAutostart = Settings.Autostart;
        if (IsRunning) _engine.Stop();
        if (_tgProxy.IsRunning) _tgProxy.Stop();
        // Windows' proxy setting is ours to put back while we still remember what it was — the reset
        // switches the toggle that drove it off, and the backup would then have nothing to explain it.
        RestoreStaleSystemProxy();

        _settingsSvc.ResetToDefaults();

        // Re-apply the side effects a bare settings write never carries: the scheduled autostart task,
        // the watchdog, the engine's live flags and the proxy's port binding.
        if (wasAutostart) _autostart.Disable();
        _monitor.Stop();
        _engine.GameFilter = Settings.GameFilter;
        _engine.BypassAllSites = EffectiveBypassAllSites;
        _engine.DisableQuic = Settings.DisableQuic;
        _engine.CoverTgProxy = Settings.TgProxyCoverage;
        _engine.DebugLog = Settings.DebugLog;
        _tgProxy.Verbose = Settings.DebugLog;
        _tgProxy.Configure(Settings.TgProxyPort, Settings.TgProxySecret);

        // Every bound setting changed at once — one blanket notify refreshes them all (WPF re-reads every
        // getter on an empty property name), then the proxy card and command states are brought in line.
        OnPropertyChanged(string.Empty);
        RefreshTelegramProxyStatus();
        RaiseCommandStates();

        AppendLog(Loc.T("Настройки сброшены к значениям по умолчанию."));
        Notify?.Invoke("Zapret2UI", Loc.T("Настройки сброшены к значениям по умолчанию."));
    }

    // ---- backup / restore -------------------------------------------------

    private string _backupStatus = Loc.T("Сохраните настройки и стратегии в файл — на случай переустановки или переноса на другой компьютер.");
    public string BackupStatus { get => _backupStatus; private set => SetField(ref _backupStatus, value); }

    /// <summary>Save the current configuration (settings, strategies, host lists) to a .z2bak file.</summary>
    private void ExportBackup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _backup.DefaultFileName,
            Filter = Loc.T("Резервная копия Zapret2UI") + " (*.z2bak)|*.z2bak",
            DefaultExt = ".z2bak",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _backup.Export(dlg.FileName);
            BackupStatus = Loc.T("Сохранено: {0}", System.IO.Path.GetFileName(dlg.FileName));
            Notify?.Invoke("Zapret2UI", Loc.T("Резервная копия сохранена."));
        }
        catch (Exception ex)
        {
            BackupStatus = Loc.T("Не удалось сохранить копию: ") + ex.Message;
        }
    }

    /// <summary>Restore a .z2bak: validate it, confirm, then overwrite the config and relaunch so the
    /// restored files are what loads (see <see cref="BackupService.Restore"/> for why the restart matters).</summary>
    private void ImportBackup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("Резервная копия Zapret2UI") + " (*.z2bak)|*.z2bak|" + Loc.T("Все файлы") + " (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        if (!_backup.IsBackup(dlg.FileName))
        {
            BackupStatus = Loc.T("Это не резервная копия Zapret2UI.");
            return;
        }
        bool ok = ConfirmDialog.Show(
            Loc.T("Восстановить из копии?"),
            Loc.T("Текущие настройки, стратегии и хостлисты будут заменены содержимым файла. Приложение "
                + "перезапустится; запущенный обход или прокси остановятся."),
            confirmText: Loc.T("Восстановить"),
            danger: true);
        if (!ok) return;
        try
        {
            if (IsRunning) _engine.Stop();
            if (_tgProxy.IsRunning) _tgProxy.Stop();
            _backup.Restore(dlg.FileName);
            App.RelaunchSelf();
        }
        catch (Exception ex)
        {
            BackupStatus = Loc.T("Не удалось восстановить: ") + ex.Message;
        }
    }

    // ---- log files --------------------------------------------------------

    private string _logFilesStatus = Loc.T("Журнал каждого запуска пишется в отдельный файл. Старые удаляются сами — остаются последние 20. Можно очистить вручную.");
    public string LogFilesStatus { get => _logFilesStatus; private set => SetField(ref _logFilesStatus, value); }

    /// <summary>Delete the accumulated engine-*.log files (the live session's own log, if any, is locked
    /// and skipped). The in-memory Журнал is left as is — this only touches files on disk.</summary>
    private void ClearLogFiles()
    {
        int n = LogMaintenance.ClearSessionLogs();
        LogFilesStatus = n > 0
            ? Loc.T("Удалено файлов логов: {0}.", n)
            : Loc.T("Старых логов нет — чистить нечего.");
    }
}
