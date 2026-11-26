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

    /// True for an absolute path that cannot reach the network, judged on the text alone
    /// (no file system call besides the local-volume check): a mention must not trigger an
    /// automount or a server lookup. The automount trees (and their roots) are refused,
    /// and a /Volumes path only passes on a local volume. ".." is resolved lexically, so a
    /// symlink can still lead elsewhere: resolvedLocalPath checks every step of the way.
    nonisolated static func isLocalPath(_ path: String) -> Bool {
        guard path.hasPrefix("/"), !path.hasPrefix("//") else { return false }
        // isDirectory given: without it, Foundation checks the file system to decide.
        let standardized = URL(fileURLWithPath: path, isDirectory: false).standardized.path
        // Folded like the default file system matches names ("/NET" is "/net", and U+017F
        // is "s"): Foundation's case folding, not lowercasing, which keeps U+017F.
        let folded = standardized.folding(options: [.caseInsensitive, .widthInsensitive], locale: nil)
        let refused = ["/net/", "/network/", "/home/", "/private/var/automount/", "/var/automount/", "/system/volumes/"]
        // The trailing "/" also refuses a tree's root itself.
        if refused.contains(where: { (folded + "/").hasPrefix($0) }) { return false }
        guard folded.hasPrefix("/volumes/") else { return true }
        // "/Volumes/<name>" itself passes too: resolvedLocalPath checks every prefix.
        let parts = standardized.split(separator: "/", maxSplits: 2)
        guard parts.count >= 2 else { return false }
        let volume = URL(fileURLWithPath: "/Volumes/\(parts[1])", isDirectory: true)
        return (try? volume.resourceValues(forKeys: [.volumeIsLocalKey]))?.volumeIsLocal == true
    }

    /// Max symlinks followed while resolving one path.
    private static let maxLinkHops = 32

    /// The physical path `path` leads to (every symlink followed, "." and ".." applied to
    /// the resolved prefix), or nil when the path or any prefix on the way, a link target
    /// included, fails isLocalPath, when a component's directory is not on a local file
    /// system (an automount point or a network share), when links nest too deep or a
    /// component can't be read. Walks one component at a time with Darwin's lstat and
    /// Foundation's link reading, which never touch a link's target, and only on prefixes
    /// that passed isLocalPath and whose directory passed isLocalDirectory.
    nonisolated static func resolvedLocalPath(_ path: String) -> String? {
        guard isLocalPath(path) else { return nil }
        var pending = path.split(separator: "/").map(String.init)
        var resolved: [String] = []
        var hops = 0
        while !pending.isEmpty {
            let part = pending.removeFirst()
            if part == "." { continue }
            if part == ".." {
                // The resolved prefix holds no links, so this step is physical.
                _ = resolved.popLast()
                continue
            }
            let candidate = "/" + (resolved + [part]).joined(separator: "/")
            guard isLocalPath(candidate), isLocalDirectory("/" + resolved.joined(separator: "/")) else { return nil }
            var info = stat()
            guard lstat(candidate, &info) == 0 else { return nil }
            guard info.st_mode & S_IFMT == S_IFLNK else {
                resolved.append(part)
                continue
            }
            hops += 1
            guard hops <= maxLinkHops,
                  let target = try? FileManager.default.destinationOfSymbolicLink(atPath: candidate),
                  !target.isEmpty
            else { return nil }
            // A relative target resolves against the link's directory.
            let absoluteTarget = target.hasPrefix("/") ? target : "/" + (resolved + [target]).joined(separator: "/")
            guard isLocalPath(absoluteTarget) else { return nil }
            if target.hasPrefix("/") { resolved.removeAll() }
            pending.insert(contentsOf: target.split(separator: "/").map(String.init), at: 0)
        }
        return "/" + resolved.joined(separator: "/")
    }

    /// True when the resolved directory `path` sits on a local file system that is not an
    /// automount point (Darwin's statfs, which does not trigger an automount; a network share mounted outside /Volumes still costs one server round trip before it is refused).
    /// Checked before anything inside it is looked up, whatever the path's text says.
    private nonisolated static func isLocalDirectory(_ path: String) -> Bool {
        var info = statfs()
        guard statfs(path, &info) == 0, info.f_flags & UInt32(MNT_LOCAL) != 0 else { return false }
        let type = withUnsafeBytes(of: info.f_fstypename) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
        return type != "autofs"
    }

    /// The resolved URL and modification date of `path` when it leads, without reaching
    /// the network, to a regular file within the size limit (Foundation resource values).
    private nonisolated static func readableFile(_ path: String) -> (url: URL, modified: Date)? {
        guard let resolved = resolvedLocalPath(path) else { return nil }
        let url = URL(fileURLWithPath: resolved, isDirectory: false)
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
