using PopGuy.Core.Composer;

namespace PopGuy.Tests.Core;

// Contract (macOS ComposerAttachmentsTests.swift): the Terminal Composer's attachment parser (the image strip's source)
// finds @path, @"path" and [Image #N] items with UTF-16 ranges in text order; only absolute local drive paths to image
// files qualify as file mentions; removing an item takes the token plus exactly one adjacent space.
public sealed class ComposerAttachmentsTests
{
    [Fact]
    public void FindsPathQuotedPathAndCliImageItemsWithUtf16Ranges()
    {
        // The emoji is 2 UTF-16 units, so every range after it is shifted by 2.
        var text = "😀 " + @"@C:\tmp\a.png @""C:\tmp\my shots\b.JPG"" [Image #12] x@C:/c.png @C:\d.txt @rel\e.png";

        var items = ComposerAttachments.Parse(text);

        Assert.Equal(
            [
                new ComposerAttachment(ComposerAttachmentKind.File, 3, 13, @"@C:\tmp\a.png", @"C:\tmp\a.png", 0),
                new ComposerAttachment(ComposerAttachmentKind.File, 17, 24, @"@""C:\tmp\my shots\b.JPG""",
                    @"C:\tmp\my shots\b.JPG", 0),
                new ComposerAttachment(ComposerAttachmentKind.CliImage, 42, 11, "[Image #12]", null, 12),
            ],
            items);
        foreach (var item in items)
            Assert.Equal(item.Token, text.Substring(item.Start, item.Length));
    }

    [Fact]
    public void OnlyAbsoluteLocalDrivePathsQualify()
    {
        Assert.Equal([@"C:\x.png"], ComposerAttachments.Parse(@"@C:\x.png").Select(i => i.Path));
        Assert.Equal(["C:/x.png"], ComposerAttachments.Parse("@C:/x.png").Select(i => i.Path));
        string[] rejected =
        [
            @"@\\server\share\x.png", "@//server/x.png", @"@\\?\C:\x.png", @"@\\.\C:\x.png", "@/a.png",
            "@a.png", @"@.\a.png", "@~/a.png", @"@C:\a.png,", "@\"C:\\a b.png\rx\"",
        ];
        foreach (var text in rejected)
            Assert.True(ComposerAttachments.Parse(text).Count == 0, text);
    }

    [Fact]
    public void RemovalTakesTheTokenAndExactlyOneAdjacentSpace()
    {
        // A space after wins.
        Assert.Equal((2, 10), ComposerAttachments.RemovalRange(@"a @C:\x.png b", 2, 9));
        // At the end of the line or the text: the space before.
        Assert.Equal((1, 10), ComposerAttachments.RemovalRange("a @C:\\x.png\rb", 2, 9));
        Assert.Equal((1, 10), ComposerAttachments.RemovalRange(@"a @C:\x.png", 2, 9));
        // No adjacent space: the token alone.
        Assert.Equal((2, 9), ComposerAttachments.RemovalRange("a\r@C:\\x.png\rb", 2, 9));
    }
}
