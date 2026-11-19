using PopGuy.Core.Composer;

namespace PopGuy.Tests.Core;

// The Terminal Composer's Markdown list editing (pure text -> edit), as in
// macos/PopGuyTests/Unit/ListContinuationTests.swift: Enter continues "-", "*" and numbered items and clears an empty
// one, Backspace right after a marker deletes it (top level) or outdents the item (nested), Tab / Shift+Tab indent /
// outdent a list item by two spaces with the caret kept in place. RichEdit's "\r" and "\v" line ends count as lines too.
public sealed class ListContinuationTests
{
    private static string? Apply(ListEdit? edit, string text) =>
        edit is { } e ? text[..e.Start] + e.Replacement + text[(e.Start + e.Length)..] : null;

    private static string? AfterReturn(string text, int? caret = null) =>
        Apply(ListContinuation.ReturnEdit(text, caret ?? text.Length), text);

    private static string? AfterBackspace(string text, int? caret = null) =>
        Apply(ListContinuation.BackspaceEdit(text, caret ?? text.Length), text);

    private static string? AfterIndent(string text, int? caret = null) =>
        Apply(ListContinuation.IndentEdit(text, caret ?? text.Length), text);

    private static string? AfterOutdent(string text, int? caret = null) =>
        Apply(ListContinuation.OutdentEdit(text, caret ?? text.Length), text);

    private static int? CaretAfter(ListEdit? edit) => edit is { } e ? e.Start + e.Replacement.Length : null;

    [Fact]
    public void ReturnContinuesBulletsAndNumbersWithTheSameIndentation()
    {
        Assert.Equal("- one\n- ", AfterReturn("- one"));
        Assert.Equal("intro\r* one\n* ", AfterReturn("intro\r* one"));
        Assert.Equal("1. one\n2. ", AfterReturn("1. one"));
        Assert.Equal("9. nine\n10. ", AfterReturn("9. nine"));
        Assert.Equal("  - nested 😀\n  - ", AfterReturn("  - nested 😀"));
        // The caret mid-item splits it, the rest moving to the new item.
        Assert.Equal("- one\n- two\rnext", AfterReturn("- onetwo\rnext", 5));
    }

    [Fact]
    public void ReturnOnAnEmptyItemClearsItAndOffAListIsPlain()
    {
        Assert.Equal("- one\r", AfterReturn("- one\r- "));
        Assert.Equal("- one\v", AfterReturn("- one\v  2.  "));
        Assert.Null(AfterReturn("plain"));
        Assert.Null(AfterReturn("-not a marker"));
        Assert.Null(AfterReturn("- one\r"));
        Assert.Null(AfterReturn("1.5 kg"));
        // The caret before the marker's end: a plain line break.
        Assert.Null(AfterReturn("- one", 1));
    }

    [Fact]
    public void BackspaceRightAfterATopLevelMarkerDeletesTheMarker()
    {
        Assert.Equal("- one\r", AfterBackspace("- one\r- "));
        Assert.Equal("", AfterBackspace("12. "));
        Assert.Equal("item", AfterBackspace("* item", 2));
        Assert.Null(AfterBackspace("- item"));
        Assert.Null(AfterBackspace("- ", 1));
        Assert.Null(AfterBackspace("plain "));
    }

    [Fact]
    public void BackspaceRightAfterANestedMarkerOutdentsTheItem()
    {
        Assert.Equal("- one\r- ", AfterBackspace("- one\r  - "));
        Assert.Equal("  12. ", AfterBackspace("    12. "));
        Assert.Equal("* x", AfterBackspace("\t* x", 3));
        Assert.Equal("- ", AfterBackspace(" - "));
    }

    [Fact]
    public void TabIndentsAListItemByTwoSpacesWhereverTheCaretIs()
    {
        Assert.Equal("- one\r  - ", AfterIndent("- one\r- "));
        Assert.Equal("- one\v  - two", AfterIndent("- one\v- two", 8));
        Assert.Equal("    1. x\rnext", AfterIndent("  1. x\rnext", 0));
        Assert.Null(AfterIndent("plain"));
        Assert.Null(AfterIndent("- one\r"));
    }

    [Fact]
    public void ShiftTabOutdentsAListItemAndATopLevelItemStays()
    {
        Assert.Equal("- one\r- two", AfterOutdent("- one\r  - two"));
        Assert.Equal("   * x", AfterOutdent("     * x", 1));
        Assert.Equal("\t- x", AfterOutdent("\t\t- x"));
        Assert.Null(AfterOutdent("- top"));
        Assert.Null(AfterOutdent("  plain"));
    }

    [Fact]
    public void IndentAndOutdentKeepTheCaretOnTheSameCharacter()
    {
        Assert.Equal(5, CaretAfter(ListContinuation.IndentEdit("- two", 3)));
        Assert.Equal(3, CaretAfter(ListContinuation.OutdentEdit("  - two", 5)));
        // The caret inside the removed indentation lands at the line start.
        Assert.Equal(0, CaretAfter(ListContinuation.OutdentEdit("    - x", 1)));
    }
}
