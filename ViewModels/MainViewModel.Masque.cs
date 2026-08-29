using System.Windows;
using Zapret2UI.Localization;
using Zapret2UI.Mvvm;
using Zapret2UI.Services.Warp;

namespace Zapret2UI.ViewModels;

/// <summary>
/// The WARP tab: Cloudflare WARP reached over MASQUE and offered as a local SOCKS5 proxy.
///
/// <para>This replaced a WireGuard implementation that could not work on the network it was written
/// for — the handshake completed and the transport stream was then cut, which no desync can repair. It
/// is also a much smaller thing to own: no adapter, no routes, no kill switch, no administrator, so a
/// failure here cannot take the machine off the network. That was the old design's worst outcome and it
/// is now structurally impossible.</para>
///
/// <para><b>What it does and does not do.</b> Free WARP is anycast and lands on the nearest edge, so
/// from a censored country the exit is usually inside that same country — measured here: Russia, and the
/// address is published as «Cloudflare WARP» with a proxy flag on it. It changes the address, which is
/// enough to get past blocks made BY address. It does not lift geo-blocks, and the tab says so rather
/// than letting the user find out.</para>
/// </summary>
public partial class MainViewModel
{
    private readonly MasqueService _masque = new();

    private bool _isMasqueOn;
    private bool _isMasqueBusy;
    private string _masqueStatus = "";

    private void InitMasqueCommands()
    {
        _masque.LogLine += AppendLog;

        RegisterMasqueCommand = new RelayCommand(
            async _ => await RegisterMasqueAsync(), _ => !IsMasqueBusy && !IsMasqueRegistered);
        ResetMasqueCommand = new RelayCommand(
            async _ => await ResetMasqueAsync(), _ => !IsMasqueBusy && IsMasqueRegistered);
        CopyProxyAddressCommand = new RelayCommand(
            _ => CopyProxyAddress(), _ => IsMasqueOn);
    }

    private void InitMasqueState()
    {
        MasqueStatus = IsMasqueRegistered
            ? Loc.T("Устройство готово. Включите прокси.")
            : Loc.T("Устройство ещё не создано.");
        NotifyMasqueState();
    }

    // ---- bindings ----------------------------------------------------------

    /// <summary>The switch. Setting it starts or stops the proxy; the property itself is put back from
    /// the real state afterwards, so a failed start shows as off rather than lying.</summary>
    public bool IsMasqueEnabled
    {
        get => _isMasqueOn;
        set { if (value != _isMasqueOn) _ = ToggleMasqueAsync(value); }
    }

    public bool CanToggleMasque => !IsMasqueBusy && IsMasqueRegistered;

    public bool IsMasqueBusy
    {
        get => _isMasqueBusy;
        private set { _isMasqueBusy = value; OnPropertyChanged(); NotifyMasqueState(); }
    }

    public static bool IsMasqueRegistered => MasqueService.IsRegistered;

    public string MasqueStatus
    {
        get => _masqueStatus;
        private set { _masqueStatus = value; OnPropertyChanged(); }
    }

    /// <summary>What to paste into a browser or an application while the proxy is up.</summary>
    public string MasqueProxyAddress => MasqueService.ProxyAddress(Settings.MasqueListenPort);

    /// <summary>Where Cloudflare says the traffic comes out, once a connection has been proved. Shown
    /// because the country is the part that decides whether this is useful to the user at all.</summary>
    public string MasqueExit
    {
        get
        {
            if (_masque.LastExit is not { } e || !IsMasqueOn) return Loc.T("—");
            return e.Location.Length > 0 ? $"{e.Ip} ({e.Location})" : e.Ip;
        }
    }

