using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Zapret2UI.Models;
using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

/// <summary>
/// Availability matrix, the DPI check and the user's own target hosts.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- diagnostics (endpoint matrix) ------------------------------------

    private bool _isDiagnosing;
    public bool IsDiagnosing
    {
        get => _isDiagnosing;
        private set { if (SetField(ref _isDiagnosing, value)) RaiseCommandStates(); }
    }

    private string _diagStatusText = "Запустите диагностику, чтобы увидеть, что открывается, а что режется.";
    public string DiagStatusText { get => _diagStatusText; private set => SetField(ref _diagStatusText, value); }

    private string _diagSummary = "";
    public string DiagSummary { get => _diagSummary; private set => SetField(ref _diagSummary, value); }

    /// <summary>Reminds the user that the matrix reflects whatever is on the wire right now.</summary>
    public string DiagEngineNote => IsRunning
        ? $"Тест идёт с активным обходом: «{SelectedPreset?.Name}». Сравните с выключенным."
        : "Обход выключен — это базовая проверка. Включите обход и перезапустите, чтобы увидеть разницу.";

    private async Task RunDiagnosticsAsync()
    {
        if (IsDiagnosing) return;
        IsDiagnosing = true;
        DiagSummary = "";
        _diagCts = new CancellationTokenSource();
        try
        {
            await _diag.RunAsync(DiagRows.ToList(), _diagCts.Token);
            ComputeDiagSummary();
        }
        catch (OperationCanceledException) { DiagStatusText = "Диагностика остановлена."; }
        catch (Exception ex) { DiagStatusText = "Ошибка диагностики: " + ex.Message; }
        finally
        {
            IsDiagnosing = false;
            _diagCts?.Dispose();
            _diagCts = null;
        }
    }

    private void ComputeDiagSummary()
    {
        int ok = 0, bad = 0, to = 0;
        foreach (var r in DiagRows)
        {
            if (r.PingOnly) continue;
            foreach (var s in new[] { r.Http, r.Tls12, r.Tls13 })
            {
                if (s == DiagStatus.Ok) ok++;
                else if (s == DiagStatus.Fail) bad++;
                else if (s == DiagStatus.Timeout) to++;
            }
        }
        DiagSummary = $"OK: {ok}   ·   Ошибки: {bad}   ·   Таймауты: {to}";
    }

    // ---- DPI check (does the provider actively block by SNI?) -------------
    // Distinct from the availability matrix: for each censored host it TCP-connects (proving the server
    // is up), then sends a TLS ClientHello with the real SNI — a reset (RST) or a freeze on THAT packet
    // is a middlebox injecting, not "the site is down". Most telling with the bypass OFF.

    private static readonly string[] DpiHosts =
    {
        "discord.com", "gateway.discord.gg", "cdn.discordapp.com", "www.youtube.com",
    };

    private bool _isDpiChecking;
    public bool IsDpiChecking
    {
        get => _isDpiChecking;
        private set { if (SetField(ref _isDpiChecking, value)) RaiseCommandStates(); }
    }

    private string _dpiVerdictText =
        "«Проверка DPI» определяет, режет ли провайдер по имени сайта (обрыв/заморозка) и душит ли соединение по объёму/пакетам (TCP 16-20) — а не просто «сайт недоступен».";
    public string DpiVerdictText { get => _dpiVerdictText; private set => SetField(ref _dpiVerdictText, value); }

    private string _dpiVerdictDetail = "";
    public string DpiVerdictDetail { get => _dpiVerdictDetail; private set => SetField(ref _dpiVerdictDetail, value); }

    private DiagStatus _dpiVerdictStatus = DiagStatus.Pending;
    public DiagStatus DpiVerdictStatus { get => _dpiVerdictStatus; private set => SetField(ref _dpiVerdictStatus, value); }

    private async Task RunDpiCheckAsync()
    {
        if (IsDpiChecking) return;
        IsDpiChecking = true;
        DpiVerdictStatus = DiagStatus.Running;
        DpiVerdictText = "Проверяем DPI: имя сайта и лимит по объёму/пакетам (TCP 16-20)…";
        DpiVerdictDetail = "";
        _dpiCts = new CancellationTokenSource();
        try
        {
            var ct = _dpiCts.Token;
            var volTask = NetProbe.VolumeProbeAsync(ct);   // TCP 16-20 volume/packet limit — runs in parallel
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(DpiHosts.Select(async host =>
            {
                await gate.WaitAsync(ct);
                try { return (host, verdict: await NetProbe.DpiProbeAsync(host, ct)); }
                finally { gate.Release(); }
            }));
            var volume = await volTask;

            int reset = results.Count(r => r.verdict == DpiVerdict.Reset);
            int freeze = results.Count(r => r.verdict == DpiVerdict.Freeze);
            int clean = results.Count(r => r.verdict == DpiVerdict.Clean);

            DpiVerdictDetail = string.Join("\n",
                results.Select(r => $"{DpiGlyph(r.verdict)}  {r.host} — {DpiText(r.verdict)}")
                       .Append(VolumeLine(volume)));

            bool sniHit = reset + freeze > 0;
            bool volHit = volume == VolumeVerdict.Throttled;

            if (sniHit || volHit)
            {
                DpiVerdictStatus = DiagStatus.Fail;
                DpiVerdictText = BuildDpiVerdict(reset, freeze, volHit);
            }
            else if (clean > 0)
            {
                DpiVerdictStatus = DiagStatus.Ok;
                DpiVerdictText = IsRunning
                    ? "Признаков DPI-блокировки нет — ни по имени сайта, ни по объёму/пакетам. Либо провайдер не режет, либо обход уже её снимает."
                    : "Признаков DPI-блокировки нет — ни по имени сайта, ни по объёму/пакетам (TCP 16-20).";
            }
            else
            {
                DpiVerdictStatus = DiagStatus.Timeout;
                DpiVerdictText = "Нет соединения с хостами — похоже на проблему сети или блокировку по IP, а не DPI по имени.";
            }
        }
        catch (OperationCanceledException) { DpiVerdictText = "Проверка DPI остановлена."; DpiVerdictStatus = DiagStatus.Pending; }
        catch (Exception ex) { DpiVerdictText = "Ошибка проверки DPI: " + ex.Message; DpiVerdictStatus = DiagStatus.Pending; }
        finally
        {
            IsDpiChecking = false;
            _dpiCts?.Dispose();
            _dpiCts = null;
        }
    }

    private static string DpiGlyph(DpiVerdict v) => v switch
    {
        DpiVerdict.Clean => "✓",
        DpiVerdict.Reset or DpiVerdict.Freeze => "✗",
        _ => "•",
    };

    private static string DpiText(DpiVerdict v) => v switch
    {
        DpiVerdict.Clean => "чисто",
        DpiVerdict.Reset => "обрыв DPI (RST)",
        DpiVerdict.Freeze => "заморозка DPI",
        _ => "нет соединения",
    };

    private static string VolumeLine(VolumeVerdict v) => v switch
    {
        VolumeVerdict.Throttled => "✗  объём/пакеты (TCP 16-20) — душат после первых десятков КБ",
        VolumeVerdict.Ok        => "✓  объём/пакеты (TCP 16-20) — лимита не видно",
        _                       => "•  объём/пакеты (TCP 16-20) — проверить не удалось",
    };

    /// <summary>Combined verdict from the two independent axes: name-based DPI (RST/freeze on the
    /// SNI) and volume/packet limiting (TCP 16-20). At least one axis is a hit when this is called.</summary>
    private string BuildDpiVerdict(int reset, int freeze, bool volHit)
    {
        string sniHow = reset > 0 && freeze > 0 ? "обрыв (RST) и заморозка"
                      : reset > 0 ? "обрыв соединения (RST-инъекция)"
                      : freeze > 0 ? "заморозка (пакеты дропаются)"
                      : "";
        bool sniHit = sniHow.Length > 0;

        if (sniHit && volHit)
            return IsRunning
                ? $"Провайдер режет и по имени сайта ({sniHow}), и по объёму/пакетам (TCP 16-20). Обход включён, но не снимает — смените стратегию."
                : $"Провайдер режет и по имени сайта ({sniHow}), и по объёму/пакетам (TCP 16-20) — отсюда «работает, но тормозит и отваливается». Включите обход; учтите, что лимит по объёму TLS-обход снимает не всегда.";
        if (sniHit)
            return IsRunning
                ? $"Провайдер режет по DPI: {sniHow}. Обход включён, но эти хосты всё равно режутся — смените стратегию."
                : $"Провайдер режет по DPI: {sniHow}. Это снимается обходом — включите его.";
        // volume-limit only (SNI clean)
        return "По имени сайта провайдер не режет, но душит соединение по объёму/пакетам (TCP 16-20) — это про «открывается, но тормозит, буферит и отваливается на больших объёмах». Обход по TLS/SNI такое лечит не всегда; помогает смена стратегии или сети.";
    }

    // ---- custom targets (Диагностика tab) ---------------------------------

    private bool _showTargetPopup;
    public bool ShowTargetPopup { get => _showTargetPopup; set => SetField(ref _showTargetPopup, value); }

    private bool _targetIsNew = true;
    public bool TargetIsNew { get => _targetIsNew; private set => SetField(ref _targetIsNew, value); }

    private string _targetRootInput = "";
    public string TargetRootInput { get => _targetRootInput; set => SetField(ref _targetRootInput, value); }

    private string _targetDomainsText = "";
    public string TargetDomainsText { get => _targetDomainsText; set => SetField(ref _targetDomainsText, value); }

    private string _targetStatus = "";
    public string TargetStatus { get => _targetStatus; private set => SetField(ref _targetStatus, value); }

    private bool _isExpandingTarget;
    public bool IsExpandingTarget
    {
        get => _isExpandingTarget;
        private set
        {
            if (!SetField(ref _isExpandingTarget, value)) return;
            ExpandTargetCommand.RaiseCanExecuteChanged();
            SaveTargetCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _editingTargetName;

    private void ReloadTargets()
    {
        CustomTargets.Clear();
        foreach (var t in _targets.GetTargets()) CustomTargets.Add(t);
    }

    /// <summary>Diagnostics rows = the built-in service matrix + a "Свои цели" group (capped).</summary>
    private void RebuildDiagRows()
    {
        DiagRows.Clear();
        foreach (var r in DiagnosticsService.BuildRows()) DiagRows.Add(r);
        foreach (var d in _targets.AllDomains().Take(30))
            DiagRows.Add(new DiagRow { Group = "Свои цели", Name = d, Host = d });
    }

    private void OpenTargetPopup(CustomTarget? existing)
    {
        if (existing is null)
        {
            _editingTargetName = null;
            TargetIsNew = true;
            TargetRootInput = "";
            TargetDomainsText = "";
            TargetStatus = "Введите домен (например yandex.ru) и нажмите «Найти домены».";
        }
        else
        {
            _editingTargetName = existing.Name;
            TargetIsNew = false;
            TargetRootInput = existing.Name;
            TargetDomainsText = string.Join('\n', _targets.ReadDomains(existing.Name));
            TargetStatus = $"{existing.DomainCount} домен(ов). Можно отредактировать список или найти ещё.";
        }
        ShowTargetPopup = true;
    }

    private async Task ExpandTargetAsync()
    {
        string root = TargetService.Normalize(TargetRootInput);
        if (root.Length == 0) { TargetStatus = "Сначала укажите корневой домен."; return; }
        IsExpandingTarget = true;
        TargetStatus = $"Ищу домены для «{root}»…";
        try
        {
            // Progress<T> marshals back to this (UI) thread, so live status updates are safe to bind.
            var progress = new Progress<string>(msg => TargetStatus = msg);
            var found = await _targets.ExpandAsync(root, 40, progress, CancellationToken.None);
            var have = TargetDomainsText.Split('\n').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0);
            var merged = have.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            TargetDomainsText = string.Join('\n', merged);
            TargetStatus = $"Найдено: {found.Count}. Всего в списке: {merged.Count}. Проверьте и сохраните.";
        }
        catch (Exception ex) { TargetStatus = "Не удалось получить домены: " + ex.Message; }
        finally { IsExpandingTarget = false; }
    }

    private void SaveTarget()
    {
        string root = TargetService.Normalize(TargetRootInput);
        if (root.Length == 0) { TargetStatus = "Укажите корневой домен."; return; }
        var domains = TargetDomainsText.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (domains.Count == 0) domains.Add(root);

        // root renamed → drop the previous file so it isn't orphaned
        if (_editingTargetName is not null &&
            !string.Equals(_editingTargetName, root, StringComparison.OrdinalIgnoreCase))
            _targets.Delete(_editingTargetName);

        _targets.Save(root, domains);
        _editingTargetName = root;
        TargetIsNew = false;
        ReloadTargets();
        RebuildDiagRows();
        ShowTargetPopup = false;
        Notify?.Invoke("Цель сохранена",
            $"«{root}»: {domains.Count} домен(ов). Учитывается в диагностике, подборе и обходе.");
        if (IsRunning) _ = ApplyStrategyAsync(); // relaunch so the bypass covers the new domains
    }

    private void DeleteTarget(CustomTarget? t)
    {
        if (t is null) return;
        _targets.Delete(t.Name);
        ReloadTargets();
        RebuildDiagRows();
        if (IsRunning) _ = ApplyStrategyAsync();
    }

    /// <summary>Flatten an exception chain into one line. HttpRequestException hides the real cause
    /// behind "see inner exception", but a TLS reset from DPI ("forcibly closed") reads completely
    /// differently from an antivirus MITM ("remote certificate is invalid") or a skewed clock — we
    /// want that distinction in the Journal instead of the dead-end outer message.</summary>
    private static string DescribeError(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            string msg = e is System.Net.Sockets.SocketException se
                ? $"{e.Message} [{se.SocketErrorCode}]" : e.Message;
            if (parts.Count == 0 || !string.Equals(parts[^1], msg, StringComparison.Ordinal))
                parts.Add(msg);
        }
        return string.Join(" ← ", parts);
    }

    /// <summary>Whether a failure looks like a TLS/connection break (reset, cert error, socket error)
    /// rather than a plain HTTP/logic error — so the engine-download catch can add the non-obvious
    /// context (the zip host differs from the release page) and a workaround.</summary>
    private static bool LooksLikeTlsFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException
                or System.Net.Sockets.SocketException) return true;
            if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("reset", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
                string detail = DescribeError(ex);
                UpdateStatus = _updater.IsEngineInstalled
                    ? $"Не удалось проверить обновления ({detail}). Работаем на установленной версии."
                    : $"Нет связи с GitHub: {detail}";
                return;
            }

            if (!_updater.IsEngineInstalled || !_updater.IsEngineComplete || _updater.IsUpdateAvailable(latest))
            {
                bool wasRunning = IsRunning;
                if (wasRunning)
                {
                    AppendLog("Остановка движка для обновления…");
                    _engine.Stop();
                    // Wait for winws2 to FULLY exit before overwriting engine files (a File.Copy over a
                    // running winws2.exe throws) and before CanStart lets us relaunch — poll instead of a
                    // fixed delay, like ApplyStrategyAsync does.
                    for (int i = 0; i < 60 && State != EngineState.Stopped; i++)
                        await Task.Delay(50);
                    await Task.Delay(200);
                }

                var progress = new Progress<UpdateProgress>(p =>
                {
                    UpdateProgress = p.Fraction;
                    UpdateStatus = p.Message;
                });
                try
                {
                    await _updater.InstallAsync(latest, progress);
                }
                catch (Exception ex)
                {
                    string detail = DescribeError(ex);
                    UpdateStatus = $"Не удалось установить движок: {detail}";
                    AppendLog("Ошибка загрузки движка: " + detail);
                    if (LooksLikeTlsFailure(ex))
                        AppendLog("TLS-соединение оборвалось. Zip движка отдаётся с githubusercontent.com "
                                + "(не с github.com — поэтому страница релиза открывается, а загрузка нет). "
                                + "Частые причины: DPI-блокировка этого хоста провайдером или антивирус с "
                                + "проверкой HTTPS. Движка ещё нет, поэтому обход не поможет — скачайте его "
                                + "вручную или через VPN (см. инструкцию).");
                    return;
                }
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
        if (!ConfirmDialog.Show("Удалить список?",
                $"Список доменов «{SelectedHostlist}» будет удалён без возможности восстановления."))
            return;
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
        if (!ConfirmDialog.Show("Удалить пресет?",
                $"Пресет «{p.Name}» будет удалён без возможности восстановления."))
            return;
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

    /// <summary>Stop everything and release resources on application exit.</summary>
    public void Shutdown()
    {
        try { _diagCts?.Cancel(); } catch { }
        try { _dpiCts?.Cancel(); } catch { }
        try { _autoCts?.Cancel(); } catch { }
        try { _genCts?.Cancel(); } catch { }
        try { _monitor.Stop(); } catch { }
        try { _autoSelect.Dispose(); } catch { }
        try { _generator.Dispose(); } catch { }
        try { _tgProxy.Stop(); } catch { }
        try { _engine.Dispose(); } catch { }
    }

}
