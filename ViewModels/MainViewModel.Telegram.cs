using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Built-in Telegram MTProto proxy (native tg-ws-proxy).
/// </summary>
public sealed partial class MainViewModel
{
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

}
