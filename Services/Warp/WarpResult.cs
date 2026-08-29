namespace Zapret2UI.Services.Warp;

/// <summary>How a WARP operation ended, in words the UI can show as-is.
///
/// <para>Lives on its own rather than beside a service because the transport underneath it changed once
/// already — the WireGuard implementation this began as could not carry traffic on a censored network at
/// all, and MASQUE replaced it — while what the user is told about the outcome did not.</para>
/// </summary>
public readonly record struct WarpResult(bool Ok, string Message)
{
    public static WarpResult Fail(string message) => new(false, message);
    public static WarpResult Success(string message) => new(true, message);
}
