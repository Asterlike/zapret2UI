namespace Zapret2UI.Startup;

/// <summary>
/// The handful of switches the executable understands. Six of them are one-shot headless modes and
/// each was previously unpacked by hand at the top of <c>OnStartup</c>, which is how a bounds check
/// gets forgotten — so the unpacking lives here once instead.
/// </summary>
internal static class CommandLine
{
    /// <summary>Is <paramref name="flag"/> present at all? Case-insensitive, like every other way
    /// Windows hands us a command line.</summary>
    internal static bool Has(string[] args, string flag) =>
        Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The argument that follows <paramref name="flag"/>, or <c>null</c> when the flag is
    /// absent or was given last with nothing after it.</summary>
    internal static string? Value(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>True when <paramref name="flag"/> is present, with <paramref name="value"/> set to the
    /// argument after it — or to <paramref name="fallback"/> when the flag was given on its own.</summary>
    internal static bool TryValue(string[] args, string flag, string fallback, out string value)
    {
        value = Value(args, flag) ?? fallback;
        return Has(args, flag);
    }
}
