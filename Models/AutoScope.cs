namespace ZapretUI.Models;

/// <summary>What the auto-selector optimises for: a strategy that works for both
/// services at once, or just one of them.</summary>
public enum AutoScope { Both, Discord, YouTube }

public static class AutoScopeExt
{
    public static string Title(this AutoScope s) => s switch
    {
        AutoScope.Discord => "Discord",
        AutoScope.YouTube => "YouTube",
        _ => "Discord + YouTube",
    };

    /// <summary>Endpoints used to score a candidate for this scope (TLS-tested hosts).</summary>
    public static string[] GoalHosts(this AutoScope s)
    {
        string[] discord = { "discord.com", "gateway.discord.gg", "cdn.discordapp.com" };
        string[] youtube = { "www.youtube.com", "i.ytimg.com" };
        return s switch
        {
            AutoScope.Discord => discord,
            AutoScope.YouTube => youtube,
            _ => discord.Concat(youtube).ToArray(),
        };
    }
}
