namespace Zapret2UI.Models;

/// <summary>
/// A self-contained winws2 strategy. <see cref="Args"/> holds the command-line
/// arguments after the mandatory lua-init of the bundled libraries.
///
/// Tokens expanded by <c>EngineService</c> at launch time:
///   {FILES}    -> absolute path to the engine "files" folder (fake blobs)
///   {WF}       -> absolute path to the engine "windivert.filter" folder (raw-part filters)
///   {HOSTLIST} -> "--hostlist=&lt;path&gt;" for the active list, or removed if none
/// </summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>winws2 arguments (capture filters, --filter-*, --lua-desync=, --new, …).</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>True if this preset honours the selected hostlist via the {HOSTLIST} token.</summary>
    public bool UsesHostlist { get; set; }

    /// <summary>Built-in presets cannot be deleted (only duplicated/edited into a copy).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>The single preset the Simple mode applies with its one-click button.</summary>
    public bool IsRecommended { get; set; }

    /// <summary>True if assembled by the strategy generator (personalised). Marked ✨ in the list.</summary>
    public bool IsGenerated { get; set; }

    /// <summary>True for the auto-saved "top-3 of the last generation" presets — replaced (actualised)
    /// on every generation run so their scores stay current. Marked ★ and grouped separately.</summary>
    public bool IsAutoLeaderboard { get; set; }

    /// <summary>Section title for grouping in the presets list.</summary>
    public string GroupTitle => !IsBuiltIn
        ? (IsAutoLeaderboard ? "★ Лучшие из последней генерации"
           : IsGenerated ? "✨ Сгенерировано автоподбором"
           : "Личные (созданные)")
        : "Основные (Discord / YouTube)";

    public Preset Clone() => new()
    {
        Name = Name,
        Description = Description,
        Args = new List<string>(Args),
        UsesHostlist = UsesHostlist,
        IsGenerated = IsGenerated,
        IsBuiltIn = false
    };
}
