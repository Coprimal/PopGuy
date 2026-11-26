// ComposerThumbnailLoader.swift
// PopGuy
//
// Decodes the images named by "@/abs/path.png" mentions in the Terminal Composer for
// the preview strip (small, cached thumbnails) and the enlarged preview (a bounded,
// uncached decode). Downsampling goes through ImageIO, so a large photo is never
// decoded at full size. Thumbnails decode on this actor; a preview decodes in its own
// detached task, so it never waits behind queued thumbnails. Nothing runs on the main actor.
//
// Safety: only absolute paths to regular files (a symlink counts when its target is
// one) of at most 50 MB are read, read-only. Any failure gives nil.

import Foundation
import ImageIO

actor ComposerThumbnailLoader {
    static let shared = ComposerThumbnailLoader()

    /// Largest file the loader reads.
    static let maxFileBytes = 50 * 1024 * 1024
    /// Thumbnail edge in pixels: a 48 pt tile at 2x, with headroom.
    static let thumbnailMaxPixel = 128
    /// Cached thumbnails before the cache is emptied.
    static let maxCacheEntries = 200

    private struct Entry {
        let modified: Date
        let image: CGImage
    }

    private var cache: [String: Entry] = [:]

    /// A small thumbnail, cached by path and file modification date (a changed file
    /// is decoded again).
    func thumbnail(path: String) async -> CGImage? {
        guard let file = Self.readableFile(path) else { return nil }
        if let entry = cache[path], entry.modified == file.modified {
            return entry.image
        }
        guard let image = Self.decode(file.url, maxPixel: Self.thumbnailMaxPixel) else {
            cache[path] = nil
            return nil
        }
        if cache.count >= Self.maxCacheEntries { cache.removeAll() }
        cache[path] = Entry(modified: file.modified, image: image)
        return image
    }

    /// A larger decode for the enlarged preview, not cached. It runs in a detached task
    /// (not on this actor, not on the caller's actor), so it starts at once.
    nonisolated func preview(path: String, maxPixel: Int) async -> CGImage? {
        await Task.detached(priority: .userInitiated) {
            guard maxPixel > 0, let file = Self.readableFile(path) else { return nil }
            return Self.decode(file.url, maxPixel: maxPixel)
        }.value
    }

    // MARK: - Helpers

    /// True for an absolute path that cannot reach the network, checked before any file
    /// system call: a mention must not trigger an automount or a server lookup. The
    /// automount trees are refused, and a /Volumes path only passes on a local volume.
    nonisolated static func isLocalPath(_ path: String) -> Bool {
        guard path.hasPrefix("/"), !path.hasPrefix("//") else { return false }
        // Lexical only: ".." cannot step into a refused tree.
        // isDirectory given: without it, Foundation checks the file system to decide.
        let standardized = URL(fileURLWithPath: path, isDirectory: false).standardized.path
        // Case-insensitive: the default file system matches "/NET" to "/net".
        let lower = standardized.lowercased()
        let refused = ["/net/", "/network/", "/private/var/automount/", "/var/automount/", "/system/volumes/"]
        if refused.contains(where: { lower.hasPrefix($0) }) { return false }
        guard lower.hasPrefix("/volumes/") else { return true }
        let parts = standardized.split(separator: "/", maxSplits: 2)
        guard parts.count == 3 else { return false }
        let volume = URL(fileURLWithPath: "/Volumes/\(parts[1])", isDirectory: true)
        return (try? volume.resourceValues(forKeys: [.volumeIsLocalKey]))?.volumeIsLocal == true
    }

    /// The resolved URL and modification date of `path` when it is an absolute path to
    /// a regular file within the size limit (Foundation resource values).
    private nonisolated static func readableFile(_ path: String) -> (url: URL, modified: Date)? {
        guard isLocalPath(path) else { return nil }
        let url = URL(fileURLWithPath: path, isDirectory: false).resolvingSymlinksInPath()
        // A link may point into a refused tree.
        guard isLocalPath(url.path) else { return nil }
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        guard let values = try? url.resourceValues(forKeys: keys),
              values.isRegularFile == true,
              let size = values.fileSize, size <= maxFileBytes,
              let modified = values.contentModificationDate
        else { return nil }
        return (url, modified)
    }

    /// An ImageIO thumbnail of the first image, always made from the full image (not
    /// an embedded thumbnail), with the orientation applied.
    private nonisolated static func decode(_ url: URL, maxPixel: Int) -> CGImage? {
        let sourceOptions = [kCGImageSourceShouldCache: false] as CFDictionary
        guard let source = CGImageSourceCreateWithURL(url as CFURL, sourceOptions),
              CGImageSourceGetCount(source) > 0
        else { return nil }
        let options = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
        ] as CFDictionary
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options)
    }
}
