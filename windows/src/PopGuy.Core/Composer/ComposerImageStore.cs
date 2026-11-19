// Images pasted into the Terminal Composer (macOS ComposerImageStore.swift). The composer is plain text, so a pasted
// image is saved as a PNG file and pasted as an "@path" mention: Claude Code (and Gemini CLI) attach an @-mentioned image
// file when the prompt is submitted, so the image survives the trip back from the external editor. Copied image files
// (Explorer) are mentioned where they are, without a copy.
//
// Files: "<guid>.png" in <see cref="DefaultDirectory"/> (%LOCALAPPDATA%\PopGuyData\Composer\images), each with a DACL
// that grants only the current user. They must outlive the composer (the prompt is submitted later), so they are pruned
// by age (7 days) on each save, never on Done or Cancel.

namespace PopGuy.Core.Composer;

public sealed class ComposerImageStore(string directory, Action<string>? log = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The image files Claude Code attaches from an @-mention.</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary><c>&lt;cache directory&gt;\Composer\images</c> (production: %LOCALAPPDATA%\PopGuyData\Composer\images).</summary>
    public static string DefaultDirectory(AppPaths paths) => Path.Combine(paths.CacheDirectory, "Composer", "images");

    /// <summary><c>@path</c>, or <c>@"path"</c> when the path has whitespace.</summary>
    public static string Mention(string path) => path.Any(char.IsWhiteSpace) ? $"@\"{path}\"" : "@" + path;

    /// <summary>
    /// The mentions to insert after the character <paramref name="previous"/> (null: at the start): set apart from a word
    /// before them, with a space after so typing goes on.
    /// </summary>
    public static string Insertion(IEnumerable<string> paths, char? previous)
    {
        var lead = previous is { } c && !char.IsWhiteSpace(c) ? " " : "";
        return lead + string.Join(' ', paths.Select(Mention)) + " ";
    }

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>A single http(s) URL (browsers put one next to a copied image): the image wins over it.</summary>
    public static bool IsLoneUrl(string text)
    {
        var trimmed = text.Trim();
        return !trimmed.Any(char.IsWhiteSpace)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>Writes <paramref name="png"/> to a new private file and prunes old ones; the file's path, or null on failure.</summary>
    public string? Save(byte[] png)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Prune();
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
            if (OperatingSystem.IsWindows())
                ComposerDraftStore.WritePrivate(path, png);
            else
                File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"composer images: save failed, {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>Deletes images older than <see cref="MaxAge"/>.</summary>
    public void Prune()
    {
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTime.UtcNow - MaxAge;
        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"composer images: prune failed, {e.GetType().Name}");
            }
        }
    }
}
