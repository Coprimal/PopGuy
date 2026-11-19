// ComposerImageStore.swift
// PopGuy
//
// Images pasted into the Terminal Composer. The composer is plain text, so a pasted
// image is saved as a PNG file and pasted as an "@path" mention: Claude Code (and
// Gemini CLI) attach an @-mentioned image file when the prompt is submitted, so the
// image survives the trip back from the external editor. Copied image files (Finder)
// are mentioned where they are, without a copy.
//
// Files: `<uuid>.png` in ~/Library/Caches/<bundle id>/ComposerImages (directory 0700,
// files 0600). They must outlive the composer (the prompt is submitted later), so
// they are pruned by age (7 days) on each save, never on Done or Cancel.

import AppKit

nonisolated struct ComposerImageStore: Sendable {
    static let maxAge: TimeInterval = 7 * 24 * 60 * 60
    /// The image files Claude Code attaches from an @-mention.
    static let imageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "webp"]

    let directory: URL

    /// ~/Library/Caches/<bundle id>/ComposerImages (no space in the path, so mentions need no quotes).
    static let standard: ComposerImageStore? = {
        guard let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first else { return nil }
        let bundleID = Bundle.main.bundleIdentifier ?? "PopGuy"
        return ComposerImageStore(directory: caches.appendingPathComponent(bundleID, isDirectory: true)
            .appendingPathComponent("ComposerImages", isDirectory: true))
    }()

    /// `@path`, or `@"path"` when the path has whitespace.
    static func mention(_ path: String) -> String {
        path.contains(where: \.isWhitespace) ? "@\"\(path)\"" : "@" + path
    }

    /// The mentions to insert after the character `previous` (nil: at the start): set
    /// apart from a word before them, with a space after so typing goes on.
    static func insertion(for paths: [String], after previous: Character?) -> String {
        let lead = previous.map { $0.isWhitespace ? "" : " " } ?? ""
        return lead + paths.map(mention).joined(separator: " ") + " "
    }

    /// The image files for a paste, or nil when the pasteboard should paste as usual:
    /// copied files that are all images (their own paths), else an image when the
    /// pasteboard has no text (or only a lone URL, as browsers copy an image), saved here.
    func imagePaths(from pasteboard: NSPasteboard) -> [String]? {
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           !urls.isEmpty {
            let allImages = urls.allSatisfy { Self.imageExtensions.contains($0.pathExtension.lowercased()) }
            return allImages ? urls.map(\.path) : nil
        }
        if let string = pasteboard.string(forType: .string), !Self.isLoneURL(string) { return nil }
        let png = pasteboard.data(forType: .png)
            ?? pasteboard.data(forType: .tiff).flatMap { NSBitmapImageRep(data: $0)?.representation(using: .png, properties: [:]) }
        return png.flatMap(save(png:)).map { [$0] }
    }

    /// Writes `png` to a new private file and prunes old ones; the file's path, or nil on failure.
    func save(png: Data) -> String? {
        let fileManager = FileManager.default
        do {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true,
                                            attributes: [.posixPermissions: 0o700])
            prune()
            let path = directory.appendingPathComponent(UUID().uuidString + ".png").path
            guard fileManager.createFile(atPath: path, contents: png, attributes: [.posixPermissions: 0o600]) else { return nil }
            return path
        } catch {
            return nil
        }
    }

    /// Deletes images older than `maxAge`.
    func prune() {
        let fileManager = FileManager.default
        guard let files = try? fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let cutoff = Date().addingTimeInterval(-Self.maxAge)
        for file in files where file.pathExtension == "png" {
            if let modified = try? file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
               modified < cutoff {
                try? fileManager.removeItem(at: file)
            }
        }
    }

    private static func isLoneURL(_ string: String) -> Bool {
        let trimmed = string.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.contains(where: \.isWhitespace), let url = URL(string: trimmed) else { return false }
        return url.scheme == "http" || url.scheme == "https"
    }
}
