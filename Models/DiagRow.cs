using ZapretUI.Mvvm;

namespace ZapretUI.Models;

public enum DiagStatus { Pending, Running, Ok, Fail, Timeout, NotSupported, Skip }

/// <summary>
/// One row of the diagnostics matrix: a named endpoint and the live result of
/// each probe (HTTP / TLS1.2 / TLS1.3 / ping). Properties are observable so the
/// table updates as the run progresses — WPF marshals item PropertyChanged to
/// the UI thread automatically, so probes may write these from worker threads.
/// </summary>
public sealed class DiagRow : ObservableObject
{
    public required string Group { get; init; }   // "Discord"
    public required string Name { get; init; }     // "Gateway"
    public required string Host { get; init; }     // "gateway.discord.gg"

    /// <summary>Ping-only endpoints (DNS resolvers) skip HTTP/TLS columns.</summary>
    public bool PingOnly { get; init; }

    private DiagStatus _http = DiagStatus.Pending;
    public DiagStatus Http { get => _http; set => SetField(ref _http, value); }

    private DiagStatus _tls12 = DiagStatus.Pending;
    public DiagStatus Tls12 { get => _tls12; set => SetField(ref _tls12, value); }

    private DiagStatus _tls13 = DiagStatus.Pending;
    public DiagStatus Tls13 { get => _tls13; set => SetField(ref _tls13, value); }

    private DiagStatus _ping = DiagStatus.Pending;
    public DiagStatus Ping { get => _ping; set => SetField(ref _ping, value); }

    private string _pingText = "";
    public string PingText { get => _pingText; set => SetField(ref _pingText, value); }

    public void Reset()
    {
        Http = PingOnly ? DiagStatus.Skip : DiagStatus.Pending;
        Tls12 = PingOnly ? DiagStatus.Skip : DiagStatus.Pending;
        Tls13 = PingOnly ? DiagStatus.Skip : DiagStatus.Pending;
        Ping = DiagStatus.Pending;
        PingText = "";
    }
}
