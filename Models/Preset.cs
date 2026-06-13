namespace ZapretUI.Models;

/// <summary>
/// A self-contained winws2 strategy. <see cref="Args"/> holds the command-line
/// arguments after the mandatory lua-init of the bundled libraries.
///
/// Tokens expanded by <c>EngineService</c> at launch time:
///   {FILES}    -> absolute path to the engine "files" folder (fake blobs)
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

    public Preset Clone() => new()
    {
        Name = Name,
        Description = Description,
        Args = new List<string>(Args),
        UsesHostlist = UsesHostlist,
        IsBuiltIn = false
    };
}
