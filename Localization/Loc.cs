using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Zapret2UI.Localization;

/// <summary>
/// Ultra-light UI localization. The app ships its Russian strings inline — the Russian source string
/// <em>is</em> the lookup key — and an embedded RU→EN table (<c>strings.en.json</c>) is loaded only when
/// the user picks English. Anything absent from the table falls back to the Russian source, so a partial
/// translation is always safe (never a blank or a raw key).
///
/// The language is chosen once, at startup (<see cref="Init"/>), because switching is restart-to-apply
/// (the XAML <c>{loc:Loc …}</c> extension resolves at parse time). Lookups therefore never change
/// mid-run, and callers may treat <see cref="T(string)"/> as pure. Default and any unknown value = Russian.
/// </summary>
public static class Loc
{
    public const string Russian = "ru";
    public const string English = "en";

    private static readonly Dictionary<string, string> EmptyMap = new(0);

    private static string _lang = Russian;
    private static IReadOnlyDictionary<string, string> _map = EmptyMap;

    /// <summary>The active UI language: "ru" (default) or "en".</summary>
    public static string Lang => _lang;

    /// <summary>True when the app is running in English (the RU→EN table is loaded).</summary>
    public static bool IsEnglish => _lang == English;

    /// <summary>Pick the language for this run. Only <c>"en"</c> loads the table; anything else is Russian.
    /// Best-effort: a missing or malformed table leaves the app on Russian rather than throwing.</summary>
    public static void Init(string? lang)
    {
        _lang = lang == English ? English : Russian;
        _map = _lang == English ? LoadEmbedded() : EmptyMap;
    }

    /// <summary>Translate a Russian source string: the English text when running in English and a mapping
    /// exists, otherwise the Russian source unchanged.</summary>
    public static string T(string ru)
        => _map.TryGetValue(ru, out string? en) && en.Length > 0 ? en : ru;

    /// <summary>Translate a composite-format template (<c>{0}</c>, <c>{1}</c>…) and fill it. Use this
    /// overload only for strings that actually carry placeholders — literal braces in the template must
    /// be doubled (<c>{{</c>, <c>}}</c>), exactly as <see cref="string.Format(string, object?[])"/> requires.</summary>
    public static string T(string ru, params object?[] args)
        => string.Format(T(ru), args);

    private static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        try
        {
            // Logical name = <RootNamespace>.<folder>.<file> = Zapret2UI.Localization.strings.en.json
            Assembly asm = Assembly.GetExecutingAssembly();
            using Stream? s = asm.GetManifestResourceStream("Zapret2UI.Localization.strings.en.json");
            if (s is null) return EmptyMap;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            return dict is { Count: > 0 } ? dict : EmptyMap;
        }
        catch
        {
            return EmptyMap;
        }
    }
}
