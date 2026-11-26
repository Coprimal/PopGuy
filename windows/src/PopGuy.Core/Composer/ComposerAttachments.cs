// Finds the attachments in the Terminal Composer's plain text, for the image strip: image mentions written by
// ComposerImageStore.Mention, and the "[Image #N]" placeholders a CLI (Claude Code) puts in its prompt. Pure
// (text -> items). Same rules as macos/PopGuy/Modules/TerminalComposer/ComposerAttachments.swift.
//
// Rules (one pass over the UTF-16 code units, no regex; the text uses RichEdit's "\r" line breaks):
//   - A mention starts with "@" at the start of the text or right after whitespace (space, tab, LF, CR, VT).
//   - "@path" ends at the first whitespace; "@\"path\"" ends at the next quote on the same line (no escaping). An
//     unquoted mention, or a closed quoted one, is skipped whole even when it does not qualify; an unclosed quote (line
//     or text ends first) is no mention and the scan goes on at the next code unit.
//   - A mention qualifies when its path is an absolute local drive path ("X:\" or "X:/", ASCII drive letter; never
//     UNC or device paths, whose lookup would reach the network) and its extension (after the last "." of the last
//     "\" or "/" component, case-insensitive) is an image extension. No "~", no relative path, no punctuation trimmed.
//   - "[Image #<ASCII digits>]" is a CLI image placeholder.
//   - One item per occurrence, in text order, at most MaxItems.
// Ranges are (Start, Length) in UTF-16 code units, the C# string index; every delimiter is ASCII, so no surrogate pair
// is ever split.

using System.Globalization;

namespace PopGuy.Core.Composer;

public enum ComposerAttachmentKind
{
    File,
    CliImage,
}

/// <summary>
/// One attachment in the text. <c>Path</c> is the file path (without "@" or quotes) for a File, null for a CliImage;
/// <c>Number</c> is the placeholder's number for a CliImage, 0 for a File.
/// </summary>
public sealed record ComposerAttachment(ComposerAttachmentKind Kind, int Start, int Length, string Token, string? Path, int Number);

public static class ComposerAttachments
{
    public const int MaxItems = 50;

    private const string ImagePrefix = "[Image #";

    /// <summary>The image files Claude Code attaches from an @-mention (same set as ComposerImageStore).</summary>
    private static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "gif", "webp"];

    public static IReadOnlyList<ComposerAttachment> Parse(string text)
    {
        var items = new List<ComposerAttachment>();
        var index = 0;
        while (index < text.Length && items.Count < MaxItems)
        {
            (int End, ComposerAttachment? Item)? found;
            if (text[index] == '@' && (index == 0 || IsWhitespace(text[index - 1])))
                found = Mention(text, index);
            else if (text[index] == '[')
                found = CliImage(text, index);
            else
                found = null;

            if (found is not { } hit)
            {
                index += 1;
                continue;
            }
            if (hit.Item is { } item) items.Add(item);
            index = hit.End;
        }
        return items;
    }

    /// <summary>
    /// The range to delete for an item: its token plus one adjacent space (the one after it, else the one before it),
    /// or the token alone. A range outside the text is returned unchanged.
    /// </summary>
    public static (int Start, int Length) RemovalRange(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start > text.Length || length > text.Length - start) return (start, length);
        var end = start + length;
        if (end < text.Length && text[end] == ' ') return (start, length + 1);
        if (start > 0 && text[start - 1] == ' ') return (start - 1, length + 1);
        return (start, length);
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\r';

    private static bool IsLineBreak(char c) => c is '\n' or '\r';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>
    /// The end of the mention starting with "@" at <paramref name="start"/> and its item (null when it does not
    /// qualify), or null when it is no mention (an unclosed quote).
    /// </summary>
    private static (int End, ComposerAttachment? Item)? Mention(string text, int start)
    {
        var pathStart = start + 1;
        if (pathStart < text.Length && text[pathStart] == '"')
        {
            var close = pathStart + 1;
            while (close < text.Length && text[close] != '"')
            {
                if (IsLineBreak(text[close])) return null;
                close += 1;
            }
            if (close >= text.Length) return null;
            return (close + 1, FileItem(text, start, close + 1, pathStart + 1, close));
        }
        var end = pathStart;
        while (end < text.Length && !IsWhitespace(text[end])) end += 1;
        return (end, FileItem(text, start, end, pathStart, end));
    }

    /// <summary>The end of a "[Image #&lt;digits&gt;]" placeholder at <paramref name="start"/> and its item, or null.</summary>
    private static (int End, ComposerAttachment? Item)? CliImage(string text, int start)
    {
        if (text.Length - start < ImagePrefix.Length
            || string.CompareOrdinal(text, start, ImagePrefix, 0, ImagePrefix.Length) != 0) return null;
        var digitsStart = start + ImagePrefix.Length;
        var digitsEnd = digitsStart;
        while (digitsEnd < text.Length && IsDigit(text[digitsEnd])) digitsEnd += 1;
        if (digitsEnd == digitsStart || digitsEnd >= text.Length || text[digitsEnd] != ']'
            || !int.TryParse(text.AsSpan(digitsStart, digitsEnd - digitsStart), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number)) return null;
        var end = digitsEnd + 1;
        return (end, new ComposerAttachment(ComposerAttachmentKind.CliImage, start, end - start,
            text[start..end], null, number));
    }

    /// <summary>A File item for the token [tokenStart, tokenEnd) when its path [pathStart, pathEnd) qualifies.</summary>
    private static ComposerAttachment? FileItem(string text, int tokenStart, int tokenEnd, int pathStart, int pathEnd)
    {
        var path = text[pathStart..pathEnd];
        if (path.Length < 3 || !IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return null;
        var nameStart = path.LastIndexOfAny(['\\', '/']) + 1;
        var dot = path.LastIndexOf('.');
        if (dot < nameStart) return null;
        var extension = path[(dot + 1)..];
        if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return null;
        return new ComposerAttachment(ComposerAttachmentKind.File, tokenStart, tokenEnd - tokenStart,
            text[tokenStart..tokenEnd], path, 0);
    }
}
