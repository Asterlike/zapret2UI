using Zapret2UI.Models;
using Zapret2UI.Mvvm;

namespace Zapret2UI.ViewModels;

/// <summary>
/// One target endpoint in the check popup's main area, with its TLS 1.2 / 1.3
/// probe state filling in live as the current candidate is tested.
/// </summary>
public sealed class CheckTargetRow : ObservableObject
{
    public string Host { get; }

    public CheckTargetRow(string host) => Host = host;

    private DiagStatus _tls12 = DiagStatus.Pending;
    public DiagStatus Tls12 { get => _tls12; set => SetField(ref _tls12, value); }

    private DiagStatus _tls13 = DiagStatus.Pending;
    public DiagStatus Tls13 { get => _tls13; set => SetField(ref _tls13, value); }

    // Real browser-like HTTP/2 reachability (marker-checked) — the page actually loads, not just a handshake.
    private DiagStatus _http = DiagStatus.Pending;
    public DiagStatus Http { get => _http; set => SetField(ref _http, value); }
}
