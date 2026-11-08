using System.Text.RegularExpressions;

namespace SessionArchive;

/// <summary>One place Claude keeps data on this machine, copied to raw/&lt;Name&gt;/.</summary>
/// <param name="Include">Decides on a path relative to <see cref="Root"/> ('/'-separated). A directory is
/// entered when its own relative path passes.</param>
/// <param name="Scrub">Files (relative path) whose JSON has its sign-in values removed on the way.</param>
public sealed record Source(string Name, string Root, string Description,
    Func<string, bool, bool> Include, Func<string, bool> Scrub, bool SingleLevel = false);

public static partial class Sources
{
    public static readonly string[] AlwaysExcluded =
    [
        @"~\.claude\.credentials.json (Claude Code's sign-in token)",
        @"the 'oauth:*' keys of %APPDATA%\Claude\config.json, and any key named like a token, secret, password or API key in ~\.claude.json (sign-in values)",
        @"%APPDATA%\Claude's program files, VM images and browser caches (claude-code, claude-code-vm, vm_bundles, Cache, GPUCache, IndexedDB, Local Storage, ...): no session data",
        "files outside these folders: a file a session read or wrote elsewhere is linked by its path, not copied",
    ];

    // Folders of %APPDATA%\Claude that hold session data (the rest is the app itself and Chromium state).
    static readonly HashSet<string> DesktopFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-code-sessions", "local-agent-mode-sessions", "side-session-notes", "spaces-present",
        "git-shadow", "logs",
    };

    public static List<Source> Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var temp = Path.Combine(Path.GetTempPath(), "claude");
        return
        [
            new("claude-home", Path.Combine(home, ".claude"),
                "Claude Code's own folder: transcripts and sub-agents (projects), memory, tool results, file history, uploads, plans, skills, settings",
                (rel, _) => !rel.Equals(".credentials.json", StringComparison.OrdinalIgnoreCase),
                _ => false),
            new("claude-json", home,
                @"~\.claude.json and its backups: Claude Code's global state (projects, history of settings)",
                (rel, isDir) => !isDir && !rel.Contains('/') && rel.StartsWith(".claude.json", StringComparison.OrdinalIgnoreCase),
                _ => true, SingleLevel: true),
            new("desktop", Path.Combine(appData, "Claude"),
                "The desktop app's session records (titles, archived, forks, Cowork, scheduled tasks), its git shadow and logs",
                (rel, isDir) =>
                {
                    var first = rel.Split('/')[0];
                    if (DesktopFolders.Contains(first)) return true;
                    return !isDir && !rel.Contains('/') && rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                },
                rel => rel.Equals("config.json", StringComparison.OrdinalIgnoreCase)),
            new("temp", temp,
                "Each session's scratchpad, background-task output, bundled skills and edit diffs",
                (_, _) => true, _ => false),
        ];
    }

    /// <summary>Property names that carry sign-in values in the scrubbed JSON files.</summary>
    [GeneratedRegex("(?i)(oauth|token|api[-_]?key|secret|password|passwd|credential|cookie|session[-_]?key|bearer)")]
    public static partial Regex SecretName();
}
