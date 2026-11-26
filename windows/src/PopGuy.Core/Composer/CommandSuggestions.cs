// The Terminal Composer's command suggestions (pure model), as in
// macos/PopGuy/Modules/TerminalComposer/CommandSuggestions.swift:
//   - the token at the caret: a trigger ("/" or "$") at the start of the text or right after whitespace, followed by
//     non-whitespace up to the caret. Claude Code and Gemini "/" only as the first token of the prompt; Codex "$" at
//     any token start.
//   - the ranking of the CLI's catalog items: only items whose name contains the query; name prefix first, then a
//     description match, then the name.
//   - the edit that accepts an item: the token becomes "<trigger><name> ".
// Positions are C# string indices (UTF-16). Whitespace includes RichEdit's "\r" paragraph mark and "\v" soft break.

using System.Globalization;

namespace PopGuy.Core.Composer;

public static class CommandSuggestions
{
    /// <summary>The token being typed: <c>Length</c> characters from <c>Start</c> (the trigger up to the caret);
    /// <c>Query</c> is the text after the trigger.</summary>
    public readonly record struct Token(int Start, int Length, char Trigger, string Query);

    private static readonly IReadOnlySet<char> Slash = new HashSet<char> { '/' };
    private static readonly IReadOnlySet<char> Dollar = new HashSet<char> { '$' };
    private static readonly IReadOnlySet<char> None = new HashSet<char>();

    private const CompareOptions MatchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>The triggers of a catalog's agent; none for an unknown agent.</summary>
    public static (IReadOnlySet<char> Triggers, bool SlashOnlyAtStart) Triggers(string agent) => agent switch
    {
        "claude" or "gemini" => (Slash, true),
        "codex" => (Dollar, false),
        _ => (None, false),
    };

    /// <summary>The token ending at <paramref name="caret"/>; null when there is none.</summary>
    public static Token? ActiveToken(string text, int caret, IReadOnlySet<char> triggers, bool slashOnlyAtStart)
    {
        if (caret <= 0 || caret > text.Length) return null;
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start == caret || !triggers.Contains(text[start])) return null;
        var trigger = text[start];
        if (trigger == '/' && slashOnlyAtStart && !string.IsNullOrWhiteSpace(text[..start])) return null;
        return new Token(start, caret - start, trigger, text[(start + 1)..caret]);
    }

    /// <summary>The items with <paramref name="trigger"/> whose name contains <paramref name="query"/> (ignoring case
    /// and diacritics), best first; all of them for an empty query.</summary>
    public static IReadOnlyList<CatalogItem> Rank(IEnumerable<CatalogItem> items, char trigger, string query)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var triggerText = trigger.ToString();
        return items
            .Where(i => i.Trigger == triggerText
                && (query.Length == 0 || compare.IndexOf(i.Name, query, MatchOptions) >= 0))
            .OrderBy(i => query.Length == 0 || compare.IsPrefix(i.Name, query, MatchOptions) ? 0 : 1)
            .ThenBy(i => query.Length > 0 && compare.IndexOf(i.Description, query, MatchOptions) >= 0 ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The edit that replaces <paramref name="token"/> with <c>&lt;trigger&gt;&lt;name&gt; </c>.</summary>
    public static (int Start, int Length, string Text) AcceptEdit(Token token, CatalogItem item) =>
        (token.Start, token.Length, $"{token.Trigger}{item.Name} ");
}