    /// <summary>Local port the proxy listens on. Changing it while the proxy is up does nothing until it
    /// is restarted — said in the status rather than silently restarting under the user.</summary>
    public int MasqueListenPort
    {
        get => Settings.MasqueListenPort;
        set
        {
            if (value == Settings.MasqueListenPort || value < 1 || value > 65535) return;
            Settings.MasqueListenPort = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasqueProxyAddress));
            if (IsMasqueOn) MasqueStatus = Loc.T("Порт сохранён — выключите и включите прокси, чтобы применить.");
        }
    }

    public RelayCommand RegisterMasqueCommand { get; private set; } = null!;
    public RelayCommand ResetMasqueCommand { get; private set; } = null!;
    public RelayCommand CopyProxyAddressCommand { get; private set; } = null!;

    // ---- actions -----------------------------------------------------------

    /// <summary>Enrol a device with Cloudflare. Needs the bypass running on a censored network: the
    /// request goes to api.cloudflareclient.com, whose name is cut by SNI, and the engine covers it
    /// unconditionally — but only while it is running.</summary>
    private async Task RegisterMasqueAsync()
    {
        IsMasqueBusy = true;
        try
        {
            MasqueStatus = Loc.T("Создаю устройство…");
            var r = await _masque.RegisterAsync();
            MasqueStatus = r.Ok
                ? Loc.T("Устройство готово. Включите прокси.")
                : r.Message + (IsRunning ? "" : " " + Loc.T("Обход сейчас выключен: включите его на «Главной» — "
                                                          + "без него запрос до Cloudflare может не дойти."));
        }
        catch (Exception ex) { MasqueStatus = Loc.T("Ошибка: {0}", ex.Message); }
        finally { IsMasqueBusy = false; }
    }

    /// <summary>Forget the device so the next registration issues a new one.</summary>
    private async Task ResetMasqueAsync()
    {
        IsMasqueBusy = true;
        try
        {
            await Task.Run(_masque.Reset);
            _isMasqueOn = false;
            MasqueStatus = Loc.T("Устройство удалено. Создайте новое.");
        }
        catch (Exception ex) { MasqueStatus = Loc.T("Ошибка: {0}", ex.Message); }
        finally { IsMasqueBusy = false; }
    }

    private async Task ToggleMasqueAsync(bool on)
    {
        if (IsMasqueBusy) { NotifyMasqueState(); return; }

        IsMasqueBusy = true;
        try
        {
            if (!on)
            {
                await Task.Run(_masque.Stop);
                _isMasqueOn = false;
                MasqueStatus = Loc.T("Прокси выключен.");
                return;
            }

            // Widen the scope BEFORE dialling, not after. What has to survive the DPI is the MASQUE
            // handshake itself, so a scope applied once the proxy is up would arrive for the one
            // connection that no longer needs it. The finally below puts it back if this fails.
            _isMasqueOn = true;
            await SyncMasqueBypassScopeAsync();

            MasqueStatus = Loc.T("Подключаюсь к Cloudflare…");
            var (result, winner) = await _masque.ConnectAsync(
                Settings.MasqueListenPort, Settings.MasqueHttp2, Settings.MasqueConnectPort);

            _isMasqueOn = result.Ok;
            MasqueStatus = result.Message;

            // Remember what worked, so the next connection starts there instead of walking the sweep.
            if (result.Ok && winner is { } w)
            {
                Settings.MasqueHttp2 = w.Http2;
                Settings.MasqueConnectPort = w.ConnectPort;
                _settingsSvc.Save();
                Notify?.Invoke(Loc.T("WARP"), result.Message);
            }
        }
        catch (Exception ex)
        {
            _isMasqueOn = false;
            MasqueStatus = Loc.T("Ошибка: {0}", ex.Message);
        }
        finally
        {
            // Read the real state back rather than trusting the request…
            _isMasqueOn = _masque.IsRunning;
            // …and put the scope where that real state says it belongs. A connection that failed must
            // not leave every site being desynced on behalf of a proxy that is not running.
            await SyncMasqueBypassScopeAsync();
            IsMasqueBusy = false;
        }
    }

    /// <summary>Push <see cref="EffectiveBypassAllSites"/> into the engine, restarting it if that
    /// actually changed anything. No-op when the user already bypasses every site.</summary>
    private async Task SyncMasqueBypassScopeAsync()
    {
        bool wanted = EffectiveBypassAllSites;
        if (_engine.BypassAllSites == wanted) return;

        _engine.BypassAllSites = wanted;
        OnPropertyChanged(nameof(CommandPreview));
        AppendLog(wanted
            ? Loc.T("WARP включён → обход временно распространён на все сайты: без этого подключение "
                    + "к Cloudflare не встаёт.")
            : Loc.T("WARP выключен → область обхода вернулась к вашей настройке."));
        if (IsRunning) await ApplyEngineOptionsAsync();
    }

    private void CopyProxyAddress()
    {
        try
        {
            Clipboard.SetText(MasqueProxyAddress);
            MasqueStatus = Loc.T("Адрес прокси скопирован: {0}", MasqueProxyAddress);
        }
        catch (Exception ex) { MasqueStatus = Loc.T("Не удалось скопировать: {0}", ex.Message); }
    }

    private void NotifyMasqueState()
    {
        OnPropertyChanged(nameof(IsMasqueEnabled));
        OnPropertyChanged(nameof(IsMasqueOn));
        OnPropertyChanged(nameof(CanToggleMasque));
        OnPropertyChanged(nameof(IsMasqueRegistered));
        OnPropertyChanged(nameof(MasqueProxyAddress));
        OnPropertyChanged(nameof(MasqueExit));
        RegisterMasqueCommand.RaiseCanExecuteChanged();
        ResetMasqueCommand.RaiseCanExecuteChanged();
        CopyProxyAddressCommand.RaiseCanExecuteChanged();
    }

    /// <summary>True while the proxy is up. Public because the bypass scope follows it (see
    /// <see cref="SyncMasqueBypassScopeAsync"/>) and both tabs that mention that have to be able to
    /// see it.</summary>
    public bool IsMasqueOn => _isMasqueOn;
}
