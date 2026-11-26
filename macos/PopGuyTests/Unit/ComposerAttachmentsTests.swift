// ComposerAttachmentsTests.swift
// PopGuyTests
//
// Tests for the Terminal Composer's attachment parser (the image strip's source):
//   - @path, @"path", [Image #N] and [Pasted image #N] items, with UTF-16 ranges, in text order
//   - only absolute local image paths qualify as file mentions
//   - removing an item takes the token plus exactly one adjacent space

import Foundation
import Testing
@testable import PopGuy

@Suite("Terminal Composer attachments")
struct ComposerAttachmentsTests {

    @Test("finds @path, @\"path\" and [Image #N] items with UTF-16 ranges")
    func findsItems() {
        // The emoji is 2 UTF-16 units, so every range after it is shifted by 2.
        let text = "😀 @/tmp/a.png @\"/tmp/my shots/b.JPG\" [Image #12] x@/c.png @/d.txt @rel/e.png"
        let items = ComposerAttachments.parse(text)

        #expect(items == [
            .init(kind: .file(path: "/tmp/a.png"), range: NSRange(location: 3, length: 11), token: "@/tmp/a.png"),
            .init(kind: .file(path: "/tmp/my shots/b.JPG"), range: NSRange(location: 15, length: 22),
                  token: "@\"/tmp/my shots/b.JPG\""),
            .init(kind: .cliImage(number: 12), range: NSRange(location: 38, length: 11), token: "[Image #12]"),
        ])
        for item in items {
            #expect((text as NSString).substring(with: item.range) == item.token)
        }
    }

    @Test("finds [Pasted image #N] badges with UTF-16 ranges")
    func findsPastedImageBadges() {
        let text = "😀 [Pasted image #3] [Pasted image #] [Pasted image #99999999999999999999]"
        #expect(ComposerAttachments.parse(text) == [
            .init(kind: .pastedImage(number: 3), range: NSRange(location: 3, length: 17), token: "[Pasted image #3]"),
        ])
    }

    @Test("only absolute local image paths qualify")
    func onlyAbsolutePaths() {
        #expect(ComposerAttachments.parse("@/a.png").map(\.kind) == [.file(path: "/a.png")])
        for text in ["@a.png", "@./a.png", "@~/a.png", "@/a.png,", "@\"/a b.png\nx\""] {
            #expect(ComposerAttachments.parse(text).isEmpty, "\(text)")
        }
    }

    @Test("removal takes the token and exactly one adjacent space")
    func removalRange() {
        // A space after wins.
        #expect(ComposerAttachments.removalRange(for: NSRange(location: 2, length: 7), in: "a @/x.png b")
            == NSRange(location: 2, length: 8))
        // At the end of the line or the text: the space before.
        #expect(ComposerAttachments.removalRange(for: NSRange(location: 2, length: 7), in: "a @/x.png\nb")
            == NSRange(location: 1, length: 8))
        #expect(ComposerAttachments.removalRange(for: NSRange(location: 2, length: 7), in: "a @/x.png")
            == NSRange(location: 1, length: 8))
        // No adjacent space: the token alone.
        #expect(ComposerAttachments.removalRange(for: NSRange(location: 2, length: 7), in: "a\n@/x.png\nb")
            == NSRange(location: 2, length: 7))
    }

    @Test("previews never touch paths that can reach the network")
    func localPathsOnly() {
        #expect(ComposerThumbnailLoader.isLocalPath("/tmp/x.png"))
        for path in ["/net/host/x.png", "/NET/host/x.png", "/Network/Servers/h/x.png", "/VOLUMES/NoSuchVolume-PopGuy/x.png", "/Users/a/../../net/h/x.png",
                     "//host/x.png", "/private/var/automount/x.png", "/Volumes/NoSuchVolume-PopGuy/x.png", "/home/u/x.png", "x.png",
                     "/home", "/System/Volumes",
                     // APFS folds U+017F to "s": these name /System/Volumes/Data and /Volumes.
                     "/\u{17F}ystem/Volumes/Data/x.png", "/Volume\u{17F}/NoSuchVolume-PopGuy/x.png"] {
            #expect(!ComposerThumbnailLoader.isLocalPath(path), "\(path)")
        }
    }

    @Test("symlinks are followed one component at a time and never into a network tree")
    func resolvedLocalPathFollowsLinksSafely() throws {
        let fileManager = FileManager.default
        let dir = fileManager.temporaryDirectory.appendingPathComponent("PopGuyTests-\(UUID().uuidString)").path
        try fileManager.createDirectory(atPath: dir, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(atPath: dir) }
        // The temporary directory sits under /var, itself a symlink: compare physical paths.
        let physicalDir = try #require(realpath(dir, nil).map { pointer in
            defer { free(pointer) }
            return String(cString: pointer)
        })
        let image = "\(dir)/img.png"
        #expect(fileManager.createFile(atPath: image, contents: Data([0])))
        try fileManager.createSymbolicLink(atPath: "\(dir)/alias.png", withDestinationPath: image)
        try fileManager.createSymbolicLink(atPath: "\(dir)/x", withDestinationPath: "/Network/Servers/h")
        try fileManager.createSymbolicLink(atPath: "\(dir)/link", withDestinationPath: "/Network/Servers/foo")
        // Refused trees that exist and lead back to the image through the Data firmlink:
        // only the guard (not a missing path) can make these nil.
        try fileManager.createSymbolicLink(atPath: "\(dir)/sys", withDestinationPath: "/System/Volumes/Data")
        try fileManager.createSymbolicLink(atPath: "\(dir)/fold", withDestinationPath: "/\u{17F}ystem/Volumes/Data")
        #expect(fileManager.fileExists(atPath: "/System/Volumes/Data\(physicalDir)/img.png"))

        #expect(ComposerThumbnailLoader.resolvedLocalPath(image) == "\(physicalDir)/img.png")
        #expect(ComposerThumbnailLoader.resolvedLocalPath("\(dir)/alias.png") == "\(physicalDir)/img.png")
        // A link used as a directory, and a link followed by "..": both lead into /Network.
        #expect(ComposerThumbnailLoader.resolvedLocalPath("\(dir)/x/img.png") == nil)
        #expect(ComposerThumbnailLoader.resolvedLocalPath("\(dir)/link/../img.png") == nil)
        #expect(ComposerThumbnailLoader.resolvedLocalPath("\(dir)/sys\(physicalDir)/img.png") == nil)
        #expect(ComposerThumbnailLoader.resolvedLocalPath("\(dir)/fold\(physicalDir)/img.png") == nil)
    }
}
