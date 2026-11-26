// The Terminal Composer's "/" and "$" catalog, built by popguy-edit.exe (PopGuy.Edit links this file) from what is
// on the machine: Claude Code commands, skills and enabled plugins, Gemini CLI custom commands, and Codex's
// skills/list reply. Self-contained (BCL only, besides ComposerProtocol's types) and trim safe: JSON is read with
// System.Text.Json's document model, never reflection. Same rules as
// macos/PopGuy/Modules/TerminalComposer/ComposerCatalog.swift, except that a linked folder is never entered inside a
// recursive commands walk (one-level skills/ and extensions/ listings follow links).
//
// Every path is passed in by the caller (config folders, cwd); nothing here reads the real home folder on its own.
// File and folder errors are ignored: a source that cannot be read adds no items.

using System.Security;
using System.Text;
using System.Text.Json;

namespace PopGuy.Core.Composer;

public static class ComposerCatalog
{
    /// <summary>Walk caps per source: folder depth below the source root, and entries visited.</summary>
    private const int MaxDepth = 4;
    private const int MaxEntriesPerSource = 2_000;
    /// <summary>Read caps: a command or skill file, a JSON settings file, and the global config (.claude.json, which
    /// grows with use).</summary>
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxJsonBytes = 1_048_576;
    private const int MaxGlobalConfigBytes = 16 * 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly EnumerationOptions ListOptions = new() { IgnoreInaccessible = true };

