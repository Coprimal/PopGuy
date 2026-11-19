using PopGuy.Core.Composer;

namespace PopGuy.Tests.Core;

// Contract (macOS ComposerImageStoreTests.swift): a pasted image is saved as a PNG file and pasted as an "@path" mention
// (quoted when the path has whitespace), copied image files are mentioned in place, text wins over an image except a
// lone http(s) URL, and images older than 7 days are pruned.
public sealed class ComposerImageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PopGuyTests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    private ComposerImageStore NewStore() => new(_directory, _log.Add);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void MentionsAreAtPathQuotedWhenThePathHasWhitespace()
    {
        Assert.Equal(@"@C:\tmp\a.png", ComposerImageStore.Mention(@"C:\tmp\a.png"));
        Assert.Equal("@\"C:\\Users\\Jane Doe\\a.png\"", ComposerImageStore.Mention(@"C:\Users\Jane Doe\a.png"));
    }

    [Fact]
    public void TheInsertionIsSpacedFromTheTextBeforeItAndEndsWithASpace()
    {
        Assert.Equal("@/a.png ", ComposerImageStore.Insertion(["/a.png"], previous: null));
        Assert.Equal("@/a.png ", ComposerImageStore.Insertion(["/a.png"], previous: '\r'));
        Assert.Equal(" @/a.png @/b.png ", ComposerImageStore.Insertion(["/a.png", "/b.png"], previous: 'x'));
    }

    [Fact]
    public void ImageFilesAndLoneUrlsAreRecognized()
    {
        Assert.True(ComposerImageStore.IsImageFile(@"C:\shots\a.PNG"));
        Assert.True(ComposerImageStore.IsImageFile("b.jpeg"));
        Assert.False(ComposerImageStore.IsImageFile("notes.txt"));
        Assert.False(ComposerImageStore.IsImageFile("photo.heic"));
        Assert.True(ComposerImageStore.IsLoneUrl(" https://example.com/cat.png\n"));
        Assert.False(ComposerImageStore.IsLoneUrl("see https://example.com"));
        Assert.False(ComposerImageStore.IsLoneUrl("a table cell"));
    }

    [Fact]
    public void SaveWritesTheBytesToANewPngFile()
    {
        var store = NewStore();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        var path = store.Save(png);

        Assert.True(path is not null, string.Join("; ", _log));
        Assert.Equal(_directory, Path.GetDirectoryName(path));
        Assert.EndsWith(".png", path);
        Assert.Equal(png, File.ReadAllBytes(path));
    }

    [Fact]
    public void PruneDeletesImagesOlderThanSevenDays()
    {
        var store = NewStore();
        var old = store.Save([1]);
        var fresh = store.Save([2]);
        Assert.True(old is not null && fresh is not null, string.Join("; ", _log));
        File.SetLastWriteTimeUtc(old!, DateTime.UtcNow - TimeSpan.FromDays(8));

        store.Prune();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }
}
