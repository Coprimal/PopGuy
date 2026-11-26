// The lexical half of the Terminal Composer's image-path rule: an image mention's path as a normalized local drive path,
// before any file system call. The App (ComposerWindow.Images.cs) runs it first, then the framework's own path
// normalization (which must agree) and the drive-type and file-attribute checks. Pure and done by hand, without the
// framework's path functions, so the result is the same on Windows and on the Mac where the tests run.
//
// Rules:
//   - The path starts "X:\" or "X:/" (ASCII drive letter): never UNC, a device path or a drive-relative path.
//   - "/" becomes "\"; repeated separators and "." segments go; ".." removes the segment before it, and a ".." above
//     the drive root rejects the path. A trailing separator, or no file name at all, rejects it.
//   - No ":" after the drive (alternate data streams) and no NUL.
//   - The file name is no reserved DOS device name with any extension ("nul.png", "COM1.tar.png", "nul .png").

namespace PopGuy.Core.Composer;

public static class ComposerImagePath
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary><paramref name="path"/> as a normalized "X:\…" path by the rules above, or null.</summary>
    public static string? NormalizedDrivePath(string path)
    {
        if (!IsDrivePath(path) || path.IndexOf(':', 2) >= 0 || path.IndexOf('\0') >= 0) return null;
        if (path[^1] is '\\' or '/') return null;
        var segments = new List<string>();
        foreach (var segment in path[3..].Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        if (segments.Count == 0 || IsReservedDeviceName(segments[^1])) return null;
        return path[..2] + "\\" + string.Join('\\', segments);
    }

    private static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>A DOS device name with any extension: the stem before the first dot, trailing spaces trimmed.</summary>
    private static bool IsReservedDeviceName(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        return ReservedDeviceNames.Contains(stem);
    }
}