    /// <summary>The agent that named this temp file: Claude Code writes <c>claude-prompt-*.md</c>, Gemini CLI a file
    /// in a <c>gemini-edit-*</c> folder. Null when the name says nothing (the caller then looks at its ancestors).</summary>
    public static string? DetectAgentFromFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (name.StartsWith("claude-prompt-", StringComparison.Ordinal) && name.EndsWith(".md", StringComparison.Ordinal))
            return "claude";
        var folder = Path.GetFileName(Path.GetDirectoryName(filePath));
        return folder is not null && folder.StartsWith("gemini-edit-", StringComparison.Ordinal) ? "gemini" : null;
    }

    // Claude Code

    /// <summary>Claude Code's "/" items: the project's (<c>cwd/.claude</c>) and the user's (<paramref name="configDir"/>:
    /// CLAUDE_CONFIG_DIR or the profile's .claude folder) commands and skills, then the signed-in account's synced skills
    /// as <c>anthropic-skills:name</c> (account read from <paramref name="globalConfigFile"/>, the .claude.json beside or
    /// in the config folder; null: none), then the enabled plugins' ones as <c>plugin:name</c>. Finished
    /// (<see cref="Finish"/>): project items win over user, synced and plugin ones.</summary>
    public static IReadOnlyList<CatalogItem> ClaudeItems(string configDir, string cwd, string? globalConfigFile = null)
    {
        var items = new List<CatalogItem>();
        AddClaudeFolder(items, Path.Combine(cwd, ".claude"), "");
        AddClaudeFolder(items, configDir, "", skipSynced: true);
        if (globalConfigFile is not null && SyncedSkillsFolder(configDir, globalConfigFile) is { } synced)
            AddSkills(items, synced, "anthropic-skills:");
        foreach (var (plugin, installPath) in EnabledPlugins(configDir, cwd))
            AddClaudeFolder(items, installPath, plugin + ":");
        return Finish(items);
    }

    /// <summary><c>root/commands/**/*.md</c> (folders joined with ":") and <c>root/skills/*/SKILL.md</c>; with
    /// <paramref name="skipSynced"/> (the user's config folder), never <c>skills/synced</c>: synced skills are listed
    /// from their account's folder only.</summary>
    private static void AddClaudeFolder(List<CatalogItem> items, string root, string prefix, bool skipSynced = false)
    {
        foreach (var (name, path) in FilesUnder(Path.Combine(root, "commands"), ".md"))
        {
            var frontmatter = ReadFrontmatter(ReadText(path, MaxFileBytes) ?? "");
            items.Add(new CatalogItem("/", Prefixed(prefix, name), frontmatter.GetValueOrDefault("description") ?? ""));
        }
        AddSkills(items, Path.Combine(root, "skills"), prefix, skipSynced);
    }

    /// <summary><c>skillsDir/*/SKILL.md</c>, except the user-invocable: false ones (and the <c>synced</c> folder with
    /// <paramref name="skipSynced"/>).</summary>
    private static void AddSkills(List<CatalogItem> items, string skillsDir, string prefix, bool skipSynced = false)
    {
        foreach (var folder in Subfolders(skillsDir))
        {
            if (skipSynced && Path.GetFileName(folder) == "synced" || ReadText(Path.Combine(folder, "SKILL.md"), MaxFileBytes) is not { } text)
                continue;
            var frontmatter = ReadFrontmatter(text);
            if (string.Equals(frontmatter.GetValueOrDefault("user-invocable"), "false", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = frontmatter.GetValueOrDefault("name") is { Length: > 0 } declared ? declared : Path.GetFileName(folder);
            items.Add(new CatalogItem("/", Prefixed(prefix, name), frontmatter.GetValueOrDefault("description") ?? ""));
        }
    }

    /// <summary>A name that already starts with <paramref name="prefix"/> keeps it once.</summary>
    private static string Prefixed(string prefix, string name) =>
        name.StartsWith(prefix, StringComparison.Ordinal) ? name : prefix + name;

    /// <summary><c>configDir/skills/synced/&lt;organizationUuid&gt;_&lt;accountUuid&gt;</c> for the global config's
    /// <c>oauthAccount</c>, or null when either id is missing or not a UUID (they become a path).</summary>
    private static string? SyncedSkillsFolder(string configDir, string globalConfigFile)
    {
        using var config = ReadJson(globalConfigFile, MaxGlobalConfigBytes);
        if (config is not { RootElement: { ValueKind: JsonValueKind.Object } root }
            || !root.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object
            || StringProperty(account, "organizationUuid") is not { } organization || !IsUuid(organization)
            || StringProperty(account, "accountUuid") is not { } user || !IsUuid(user))
            return null;
        return Path.Combine(configDir, "skills", "synced", organization + "_" + user);
    }

    /// <summary>Exactly 8-4-4-4-12 hex digits.</summary>
    private static bool IsUuid(string value)
    {
        if (value.Length != 36)
            return false;
        for (var i = 0; i < value.Length; i++)
        {
            var dash = i is 8 or 13 or 18 or 23;
            if (dash ? value[i] != '-' : !char.IsAsciiHexDigit(value[i]))
                return false;
        }
        return true;
    }

    /// <summary>The installed plugins (installed_plugins.json, version 2) that <c>enabledPlugins</c> turns on, after
    /// merging the user's settings.json, then the project's settings.json and settings.local.json (later wins per key).
    /// Each yields its plugin name (before "@") and the first install path that exists.</summary>
    private static List<(string Plugin, string InstallPath)> EnabledPlugins(string configDir, string cwd)
    {
        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        string[] settingsFiles =
        [
            Path.Combine(configDir, "settings.json"),
            Path.Combine(cwd, ".claude", "settings.json"),
            Path.Combine(cwd, ".claude", "settings.local.json"),
        ];
        foreach (var file in settingsFiles)
        {
            using var settings = ReadJson(file);
            if (settings is { RootElement: { ValueKind: JsonValueKind.Object } root }
                && root.TryGetProperty("enabledPlugins", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                    enabled[entry.Name] = entry.Value.ValueKind == JsonValueKind.True;
            }
        }

        var result = new List<(string, string)>();
        using var installed = ReadJson(Path.Combine(configDir, "plugins", "installed_plugins.json"));
        if (installed is not { RootElement: { ValueKind: JsonValueKind.Object } installedRoot }
            || !installedRoot.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var plugin in plugins.EnumerateObject())
        {
            var name = plugin.Name.Split('@')[0];
            if (name.Length == 0 || !enabled.GetValueOrDefault(plugin.Name) || plugin.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var install in plugin.Value.EnumerateArray())
            {
                if (install.ValueKind == JsonValueKind.Object
                    && install.TryGetProperty("installPath", out var pathValue)
                    && pathValue.ValueKind == JsonValueKind.String
                    && TryGetString(pathValue) is { Length: > 0 } path
                    && Directory.Exists(path))
                {
                    result.Add((name, path));
                    break;
                }
            }
        }
        return result;
    }

    // Gemini CLI

    /// <summary>Gemini CLI's "/" items: the project's (<c>cwd/.gemini/commands</c>, unless trustedFolders.json marks
    /// the cwd or a parent DO_NOT_TRUST), the user's (<paramref name="geminiDir"/>/commands) and each extension's
    /// TOML commands. Finished (<see cref="Finish"/>): project commands win over user and extension ones.</summary>
    public static IReadOnlyList<CatalogItem> GeminiItems(string geminiDir, string cwd)
    {
        var items = new List<CatalogItem>();
        if (!IsDistrusted(geminiDir, cwd))
            AddGeminiCommands(items, Path.Combine(cwd, ".gemini", "commands"));
        AddGeminiCommands(items, Path.Combine(geminiDir, "commands"));
        foreach (var extension in Subfolders(Path.Combine(geminiDir, "extensions")))
            AddGeminiCommands(items, Path.Combine(extension, "commands"));
        return Finish(items);
    }

    private static void AddGeminiCommands(List<CatalogItem> items, string root)
    {
        foreach (var (name, path) in FilesUnder(root, ".toml"))
            items.Add(new CatalogItem("/", name, TomlDescription(ReadText(path, MaxFileBytes) ?? "")));
    }

    /// <summary>True when trustedFolders.json maps <paramref name="cwd"/> or one of its parents to DO_NOT_TRUST.</summary>
    private static bool IsDistrusted(string geminiDir, string cwd)
    {
        using var trust = ReadJson(Path.Combine(geminiDir, "trustedFolders.json"));
        if (trust is not { RootElement: { ValueKind: JsonValueKind.Object } root })
            return false;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var distrusted = new HashSet<string>(comparer);
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String && TryGetString(entry.Value) == "DO_NOT_TRUST"
                && NormalizedPath(entry.Name) is { } path)
                distrusted.Add(path);
        }
        for (var folder = NormalizedPath(cwd); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (distrusted.Contains(folder))
                return true;
        }
        return false;
    }

    private static string? NormalizedPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>The top-level single-line <c>description = "…"</c> or <c>'…'</c> before any table header, else "".</summary>
    internal static string TomlDescription(string text)
    {
        var inMultiline = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            // A """ or ''' string spans lines: skip what is inside it.
            var wasInMultiline = inMultiline;
            if ((Count(line, "\"\"\"") + Count(line, "'''")) % 2 == 1)
                inMultiline = !inMultiline;
            if (wasInMultiline || line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
                return "";
            if (!line.StartsWith("description", StringComparison.Ordinal))
                continue;
            var rest = line["description".Length..].TrimStart();
            if (!rest.StartsWith('='))
                continue;
            rest = rest[1..].TrimStart();
            if (rest.StartsWith("\"\"\"", StringComparison.Ordinal) || rest.StartsWith("'''", StringComparison.Ordinal))
                return "";
            return QuotedValue(rest) ?? "";
        }
        return "";
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // Codex

    /// <summary>Codex's "$" items from its app-server <c>skills/list</c> JSON-RPC response
    /// (<c>result.data[].skills[]</c>): enabled skills only. Malformed entries are skipped; a malformed reply is empty.
    /// Finished (<see cref="Finish"/>).</summary>
    public static IReadOnlyList<CatalogItem> ParseSkillsListReply(string json)
    {
        var items = new List<CatalogItem>();
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            if (document.RootElement is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return items;
            foreach (var group in data.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object
                    || !group.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var skill in skills.EnumerateArray())
                {
                    if (skill.ValueKind == JsonValueKind.Object
                        && skill.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True
                        && StringProperty(skill, "name") is { } name)
                        items.Add(new CatalogItem("$", name, StringProperty(skill, "description") ?? ""));
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: no items.
        }
        return Finish(items);
    }

    // Finishing

    /// <summary>Makes items fit the protocol's caps: descriptions on one line (control and line-separator characters
    /// become spaces, whitespace runs collapse) and cut to <see cref="ComposerMessage.MaxItemDescriptionLength"/>;
    /// items with an empty, over-long or spaced name dropped; duplicates (same trigger and name) dropped, first wins,
    /// so callers list project items before user items before plugin items; sorted by name; at most
    /// <see cref="ComposerMessage.MaxCatalogItems"/>. Idempotent, so merged lists may be finished again.</summary>
    public static IReadOnlyList<CatalogItem> Finish(IEnumerable<CatalogItem> items)
    {
        var seen = new HashSet<(string, string)>();
        var kept = new List<CatalogItem>();
        foreach (var item in items)
        {
            if (item.Trigger is not ("/" or "$") || !IsValidName(item.Name) || !seen.Add((item.Trigger, item.Name)))
                continue;
            kept.Add(item with { Description = OneLine(item.Description) });
        }
        return kept
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Trigger, StringComparer.Ordinal)
            .Take(ComposerMessage.MaxCatalogItems)
            .ToList();
    }

    private static bool IsValidName(string name)
    {
        if (name.Length is 0 or > ComposerMessage.MaxItemNameLength)
            return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                i++;
            else if (char.IsSurrogate(c))
                return false;
        }
        return true;
    }

    /// <summary>One trimmed line with single spaces, cut without splitting a surrogate pair; a lone surrogate
    /// becomes U+FFFD so the line encodes.</summary>
    private static string OneLine(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, ComposerMessage.MaxItemDescriptionLength + 2));
        var gap = false;
        for (var i = 0; i < text.Length && builder.Length <= ComposerMessage.MaxItemDescriptionLength; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                gap = true;
                continue;
            }
            if (gap && builder.Length > 0)
                builder.Append(' ');
            gap = false;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                builder.Append(c).Append(text[++i]);
            else
                builder.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }
        if (builder.Length <= ComposerMessage.MaxItemDescriptionLength)
            return builder.ToString();
        var cut = ComposerMessage.MaxItemDescriptionLength;
        if (char.IsHighSurrogate(builder[cut - 1]))
            cut--;
        return builder.ToString(0, cut).TrimEnd();
    }

    // Frontmatter

    /// <summary>The keys of a YAML frontmatter block (first line exactly "---", up to the next "---" line). Values
    /// may be plain, single- or double-quoted, folded (<c>&gt;</c>, joined with spaces) or literal (<c>|</c>) block
    /// scalars; continuation lines are the indented lines that follow a key. Nested maps and lists are not
    /// understood (their key's value is empty); no block means no keys.</summary>
    internal static Dictionary<string, string> ReadFrontmatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
            return values;
        var end = Array.FindIndex(lines, 1, line => line.TrimEnd() == "---");
        if (end < 0)
            return values;

        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            var head = line[(colon + 1)..].Trim();

            var next = i + 1;
            while (next < end && (lines[next].Length == 0 || char.IsWhiteSpace(lines[next][0])))
                next++;
            var continuation = lines[(i + 1)..next];
            i = next - 1;

            values[key] = head switch
            {
                ['>', ..] => string.Join(' ', BlockLines(continuation).Where(part => part.Length > 0)),
                ['|', ..] => string.Join('\n', BlockLines(continuation)).TrimEnd('\n'),
                ['"', ..] or ['\'', ..] => QuotedValue(Joined(head, continuation)) ?? "",
                _ => Joined(StripComment(head), continuation),
            };
        }
        return values;
    }

    /// <summary>A block scalar's lines without their common indentation.</summary>
    private static IEnumerable<string> BlockLines(string[] lines)
    {
        var indent = lines.Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return lines.Select(line => line.Length >= indent ? line[indent..].TrimEnd() : "");
    }

    /// <summary>A flow value folded over continuation lines: joined with single spaces.</summary>
    private static string Joined(string head, string[] continuation) =>
        string.Join(' ', continuation.Select(line => line.Trim()).Where(line => line.Length > 0).Prepend(head)
            .Where(part => part.Length > 0));

    private static string StripComment(string plain)
    {
        var index = plain.IndexOf(" #", StringComparison.Ordinal);
        return index >= 0 ? plain[..index].TrimEnd() : plain;
    }

    /// <summary>The string a leading <c>"…"</c> (with \" \\ \n \t escapes) or <c>'…'</c> (with '' for a quote) holds;
    /// null when it is not quoted or never closes.</summary>
    private static string? QuotedValue(string text)
    {
        if (text.Length == 0 || text[0] is not ('"' or '\''))
            return null;
        var quote = text[0];
        var builder = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'' && c == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i++;
                    continue;
                }
                return builder.ToString();
            }
            if (quote == '"' && c == '"')
                return builder.ToString();
            if (quote == '"' && c == '\\' && i + 1 < text.Length)
            {
                var escaped = text[++i];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => escaped,
                });
                continue;
            }
            builder.Append(c);
        }
        return null;
    }

    // Disk access: bounded, silent on errors.

    /// <summary>Files with <paramref name="extension"/> under <paramref name="root"/>, named by their relative path
    /// without the extension, folders joined with ":". Hidden entries (".name") are skipped and linked folders are
    /// not entered; at most <see cref="MaxDepth"/> folders deep and <see cref="MaxEntriesPerSource"/> entries.</summary>
    private static List<(string Name, string Path)> FilesUnder(string root, string extension)
    {
        var found = new List<(string, string)>();
        var budget = MaxEntriesPerSource;
        try
        {
            if (Directory.Exists(root))
                Walk(new DirectoryInfo(root), "", 0, extension, found, ref budget);
        }
        catch (Exception e) when (IsIgnorable(e))
        {
            // Unreadable source: what was found so far stays.
        }
        return found;
    }

    private static void Walk(DirectoryInfo folder, string prefix, int depth, string extension,
        List<(string, string)> found, ref int budget)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = folder.EnumerateFileSystemInfos("*", ListOptions)
                .Take(budget)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (IsIgnorable(e))
        {
            return;
        }
        foreach (var entry in entries)
        {
            if (budget-- <= 0)
                return;
            if (entry.Name.StartsWith('.'))
                continue;
            if (entry is DirectoryInfo subfolder)
            {
                if (depth < MaxDepth && (subfolder.Attributes & FileAttributes.ReparsePoint) == 0)
                    Walk(subfolder, prefix + subfolder.Name + ":", depth + 1, extension, found, ref budget);
            }
            else if (entry.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                found.Add((prefix + Path.GetFileNameWithoutExtension(entry.Name), entry.FullName));
            }
        }
    }

    /// <summary>The non-hidden folders directly in <paramref name="root"/> (a linked one included: nothing below it is
    /// walked), sorted, at most <see cref="MaxEntriesPerSource"/>.</summary>
    private static List<string> Subfolders(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];
            return Directory.EnumerateDirectories(root, "*", ListOptions)
                .Take(MaxEntriesPerSource)
                .Where(path => !Path.GetFileName(path).StartsWith('.'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (IsIgnorable(e))
        {
            return [];
        }
    }

    /// <summary>The first <paramref name="maxBytes"/> of a regular file as LF text (lenient UTF-8, BOM dropped), or
    /// null when it cannot be read.</summary>
    private static string? ReadText(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!stream.CanSeek)
                return null;
            var buffer = new byte[Math.Min(maxBytes, Math.Max(0, stream.Length))];
            var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var text = Encoding.UTF8.GetString(buffer, 0, count);
            if (text.StartsWith('\uFEFF'))
                text = text[1..];
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch (Exception e) when (IsIgnorable(e))
        {
            return null;
        }
    }

    /// <summary>A JSON file parsed as a document (the caller disposes it), or null when missing, over
    /// <paramref name="maxBytes"/> or not JSON.</summary>
    private static JsonDocument? ReadJson(string path, int maxBytes = MaxJsonBytes)
    {
        if (ReadText(path, maxBytes + 1) is not { } text || text.Length > maxBytes)
            return null;
        try
        {
            return JsonDocument.Parse(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? TryGetString(value) : null;

    /// <summary>The string, or null when it holds an invalid escape (GetString throws on a lone surrogate escape).</summary>
    private static string? TryGetString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsIgnorable(Exception e) =>
        e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
