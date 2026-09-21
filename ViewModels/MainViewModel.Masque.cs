using System.Windows;
using Zapret2UI.Localization;
using Zapret2UI.Mvvm;
using Zapret2UI.Services.Platform;
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

    /// <summary>The chain: a SECOND WARP connection, dialled through Tor or the user's own proxy, living
    /// beside the ordinary one rather than replacing it.
    ///
    /// <para>They were one switch at first, and that was wrong: turning WARP on then dragged the whole
    /// machine through Tor, and the ordinary proxy — the fast one people use for reputation — could not
    /// be had at all. Two simultaneous sessions from one registered device were measured working, so
    /// these are two switches: the fast proxy for everything, the slow chain for the sites that need a
    /// country.</para></summary>
    private readonly MasqueService _chain = new();

    private bool _isMasqueOn;
    private bool _isMasqueBusy;
    private string _masqueStatus = "";

    private bool _isChainOn;
    private bool _isChainBusy;
    private string _chainStatus = "";

    private void InitMasqueCommands()
    {
        _masque.LogLine += AppendLog;
        // A 22 MB download and a Tor bootstrap are minutes, not seconds: without this the switch sits
        // on «Подключаюсь…» long enough to look hung.
        _masque.Status += line => OnUi(() => MasqueStatus = line);
        _chain.LogLine += AppendLog;
        _chain.Status += line => OnUi(() => ChainStatus = line);

        RegisterMasqueCommand = new RelayCommand(
            async _ => await RegisterMasqueAsync(), _ => !IsMasqueBusy && !IsMasqueRegistered);
        ResetMasqueCommand = new RelayCommand(
            async _ => await ResetMasqueAsync(), _ => !IsMasqueBusy && IsMasqueRegistered);
        CopyProxyAddressCommand = new RelayCommand(
            _ => CopyProxyAddress(), _ => IsMasqueOn);
        ResetAiDomainsCommand = new RelayCommand(_ => ResetAiDomains());
        FetchBridgesCommand = new RelayCommand(
            async _ => await FetchBridgesAsync(), _ => !IsBridgeBusy);
        // The test starts a Tor of its own, and two of those share one data directory and refuse to run.
        // Blocked rather than queued: «подождите, пока цепочка выключится» is a truth the button can say
        // in a tooltip, while a queued job that starts minutes later is not what anyone pressed.
        TestBridgesCommand = new RelayCommand(
            async _ => await TestBridgesAsync(), _ => !IsBridgeBusy && !IsChainOn);
        // Same reason as the bridge test: it starts a Tor of its own.
        PickExitCommand = new RelayCommand(
            async _ => await PickExitAsync(), _ => !IsBridgeBusy && !IsChainOn);
        ClearExitCommand = new RelayCommand(_ => ClearPinnedExit(), _ => HasPinnedExit);
    }

    /// <summary>Put the shipped list back — by forgetting the user's, not by copying today's defaults
    /// into their settings, so the list keeps following the app.</summary>
    private void ResetAiDomains()
    {
        if (Settings.MasqueAiDomains.Length == 0 && Settings.MasqueAiBypass == AiDomains.BypassText) return;
        Settings.MasqueAiDomains = "";
        Settings.MasqueAiBypass = AiDomains.BypassText;
        _settingsSvc.Save();
        OnPropertyChanged(nameof(MasqueAiDomainsText));
        OnPropertyChanged(nameof(MasqueAiBypassText));
        SyncSystemProxy();
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
            string exit = e.Location.Length > 0 ? $"{e.Ip} ({e.Location})" : e.Ip;
            // With an entrance of the user's own, the two countries together are the answer: the exit
            // follows the entrance, so one without the other says nothing about whether it worked.
            return _masque.LastIngressLocation.Length > 0
                ? Loc.T("{0} · вход {1}", exit, _masque.LastIngressLocation)
                : exit;
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

    /// <summary>Send the whole system through the proxy while it runs, by pointing Windows' own proxy
    /// setting at it. Applied only once the tunnel is proved up — the setting is what most applications
    /// obey, so pointing it at a proxy that turned out not to work would take browsing down with it.</summary>
    public bool MasqueSystemProxy
    {
        get => Settings.MasqueSystemProxy;
        set
        {
            if (value == Settings.MasqueSystemProxy) return;
            Settings.MasqueSystemProxy = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            SyncSystemProxy();
        }
    }

    /// <summary>A SOCKS5 proxy to reach Cloudflare THROUGH — usually the local port a VLESS client
    /// already listens on. Empty dials Cloudflare directly, which is what every version so far did.
    ///
    /// <para>Applied on the next connection rather than under a running one: the entrance is chosen when
    /// the tunnel is dialled, so changing it mid-flight would show a setting that is not the one in
    /// force. The status line says so instead of pretending.</para></summary>
    public string MasqueIngressProxy
    {
        get => Settings.MasqueIngressProxy;
        set
        {
            string typed = (value ?? "").Trim();
            if (typed == Settings.MasqueIngressProxy) return;
            Settings.MasqueIngressProxy = typed;
            _settingsSvc.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesExternalEntrance));
            NotifyChain();
            // The entrance belongs to the chain alone — the WARP switch always dials directly — so it is the
            // chain that has to be restarted, and the chain's line that says so.
            if (IsChainOn) ChainStatus = Loc.T("Адрес сохранён — выключите и включите цепочку, чтобы применить.");
        }
    }

    /// <summary>The entrance as the service wants it, assembled from the three settings that describe
    /// it. One place, so a half-applied choice (Tor picked, country left behind) cannot happen.</summary>
    private MasqueEntrance Entrance => Settings.MasqueIngress switch
    {
        "proxy" => new MasqueEntrance(MasqueEntranceKind.Proxy, Settings.MasqueIngressProxy),
        "tor" => new MasqueEntrance(MasqueEntranceKind.Tor, "",
                                    Settings.MasqueTorCountry, Settings.MasqueTorBridges,
                                    Settings.MasqueTorExit),
        _ => MasqueEntrance.Straight,
    };

    /// <summary>True while the connection goes out through something of the user's own — used by the
    /// interface to show the note that explains what that changes.</summary>
    public bool UsesExternalEntrance => Entrance.Kind != MasqueEntranceKind.Direct;

    /// <summary>The route, link by link, for the diagram at the top of the tab.
    ///
    /// <para><b>Why the tab starts with this.</b> What HMS does is put several things in a row, and every
    /// question people ask about it — why is it slow, why does the country still say RU, why do only some
    /// sites go this way — is a question about WHICH link. A list of settings cannot answer that; a
    /// drawing of the chain answers it before it is asked. It is built from the live settings rather than
    /// written out in the XAML, so it shows the chain the user actually has: pick «Напрямую» and it
    /// visibly loses its middle, which is the honest way to say that this entrance changes no
    /// country.</para></summary>
    public IReadOnlyList<ChainStep> ChainSteps
    {
        get
        {
            // What the app itself does here depends on the entrance: the engine only helps where something of
            // OURS crosses the censored network (the bridges, WARP dialled directly), never the user's own
            // proxy client — see EntranceNeedsBypass.
            var steps = new List<(string Title, string Detail)>
            {
                (Loc.T("Ваш браузер"), Loc.T("в цепочку уходят только сайты из списка")),
                ("Zapret2UI", Entrance.Kind switch
                {
                    MasqueEntranceKind.Tor => Loc.T("принимает эти сайты и ведёт к мосту; обход помогает до него дойти"),
                    MasqueEntranceKind.Proxy => Loc.T("принимает эти сайты и отдаёт вашему прокси"),
                    _ => Loc.T("отдаёт эти сайты обычному прокси WARP, если он включён"),
                }),
            };

            switch (Entrance.Kind)
            {
                case MasqueEntranceKind.Tor:
                    steps.Add((Loc.T("Мост"), BridgeStepDetail));
                    steps.Add(("Tor", TorStepDetail));
                    break;
                case MasqueEntranceKind.Proxy:
                    steps.Add((Loc.T("Ваш прокси"), Settings.MasqueIngressProxy.Length > 0
                        ? Settings.MasqueIngressProxy
                        : Loc.T("адрес не указан")));
                    break;
            }

            steps.Add(("Cloudflare WARP", Loc.T("адрес, которому доверяют")));
            steps.Add((Loc.T("Сайт"), Entrance.Kind == MasqueEntranceKind.Direct
                ? Loc.T("видит вашу страну — этот вход её не меняет")
                : Loc.T("видит страну входа, а не вашу")));

            return steps.Select((s, i) => new ChainStep(i + 1, s.Title, s.Detail, i < steps.Count - 1)).ToList();
        }
    }

    /// <summary>The WARP tab's four points, drawn by the same template as the chain so the two tabs explain
    /// themselves the same way. The order is the argument: two obstacles, then reputation, then why
    /// Cloudflare is trusted, then what it does NOT change — and the last one now says where the country
    /// IS changed, because since HMS exists «не меняет» is only half the answer. The same four beats are
    /// in both manuals, both warp.html and both READMEs; they move together.</summary>
    public IReadOnlyList<ChainStep> WarpSteps { get; } = new[]
    {
        new ChainStep(1, "", Loc.T("Преграды две, и они разные. Обход снимает первую: провайдер не даёт достучаться до сайта. Пакеты переписываются, соединение проходит. Адрес, с которого вы приходите, при этом остаётся вашим."), true),
        new ChainStep(2, "", Loc.T("Вторая преграда — сам сайт. Он смотрит, с какого адреса вы пришли, и сверяется с репутацией. Домашние российские диапазоны у антифрод-систем на плохом счету: оттуда идёт много ботов и злоупотреблений, и низкое доверие получает весь диапазон разом, а не отдельный нарушитель."), true),
        new ChainStep(3, "", Loc.T("Адреса WARP принадлежат Cloudflare, а через неё проходит заметная часть всего веба — в списках доверия её адреса стоят совсем иначе. С включённым прокси вы приходите не из подозрительного пула, а как клиент Cloudflare, и та же самая проверка вас пропускает."), true),
        new ChainStep(4, "", Loc.T("Страну сам WARP не меняет: бесплатный выводит через ближайший узел, и из России адрес будет российским. Меняется репутация адреса. Для «недоступно в вашем регионе» есть вкладка HMS — там к Cloudflare приходят из другой страны, и выход оказывается там же."), false),
    };

    /// <summary>Which kind of first hop the pasted lines describe. A plain relay is a different animal
    /// from a pluggable transport — no obfuscation, one process fewer — and the difference is worth a
    /// word in the drawing rather than a paragraph underneath it.</summary>
    private string BridgeStepDetail
    {
        get
        {
            var bridges = TorRuntime.ParseBridges(Settings.MasqueTorBridges);
            if (bridges.Count == 0) return Loc.T("встроенный в комплект");

            var kinds = bridges
                .Select(b => b.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
                .Select(k => k.Contains(':') ? Loc.T("обычное реле") : k.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Loc.T("{0}, всего {1}", string.Join(" / ", kinds), bridges.Count);
        }
    }

    private string TorStepDetail
    {
        get
        {
            string code = Settings.MasqueTorCountry;
            string name = Countries.FirstOrDefault(c => string.Equals(c.Code, code,
                                                                      StringComparison.OrdinalIgnoreCase))?.Name
                          ?? code.ToUpperInvariant();
            return HasPinnedExit
                ? Loc.T("{0}, закреплён выход {1}", name, Settings.MasqueTorExit)
                : Loc.T("{0}, выход каждый раз новый", name);
        }
    }

    /// <summary>Redraw the diagram. Called from every setting it is built from — a picture that lags
    /// behind the switch beside it is worse than no picture.</summary>
    private void NotifyChain() => OnPropertyChanged(nameof(ChainSteps));

    // ---- the chain -----------------------------------------------------------

    /// <summary>The chain's own switch, separate from the WARP one. Off by default and never implied:
    /// turning WARP on must not drag anyone through Tor.</summary>
    public bool IsChainEnabled
    {
        get => _isChainOn;
        set { if (value != _isChainOn) _ = ToggleChainAsync(value); }
    }

    public bool IsChainOn => _isChainOn;

    public bool IsChainBusy
    {
        get => _isChainBusy;
        private set { _isChainBusy = value; OnPropertyChanged(); NotifyMasqueState(); }
    }

    /// <summary>The chain only exists to change the country, so with «напрямую» chosen there is nothing
    /// to raise — the listed sites simply go through the ordinary proxy instead. A chain that is already
    /// running can always be switched OFF, though: picking «напрямую» under it must not leave a switch
    /// that is on and greyed out.</summary>
    public bool CanToggleChain => !IsChainBusy && IsMasqueRegistered && (UsesExternalEntrance || IsChainOn);

    public string ChainStatus
    {
        get => _chainStatus;
        private set { _chainStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasChainStatus)); }
    }

    /// <summary>Keeps the status line out of the card until there is something in it: an empty TextBlock
    /// still measures one line, and under the switch it read as a hole.</summary>
    public bool HasChainStatus => ChainStatus.Length > 0;

    /// <summary>Where the chain comes out, and where it went in — the pair is the answer, because the
    /// exit follows the entrance.</summary>
    public string ChainExit
    {
        get
        {
            if (_chain.LastExit is not { } e || !IsChainOn) return Loc.T("—");
            string exit = e.Location.Length > 0 ? $"{e.Ip} ({e.Location})" : e.Ip;
            return _chain.LastIngressLocation.Length > 0
                ? Loc.T("{0} · вход {1}", exit, _chain.LastIngressLocation)
                : exit;
        }
    }

    /// <summary>Loopback port for the chain. Nudged off the ordinary proxy's port rather than colliding
    /// with it: a user who changed one and not the other should not get «порт занят».</summary>
    private int ChainPort => Settings.MasqueChainPort == Settings.MasqueListenPort
        ? Settings.MasqueListenPort + 1
        : Settings.MasqueChainPort;

    /// <summary>Which proxy the routed sites should be sent to: the chain's port while it runs, the
    /// ordinary proxy's when «напрямую» is chosen and it runs, and 0 when there is nothing to send them
    /// to — in which case the rule is not written at all, and the sites go out as usual.
    ///
    /// <para>The running chain wins even over «напрямую»: the entrance can be changed under it, and the
    /// sites should stay where the user put them until the chain is switched off.</para></summary>
    private int RoutedProxyPort => _chain.IsRunning
        ? ChainPort
        : (Entrance.Kind == MasqueEntranceKind.Direct && _masque.IsRunning ? Settings.MasqueListenPort : 0);

    /// <summary>True while something of OURS still has to cross the censored network, and therefore
    /// still needs the engine's help: WARP dialled directly, or Tor reaching its bridges (and, the first
    /// time, fetching its own bundle). Only the user's own proxy is exempt — there the last hop out
    /// belongs to their client, which looks after itself.</summary>
    public bool EntranceNeedsBypass => Entrance.Kind != MasqueEntranceKind.Proxy;

    /// <summary>The three-way choice, as three bindable flags — what a group of radio buttons needs.
    /// Setting one is what stores the choice; the other two follow from it.</summary>
    public bool IngressIsDirect
    {
        get => Entrance.Kind == MasqueEntranceKind.Direct;
        set { if (value) SetIngress("direct"); }
    }

    public bool IngressIsProxy
    {
        get => Entrance.Kind == MasqueEntranceKind.Proxy;
        set { if (value) SetIngress("proxy"); }
    }

    public bool IngressIsTor
    {
        get => Entrance.Kind == MasqueEntranceKind.Tor;
        set { if (value) SetIngress("tor"); }
    }

    private void SetIngress(string kind)
    {
        if (Settings.MasqueIngress == kind) return;
        Settings.MasqueIngress = kind;
        _settingsSvc.Save();
        // The whole state, not just the three chips: whether the chain switch is enabled depends on the
        // entrance too, and without this it stayed greyed out after «напрямую» → «Tor» until something
        // unrelated happened to refresh it.
        NotifyMasqueState();
        NotifyChain();
        if (IsChainOn)
            ChainStatus = kind == "direct"
                ? Loc.T("Выбран вход «Напрямую» — цепочка пока идёт через прежний вход. Выключите её.")
                : Loc.T("Вход сохранён — выключите и включите цепочку, чтобы применить.");
    }

    /// <summary>Country the Tor entrance is pinned to. Two letters, because that is what torrc takes.</summary>
    public string MasqueTorCountry
    {
        get => Settings.MasqueTorCountry;
        set
        {
            string code = (value ?? "").Trim().ToLowerInvariant();
            if (code == Settings.MasqueTorCountry || code.Length == 0) return;
            Settings.MasqueTorCountry = code;
            _settingsSvc.Save();
            OnPropertyChanged();
            NotifyChain();
            if (IsChainOn)
                ChainStatus = Loc.T("Страна сохранена — выключите и включите цепочку, чтобы применить.");
        }
    }

    /// <summary>Bridge lines, one per line. Empty falls back to the ones inside the bundle.</summary>
    public string MasqueTorBridges
    {
        get => Settings.MasqueTorBridges;
        set
        {
            string text = (value ?? "").Trim();
            if (text == Settings.MasqueTorBridges) return;
            Settings.MasqueTorBridges = text;
            _settingsSvc.Save();
            OnPropertyChanged();
            NotifyChain();
            if (IsChainOn)
                ChainStatus = Loc.T("Мосты сохранены — выключите и включите цепочку, чтобы применить.");
        }
    }

    /// <summary>What the bridge buttons are doing at the moment. Empty when they are not doing
    /// anything — both jobs take minutes, and a button that looks stuck is a button pressed twice.</summary>
    public string BridgeStatus
    {
        get => _bridgeStatus;
        private set { _bridgeStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBridgeStatus)); }
    }

    /// <summary>Keeps the line out of the layout entirely until there is something to say — an empty
    /// paragraph still takes its margin, and this card is already tall.</summary>
    public bool HasBridgeStatus => BridgeStatus.Length > 0;

    private string _bridgeStatus = "";
    private bool _bridgeBusy;

    public bool IsBridgeBusy
    {
        get => _bridgeBusy;
        private set
        {
            _bridgeBusy = value;
            OnPropertyChanged();
            FetchBridgesCommand.RaiseCanExecuteChanged();
            TestBridgesCommand.RaiseCanExecuteChanged();
            PickExitCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Ask the Tor Project for bridges and add what comes back to the box.
    ///
    /// <para>Added rather than replaced: the lines the user pasted from the Telegram bot are theirs, and
    /// a button that silently overwrites them is a button pressed once and then never again.</para></summary>
    private async Task FetchBridgesAsync()
    {
        IsBridgeBusy = true;
        try
        {
            BridgeStatus = Loc.T("Запрос к Tor Project…");

            string country = await BridgeDirectory.DetectCountryAsync().ConfigureAwait(true);

            // Through WARP when it is up, because torproject.org is blocked on exactly the networks this
            // button is for — the same way the Tor bundle itself is fetched.
            string through = IsMasqueOn ? MasqueService.ProxyAddress(Settings.MasqueListenPort) : "";

            var (result, bridges) = await BridgeDirectory.FetchAsync(country, through).ConfigureAwait(true);
            if (result.Ok)
                AppendLog(Loc.T("[мосты] получено от Tor Project: {0}", bridges.Count));
            else
                AppendLog(Loc.T("[мосты] {0}", result.Message));

            // And then the other source, which is a different thing entirely: ordinary relays, picked for
            // answering THIS machine right now rather than for being on anyone's list. They carry no
            // obfuscation, so they are the faster way in where they are not blocked — and where they are,
            // the probe simply finds none and nothing is added. Asked for even when the distributor
            // answered, because the two fail in different places and on a bad day only one of them does.
            BridgeStatus = Loc.T("Поиск обычных реле Tor, до которых отсюда есть связь…");
            var (scan, relays) = await RelayScanner.FindAsync(3, through).ConfigureAwait(true);
            AppendLog(scan.Ok
                ? Loc.T("[мосты] обычных реле, ответивших отсюда: {0}", relays.Count)
                : Loc.T("[мосты] {0}", scan.Message));

            // Every line in the box, not only the ones torrc will take — a line past the first twelve is
            // still the user's, and adding it a second time would only push the real ones further down.
            var have = TorRuntime.ParseBridges(MasqueTorBridges, int.MaxValue);
            var addedBridges = bridges.Where(b => !have.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList();
            var addedRelays = relays.Where(r => !have.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            var added = addedBridges.Concat(addedRelays).ToList();

            if (added.Count == 0)
            {
                BridgeStatus = result.Ok || scan.Ok
                    ? Loc.T("Новых мостов нет — отдали те же, что уже есть.")
                    : result.Message;
                return;
            }

            string text = MasqueTorBridges.TrimEnd();
            MasqueTorBridges = (text.Length > 0 ? text + Environment.NewLine : "")
                             + string.Join(Environment.NewLine, added);
            OnPropertyChanged(nameof(MasqueTorBridges));

            BridgeStatus = Loc.T("Добавлено строк: {0} (мостов {1}, обычных реле {2}). Проверьте скорость — "
                                 + "работают не все.", added.Count, addedBridges.Count, addedRelays.Count);
        }
        catch (Exception ex) { BridgeStatus = ex.Message; }
        finally { IsBridgeBusy = false; }
    }

    /// <summary>The pinned exit, spelled out for the card. Empty when any node in the country will
    /// do.</summary>
    public string MasqueTorExit => Settings.MasqueTorExit;

    public bool HasPinnedExit => Settings.MasqueTorExit.Length > 0;

    /// <summary>The pin as one readable line, so the card does not have to glue a label to a binding.</summary>
    public string PinnedExitLine => HasPinnedExit
        ? Loc.T("Закреплён выход {0}. Адрес, который видят сервисы, больше не меняется.",
                Settings.MasqueTorExit)
        : "";

    /// <summary>Measure the country's fastest exits and keep the best, so the address the services see
    /// stops changing on every reconnect.</summary>
    private async Task PickExitAsync()
    {
        IsBridgeBusy = true;
        try
        {
            var picker = new ExitPicker();
            picker.Status += line => OnUi(() => BridgeStatus = line);
            picker.LogLine += AppendLog;

            var (result, address, rows) = await picker
                .RunAsync(Settings.MasqueTorCountry, Settings.MasqueTorBridges).ConfigureAwait(true);

            if (!result.Ok)
            {
                BridgeStatus = result.Message;
                return;
            }

            Settings.MasqueTorExit = address;
            _settingsSvc.Save();
            OnPropertyChanged(nameof(MasqueTorExit));
            OnPropertyChanged(nameof(HasPinnedExit));
            OnPropertyChanged(nameof(PinnedExitLine));
            ClearExitCommand.RaiseCanExecuteChanged();
            NotifyChain();

            BridgeStatus = Loc.T("Закреплён выход {0} — {1} Мбит/с, из {2} проверенных. Теперь адрес не "
                                 + "будет меняться при каждом переподключении.",
                                 address, rows[0].Mbits.ToString("0.0"), rows.Count);
            AppendLog(Loc.T("[выход] закреплён {0}", address));
        }
        catch (Exception ex) { BridgeStatus = ex.Message; }
        finally { IsBridgeBusy = false; }
    }

    private void ClearPinnedExit()
    {
        if (Settings.MasqueTorExit.Length == 0) return;
        Settings.MasqueTorExit = "";
        _settingsSvc.Save();
        OnPropertyChanged(nameof(MasqueTorExit));
        OnPropertyChanged(nameof(HasPinnedExit));
        OnPropertyChanged(nameof(PinnedExitLine));
        ClearExitCommand.RaiseCanExecuteChanged();
        NotifyChain();
        BridgeStatus = Loc.T("Выход больше не закреплён — Tor снова берёт любой узел в выбранной стране.");
    }

    /// <summary>Bring every bridge up in turn and write what it carried next to it.
    ///
    /// <para>EVERY line, not the twelve torrc takes: the box is rewritten from the results, so a line that
    /// was not measured would be a line deleted — and «Получить мосты» appends, so the ones past twelve are
    /// exactly the fresh ordinary relays. Measuring all of them is also what makes the cap harmless: the
    /// fastest end up on top, where tor reads.</para></summary>
    private async Task TestBridgesAsync()
    {
        var bridges = TorRuntime.ParseBridges(MasqueTorBridges, int.MaxValue);
        if (bridges.Count == 0)
        {
            BridgeStatus = Loc.T("Сначала получите или вставьте мосты.");
            return;
        }

        IsBridgeBusy = true;
        try
        {
            var test = new BridgeSpeedTest();
            test.Status += line => OnUi(() => BridgeStatus = line);
            test.LogLine += AppendLog;

            var rows = await test.RunAsync(bridges, Settings.MasqueTorCountry).ConfigureAwait(true);

            MasqueTorBridges = BridgeSpeedTest.Rewrite(rows);
            OnPropertyChanged(nameof(MasqueTorBridges));

            var alive = rows.Where(r => r.Ok).ToList();
            BridgeStatus = alive.Count == 0
                ? Loc.T("Ни один мост не поднялся. Получите новые или возьмите другие у @GetBridgesBot.")
                : Loc.T("Живых мостов: {0} из {1}, самый быстрый {2} Мбит/с. Список пересобран, быстрые сверху.",
                        alive.Count, rows.Count, alive.Max(r => r.Mbits).ToString("0.0"))
                  + (rows.Count > TorRuntime.MaxBridges
                        ? " " + Loc.T("Tor берёт из списка первые {0}.", TorRuntime.MaxBridges)
                        : "");
        }
        catch (Exception ex) { BridgeStatus = ex.Message; }
        finally { IsBridgeBusy = false; }
    }

    /// <summary>Countries worth offering for the Tor entrance: enough exit capacity to actually get a
    /// circuit, and not themselves refused by the services this exists for. Any other code still works —
    /// this is guidance, not a whitelist, because Tor takes any country and we have no business deciding
    /// the user cannot try Estonia.</summary>
    private static readonly TorCountry[] Countries =
    {
        new("de", Loc.T("Германия")),
        new("nl", Loc.T("Нидерланды")),
        new("fr", Loc.T("Франция")),
        new("fi", Loc.T("Финляндия")),
        new("se", Loc.T("Швеция")),
        new("gb", Loc.T("Великобритания")),
        new("us", Loc.T("США")),
        new("pl", Loc.T("Польша")),
    };

    /// <summary>The codes spelled out, because «двухбуквенный код страны» helps nobody who does not
    /// already know one.</summary>
    public string TorCountriesHint =>
        string.Join(", ", Countries.Select(c => $"{c.Code} — {c.Name}"));

    /// <summary>Send the AI sites through the proxy and leave everything else alone, by writing a routing
    /// rule into Windows' own proxy setting. Takes effect at once — the rule is applied and removed with
    /// the proxy, not with the connection.</summary>
    public bool MasqueAiRouting
    {
        get => Settings.MasqueAiRouting;
        set
        {
            if (value == Settings.MasqueAiRouting) return;
            Settings.MasqueAiRouting = value;
            _settingsSvc.Save();
            OnPropertyChanged();
            SyncSystemProxy();
        }
    }

    /// <summary>Exactly which sites the rule covers, one per line — editable, because «сайты ИИ» is a
    /// promise the user should be able to read and change rather than take on trust.
    ///
    /// <para>The shipped list is stored as an EMPTY setting rather than as its own text: that way a user
    /// who never touched it keeps getting the list as it grows with the app, and one who edited it keeps
    /// exactly what they typed.</para></summary>
    public string MasqueAiDomainsText
    {
        get => Settings.MasqueAiDomains.Length > 0
            ? Settings.MasqueAiDomains
            : string.Join(Environment.NewLine, AiDomains.Default);
        set
        {
            string text = (value ?? "").Trim();
            if (text == string.Join(Environment.NewLine, AiDomains.Default)) text = "";
            if (text == Settings.MasqueAiDomains) return;

            Settings.MasqueAiDomains = text;
            _settingsSvc.Save();
            OnPropertyChanged();
            SyncSystemProxy();                  // the rule follows the list immediately
        }
    }

    /// <summary>The list as the rule needs it: trimmed, without comments or repeats. An empty list falls
    /// back to the shipped one — clearing the box is «верните как было», not «не вести ничего», and the
    /// switch beside it is what turns the routing off.</summary>
    private IReadOnlyList<string> RoutedDomains
    {
        get
        {
            var domains = Lines(MasqueAiDomainsText);
            return domains.Count > 0 ? domains : AiDomains.Default;
        }
    }

    /// <summary>The names that go past the chain. Unlike the routed list this one is allowed to be empty:
    /// «никаких исключений» is a legitimate answer, and falling back to the shipped list when the user
    /// deliberately cleared the box would put an exception back that they had just removed.</summary>
    private IReadOnlyList<string> BypassDomains => Lines(Settings.MasqueAiBypass);

    /// <summary>What the user typed in one of the two boxes: one name per line, blanks and «#» notes
    /// dropped, duplicates collapsed.</summary>
    private static List<string> Lines(string text) => text
        .Split('\n')
        .Select(line => line.Trim().Trim('\r'))
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The exceptions box, stored exactly as typed — unlike the routed list, where empty means
    /// «the list that ships with the app». «Никаких исключений» has to be a state the user can actually
    /// reach: an emptied box that quietly refilled itself with the shipped exception would send a site
    /// past the chain after the user had just said not to.</summary>
    public string MasqueAiBypassText
    {
        get => Settings.MasqueAiBypass;
        set
        {
            string text = (value ?? "").Trim();
            if (text == Settings.MasqueAiBypass) return;

            Settings.MasqueAiBypass = text;
            _settingsSvc.Save();
            OnPropertyChanged();
            SyncSystemProxy();                  // the rule follows the list immediately
        }
    }

    public RelayCommand ResetAiDomainsCommand { get; private set; } = null!;

    public RelayCommand FetchBridgesCommand { get; private set; } = null!;

    public RelayCommand TestBridgesCommand { get; private set; } = null!;

    public RelayCommand PickExitCommand { get; private set; } = null!;

    public RelayCommand ClearExitCommand { get; private set; } = null!;

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
            MasqueStatus = Loc.T("Создание устройства…");
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

            // Always dialled DIRECTLY. The entrance belongs to the chain, on its own switch: this one is
            // the fast proxy people turn on for reputation, and it must never quietly become Tor.
            MasqueStatus = Loc.T("Подключение к Cloudflare…");
            var (result, winner) = await _masque.ConnectAsync(
                Settings.MasqueListenPort, Settings.MasqueHttp2, Settings.MasqueConnectPort,
                MasqueEntrance.Straight);

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
            SyncSystemProxy();
            IsMasqueBusy = false;
        }
    }

    /// <summary>Raise or drop the chain — the second WARP connection, dialled through the chosen
    /// entrance. Shaped like the WARP switch above and deliberately independent of it: either can be on
    /// without the other.</summary>
    private async Task ToggleChainAsync(bool on)
    {
        if (IsChainBusy) { NotifyMasqueState(); return; }

        IsChainBusy = true;
        try
        {
            if (!on)
            {
                await Task.Run(_chain.Stop);
                _isChainOn = false;
                ChainStatus = Loc.T("Цепочка выключена.");
                return;
            }

            // Tor's bridges cross the censored network, so the engine has to be wide before they are
            // dialled — same reasoning as the WARP switch, and the finally below puts it back.
            _isChainOn = true;
            await SyncMasqueBypassScopeAsync();

            ChainStatus = Entrance.Kind == MasqueEntranceKind.Tor
                ? Loc.T("Запуск Tor…")
                : Loc.T("Подключение к Cloudflare через ваш прокси…");

            var (result, _) = await _chain.ConnectAsync(
                ChainPort, preferHttp2: true, Settings.MasqueConnectPort, Entrance);

            _isChainOn = result.Ok;
            ChainStatus = result.Message;
            if (result.Ok) Notify?.Invoke(Loc.T("WARP"), result.Message);
        }
        catch (Exception ex)
        {
            _isChainOn = false;
            ChainStatus = Loc.T("Ошибка: {0}", ex.Message);
        }
        finally
        {
            _isChainOn = _chain.IsRunning;
            await SyncMasqueBypassScopeAsync();
            SyncSystemProxy();
            IsChainBusy = false;
        }
    }

    /// <summary>Point Windows' proxy setting at the proxy, or put it back, according to the setting and
    /// the REAL state of the proxy. Called after every change of either, so the two can never disagree.
    ///
    /// <para>Unlike the bypass scope this is applied <b>after</b> the tunnel is up, not before: almost
    /// everything on the machine obeys this setting, so aiming it at a proxy that then fails to connect
    /// would cost the user their browsing rather than just their WARP.</para></summary>
    private void SyncSystemProxy()
    {
        bool systemWide = Settings.MasqueSystemProxy && _masque.IsRunning;
        int port = Settings.MasqueListenPort;
        // Where the routed sites go: the chain while it runs, the ordinary proxy when «напрямую» is the
        // chosen entrance, and nowhere at all when neither is up — in which case the rule is not written,
        // because a rule pointing at a dead port would close those sites for no reason.
        int routed = Settings.MasqueAiRouting ? RoutedProxyPort : 0;

        if (!systemWide && routed == 0)
        {
            _proxyApplied = default;
            if (Settings.SystemProxyBackup.Length == 0) return;
            SystemProxyService.Restore(Settings.SystemProxyBackup, port);
            Settings.SystemProxyBackup = "";
            AppendLog(Loc.T("Системный прокси Windows возвращён в исходное состояние."));
            _settingsSvc.Save();
            return;
        }

        // One rule carries both switches. Windows prefers a routing rule over a plain proxy address, so
        // writing the rule for the AI sites while «весь трафик» is also on would quietly send everything
        // else direct — the rule has to answer for the whole machine in that case.
        string pac = routed > 0
            ? SystemProxyService.BuildPac(routed, RoutedDomains, systemWide ? port : 0, BypassDomains)
            : "";

        // The list is the user's to edit, so it can outgrow what Windows will carry. Said out loud
        // rather than swallowed: a rule that was silently not applied is a leak with a tidy log.
        if (pac.Length > 0 && SystemProxyService.PacUrl(pac).Length > SystemProxyService.PacUrlLimit)
        {
            AppendLog(Loc.T("Список сайтов слишком длинный — правило не помещается в настройку Windows. "
                            + "Уберите лишние домены: пока сайты из списка идут мимо прокси."));
            pac = "";
        }

        string? backup = SystemProxyService.Apply(port, systemWide, pac, Settings.SystemProxyBackup);
        if (backup is null)
        {
            AppendLog(Loc.T("Не удалось записать настройки прокси Windows — правило не применено."));
            return;
        }

        Settings.SystemProxyBackup = backup;
        _settingsSvc.Save();

        if (_proxyApplied == (systemWide, routed)) return;
        _proxyApplied = (systemWide, routed);

        if (systemWide)
            AppendLog(Loc.T("Системный прокси Windows направлен на {0}. Firefox читает свою настройку — "
                            + "его нужно настроить отдельно.", MasqueProxyAddress));
        if (pac.Length > 0)
            AppendLog(routed == Settings.MasqueListenPort
                ? Loc.T("Через обычный прокси WARP идут эти сайты: {0}. Остальные — по вашей настройке.",
                        string.Join(", ", RoutedDomains))
                : Loc.T("Через цепочку идут только эти сайты: {0}. Остальные — мимо неё, на полной "
                        + "скорости. Пока цепочка включена, мимо неё эти сайты не уйдут; выключите её — "
                        + "и правило снимется.", string.Join(", ", RoutedDomains)));
    }

    /// <summary>What the Windows setting was last put into, so the journal reports a change instead of
    /// repeating itself every time the switches are re-checked.</summary>
    private (bool SystemWide, int Routed) _proxyApplied;

    /// <summary>Undo a system proxy left applied by a crash. The proxy itself never survives the
    /// process, so a backup found at startup always means the setting outlived the thing it pointed
    /// at.</summary>
    internal void RestoreStaleSystemProxy()
    {
        if (Settings.SystemProxyBackup.Length == 0) return;
        SystemProxyService.Restore(Settings.SystemProxyBackup, Settings.MasqueListenPort);
        Settings.SystemProxyBackup = "";
        _settingsSvc.Save();
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
            ? Loc.T("WARP или цепочка через Tor включены → обход временно распространён на все сайты: "
                    + "без этого не подключаются ни WARP, ни мосты Tor.")
            : Loc.T("WARP и цепочка выключены → область обхода вернулась к вашей настройке."));
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
        OnPropertyChanged(nameof(MasqueSystemProxy));
        OnPropertyChanged(nameof(MasqueAiRouting));
        OnPropertyChanged(nameof(MasqueIngressProxy));
        OnPropertyChanged(nameof(MasqueTorCountry));
        OnPropertyChanged(nameof(MasqueTorBridges));
        OnPropertyChanged(nameof(UsesExternalEntrance));
        OnPropertyChanged(nameof(IngressIsDirect));
        OnPropertyChanged(nameof(IngressIsProxy));
        OnPropertyChanged(nameof(IngressIsTor));
        OnPropertyChanged(nameof(CanToggleMasque));
        OnPropertyChanged(nameof(IsChainEnabled));
        OnPropertyChanged(nameof(IsChainOn));
        OnPropertyChanged(nameof(CanToggleChain));
        OnPropertyChanged(nameof(ScopeWidenedByWarp));
        OnPropertyChanged(nameof(ChainExit));
        OnPropertyChanged(nameof(IsMasqueRegistered));
        OnPropertyChanged(nameof(MasqueProxyAddress));
        OnPropertyChanged(nameof(MasqueExit));
        RegisterMasqueCommand.RaiseCanExecuteChanged();
        ResetMasqueCommand.RaiseCanExecuteChanged();
        CopyProxyAddressCommand.RaiseCanExecuteChanged();
        // Neither the bridge test nor the exit picker can share Tor with a running chain, so both
        // buttons follow that switch.
        TestBridgesCommand.RaiseCanExecuteChanged();
        PickExitCommand.RaiseCanExecuteChanged();
        ClearExitCommand.RaiseCanExecuteChanged();
    }

    /// <summary>True while the proxy is up. Public because the bypass scope follows it (see
    /// <see cref="SyncMasqueBypassScopeAsync"/>) and both tabs that mention that have to be able to
    /// see it.</summary>
    public bool IsMasqueOn => _isMasqueOn;
}

/// <summary>A country the Tor entrance can be pinned to: the code torrc needs, and the name a person
/// reads. The code is identity and stays out of the translation table; the name is display.</summary>
/// <param name="Code">Two-letter country code, lower case.</param>
/// <param name="Name">What the list shows.</param>
internal sealed record TorCountry(string Code, string Name);

/// <summary>One step of a side-panel diagram: a link of the chain on the HMS tab, a line of the argument
/// on the WARP tab. Both are drawn by the same template, so the two tabs explain themselves the same
/// way.</summary>
/// <param name="Number">What the circle says.</param>
/// <param name="Title">What the step is: «Tor», «Cloudflare WARP». Empty when the step is a paragraph
/// rather than a link — the WARP tab's four points are sentences, not stations.</param>
/// <param name="Detail">What it is doing here.</param>
/// <param name="ShowLine">False on the last step, which has nothing below it to connect to. A bool
/// rather than an index because WPF has no «unless this is the last item», and a converter for it
/// would be one more thing to maintain.</param>
public sealed record ChainStep(int Number, string Title, string Detail, bool ShowLine)
{
    public bool HasTitle => Title.Length > 0;
}
