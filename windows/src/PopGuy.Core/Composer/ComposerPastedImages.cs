// The Terminal Composer's short badges for its own pasted images (macOS ComposerPastedImages.swift). An image pasted into
// the composer is saved in its image folder and mentioned by its long path; in the editor that mention shows as
// "[Pasted image #N]" instead, and goes back to the full mention wherever the text leaves the composer (Done, Copy &
// Close, drafts). Pure (text and mapping in, text out), so it runs the same on Windows and on the Mac where the tests run.
//
// Rules (one pass, no regex; indices are the editor's, with "\r" line breaks; the text is parsed without the strip's
// item limit):
//   - Collapse: each File item of ComposerAttachments.Parse whose path is in the folder (the caller's predicate) becomes
//     the badge of its number. A path already in the mapping (same normalized path, case-insensitive) keeps its number;
//     a new one gets the largest number + 1 (1 for the first). Numbers are given in text order.
//   - Expand: each PastedImage item whose N is in the mapping becomes ComposerImageStore.Mention(path), with a space
//     added before it when the text before is no whitespace (space, tab, CR, LF, VT; none at the start) and, for an
//     unquoted mention, after it when the text after is none (none at the end); an unknown N stays as written.
//   - The mapping lives for one composer session, so an undo that brings a badge back still expands.

using System.Text;

namespace PopGuy.Core.Composer;

public static class ComposerPastedImages
{
    /// <summary>The badge for pasted image <paramref name="number"/>.</summary>
    public static string Badge(int number) => $"[Pasted image #{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";

    /// <summary>
    /// <paramref name="text"/> with every mention of a file in the image folder (<paramref name="isStorePath"/>) replaced
    /// by its badge; new paths are added to <paramref name="mapping"/>.
    /// </summary>
    public static string Collapse(string text, Func<string, bool> isStorePath, IDictionary<int, string> mapping)
    {
        var replacements = new List<(int Start, int Length, string Badge)>();
        foreach (var item in ComposerAttachments.Parse(text, int.MaxValue))
        {
            if (item.Kind != ComposerAttachmentKind.File || item.Path is not { } path || !isStorePath(path)) continue;
            replacements.Add((item.Start, item.Length, Badge(NumberFor(path, mapping))));
        }
        return Replace(text, replacements);
    }

    /// <summary>
    /// <paramref name="text"/> with every known badge replaced by its full mention, set apart by a space from text right
    /// before or after the badge (a mention counts only after whitespace and an unquoted one runs to the next whitespace).
    /// </summary>
    public static string Expand(string text, IReadOnlyDictionary<int, string> mapping)
    {
        if (mapping.Count == 0) return text;
        var replacements = new List<(int Start, int Length, string Mention)>();
        foreach (var item in ComposerAttachments.Parse(text, int.MaxValue))
        {
            if (item.Kind != ComposerAttachmentKind.PastedImage || !mapping.TryGetValue(item.Number, out var path)) continue;
            var end = item.Start + item.Length;
            var before = item.Start > 0 && !IsWhitespace(text[item.Start - 1]) ? " " : "";
            var mention = ComposerImageStore.Mention(path);
            // A quoted mention ends at its quote, so text right after it is harmless (and the round trip stays exact).
            var after = !mention.StartsWith("@\"", StringComparison.Ordinal) && end < text.Length && !IsWhitespace(text[end]) ? " " : "";
            replacements.Add((item.Start, item.Length, before + mention + after));
        }
        return Replace(text, replacements);
    }

    /// <summary>The badge that covers the character at <paramref name="index"/> (start, length, number), or null.</summary>
    public static (int Start, int Length, int Number)? BadgeAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return null;
        // A badge holds no "[" after its first character and no line break: the nearest "[" on the line is the only start.
        var start = index;
        while (start >= 0 && text[start] != '[')
        {
            if (text[start] is '\r' or '\n' or '\v') return null;
            start -= 1;
        }
        if (start < 0 || ComposerAttachments.PastedImageBadge(text, start) is not { } hit || index >= hit.End) return null;
        return (start, hit.End - start, hit.Number);
    }

    /// <summary>The number of <paramref name="path"/> in <paramref name="mapping"/>, added as the next number when new.</summary>
    private static int NumberFor(string path, IDictionary<int, string> mapping)
    {
        var key = PathKey(path);
        var next = 1;
        foreach (var (number, known) in mapping)
        {
            if (string.Equals(PathKey(known), key, StringComparison.OrdinalIgnoreCase)) return number;
            if (number >= next) next = number + 1;
        }
        mapping[next] = path;
        return next;
    }

    /// <summary>Applies non-overlapping replacements given in text order, from the last one back.</summary>
    private static string Replace(string text, List<(int Start, int Length, string Text)> replacements)
    {
        if (replacements.Count == 0) return text;
        var builder = new StringBuilder(text);
        for (var i = replacements.Count - 1; i >= 0; i--)
        {
            var (start, length, replacement) = replacements[i];
            builder.Remove(start, length).Insert(start, replacement);
        }
        return builder.ToString();
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\v';

    private static string PathKey(string path) => ComposerImagePath.NormalizedDrivePath(path) ?? path;
}
