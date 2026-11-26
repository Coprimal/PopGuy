// ComposerAttachmentsTests.swift
// PopGuyTests
//
// Tests for the Terminal Composer's attachment parser (the image strip's source):
//   - @path, @"path" and [Image #N] items, with UTF-16 ranges, in text order
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
        for path in ["/net/host/x.png", "/Network/Servers/h/x.png", "/Users/a/../../net/h/x.png",
                     "//host/x.png", "/private/var/automount/x.png", "/Volumes/NoSuchVolume-PopGuy/x.png", "x.png"] {
            #expect(!ComposerThumbnailLoader.isLocalPath(path), "\(path)")
        }
    }
}
