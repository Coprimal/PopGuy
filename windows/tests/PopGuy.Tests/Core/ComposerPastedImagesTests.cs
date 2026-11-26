using PopGuy.Core.Composer;

namespace PopGuy.Tests.Core;

// Contract (macOS ComposerPastedImagesTests.swift): PopGuy's own pasted-image mentions (files directly in the composer's
// image folder) show in the editor as "[Pasted image #N]" badges, numbered per composer session, and expand back to the
// full mention wherever the text leaves the composer.
public sealed class ComposerPastedImagesTests
{
    private const string Store = @"C:\Users\A B\AppData\Local\PopGuyData\Composer\images";

    private static bool IsStorePath(string path) =>
        path.StartsWith(Store + @"\", StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\', Store.Length + 1) < 0;

    [Fact]
    public void CollapsesStoreMentionsAndExpandsThemBackUnchanged()
    {
        var first = Store + @"\1.png";
        var second = Store + @"\2.png";
        // A comma right after a quoted mention stays glued on the way back: the round trip is exact.
        var text = $"@\"{first}\" fix\r@D:\\shots\\x.png then @\"{second}\", and @\"{first}\"";
        var mapping = new Dictionary<int, string>();

        var collapsed = ComposerPastedImages.Collapse(text, IsStorePath, mapping);

        Assert.Equal(@"[Pasted image #1] fix" + "\r" + @"@D:\shots\x.png then [Pasted image #2], and [Pasted image #1]", collapsed);
        Assert.Equal(new Dictionary<int, string> { [1] = first, [2] = second }, mapping);
        Assert.Equal(text, ComposerPastedImages.Expand(collapsed, mapping));
    }

    [Fact]
    public void CollapseAndExpandParsePastTheStripsItemLimit()
    {
        // More mentions than the strip shows: a badge past the limit must still expand, or it leaves the composer literal.
        var count = ComposerAttachments.MaxItems + 5;
        var text = string.Join(" ", Enumerable.Range(1, count).Select(i => $"@\"{Store}\\{i}.png\""));
        var mapping = new Dictionary<int, string>();

        var collapsed = ComposerPastedImages.Collapse(text, IsStorePath, mapping);

        Assert.Equal(string.Join(" ", Enumerable.Range(1, count).Select(ComposerPastedImages.Badge)), collapsed);
        Assert.Equal(count, mapping.Count);
        Assert.Equal(text, ComposerPastedImages.Expand(collapsed, mapping));
    }

    [Fact]
    public void ExpandSeparatesAMentionFromTextAroundItsBadge()
    {
        // A mention counts only after whitespace, and an unquoted one runs to the next whitespace.
        var path = @"C:\shots\x.png";
        var mapping = new Dictionary<int, string> { [1] = path };

        Assert.Equal($"Look: @{path} ,", ComposerPastedImages.Expand("Look:[Pasted image #1],", mapping));
    }

    [Fact]
    public void NumberingContinuesFromTheSessionMappingAndReusesKnownPaths()
    {
        var known = Store + @"\known.png";
        var mapping = new Dictionary<int, string> { [4] = known };

        var collapsed = ComposerPastedImages.Collapse($"@\"{Store}\\new.png\" @\"{known}\"", IsStorePath, mapping);

        Assert.Equal("[Pasted image #5] [Pasted image #4]", collapsed);
        Assert.Equal(Store + @"\new.png", mapping[5]);
    }

    [Fact]
    public void ExpandLeavesUnknownBadgesLiteral()
    {
        var mapping = new Dictionary<int, string> { [1] = Store + @"\1.png" };

        Assert.Equal($"@\"{Store}\\1.png\" [Pasted image #9] [Pasted image #]",
            ComposerPastedImages.Expand("[Pasted image #1] [Pasted image #9] [Pasted image #]", mapping));
    }

    [Fact]
    public void BadgeAtFindsTheBadgeAroundAnIndex()
    {
        const string text = "ab [Pasted image #12] c";

        Assert.Equal((3, 18, 12), ComposerPastedImages.BadgeAt(text, 3));
        Assert.Equal((3, 18, 12), ComposerPastedImages.BadgeAt(text, 20));
        Assert.Null(ComposerPastedImages.BadgeAt(text, 2));
        Assert.Null(ComposerPastedImages.BadgeAt(text, 21));
    }
}
