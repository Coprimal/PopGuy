// ComposerPastedImagesTests.swift
// PopGuyTests
//
// Tests for the Terminal Composer's short badges for PopGuy's own pasted images:
//   - store-child mentions collapse to "[Pasted image #N]" and expand back
//   - one number per path per session, numbering goes on from the mapping
//   - unknown badges stay literal; hover finds the badge under a UTF-16 offset
//   - every mention and badge is rewritten, past the image strip's item cap
//   - an expanded badge is set apart from glued text, so it parses as a mention
//   - the editor copies badges expanded and collapses pasted store mentions

import AppKit
import Foundation
import Testing
@testable import PopGuy

@Suite("Terminal Composer pasted image badges")
struct ComposerPastedImagesTests {
    private static let store = "/Users/u/Library/Caches/app/ComposerImages"

    @Test("store images collapse to badges and expand back; other mentions stay")
    func roundTrip() {
        let a = "\(Self.store)/A.png"
        let b = "\(Self.store)/my shot.png"
        let text = "Look @\(a) and @\"\(b)\", vs @/tmp/other.png @\(Self.store)/sub/c.png @\(Self.store)/../d.png"
        var mapping: [Int: String] = [:]

        let collapsed = ComposerPastedImages.collapse(text, storeDirectory: Self.store, mapping: &mapping)

        #expect(collapsed == "Look [Pasted image #1] and [Pasted image #2], vs @/tmp/other.png "
            + "@\(Self.store)/sub/c.png @\(Self.store)/../d.png")
        #expect(mapping == [1: a, 2: b])
        #expect(ComposerPastedImages.expand(collapsed, mapping: mapping) == text)
    }

    @Test("a path keeps its number; new paths go on after the highest one")
    func numbering() {
        var mapping = [3: "\(Self.store)/old.png"]
        let text = "@\(Self.store)/new.png @\(Self.store)/old.png x @\(Self.store)/new.png"

        let collapsed = ComposerPastedImages.collapse(text, storeDirectory: Self.store, mapping: &mapping)

        #expect(collapsed == "[Pasted image #4] [Pasted image #3] x [Pasted image #4]")
        #expect(mapping == [3: "\(Self.store)/old.png", 4: "\(Self.store)/new.png"])
    }

    @Test("expand leaves badges without a mapping literal")
    func unknownBadgesStay() {
        let expanded = ComposerPastedImages.expand("[Pasted image #99] [Pasted image #1]",
                                                   mapping: [1: "\(Self.store)/a.png"])
        #expect(expanded == "[Pasted image #99] @\(Self.store)/a.png")
    }

    @Test("collapse and expand rewrite every mention, past the strip's item cap")
    func pastTheStripCap() {
        let count = ComposerAttachments.maxItems + 5
        let text = (1...count).map { "@\(Self.store)/\($0).png" }.joined(separator: " ")
        var mapping: [Int: String] = [:]

        let collapsed = ComposerPastedImages.collapse(text, storeDirectory: Self.store, mapping: &mapping)

        #expect(collapsed == (1...count).map(ComposerPastedImages.badge).joined(separator: " "))
        #expect(mapping.count == count)
        #expect(ComposerPastedImages.expand(collapsed, mapping: mapping) == text)
    }

    @Test("expand sets a badge glued to other text apart with spaces")
    func expandBoundary() {
        let path = "\(Self.store)/x.png"
        #expect(ComposerPastedImages.expand("Look:[Pasted image #1],", mapping: [1: path])
            == "Look: @\(path) ,")
    }

    @MainActor
    @Test("the editor copies badges expanded and collapses pasted store mentions")
    func editorPasteboard() throws {
        let path = "\(Self.store)/a.png"
        var mapping = [1: path]
        let textView = ComposerNSTextView(usingTextLayoutManager: false)
        textView.isRichText = false
        textView.smartInsertDeleteEnabled = false
        textView.expandPastedImages = { ComposerPastedImages.expand($0, mapping: mapping) }
        textView.collapsePastedImages = {
            ComposerPastedImages.collapse($0, storeDirectory: Self.store, mapping: &mapping)
        }
        textView.string = "Look [Pasted image #1] "
        textView.setSelectedRange(NSRange(location: 0, length: 22))
        let pasteboard = NSPasteboard.withUniqueName()
        defer { pasteboard.releaseGlobally() }

        #expect(textView.writeSelection(to: pasteboard, types: textView.writablePasteboardTypes))
        #expect(pasteboard.string(forType: .string) == "Look @\(path)")

        pasteboard.clearContents()
        pasteboard.setString("Look @\(path)", forType: .string)
        textView.setSelectedRange(NSRange(location: (textView.string as NSString).length, length: 0))
        #expect(textView.readSelection(from: pasteboard))
        #expect(textView.string == "Look [Pasted image #1] Look [Pasted image #1]")
        #expect(mapping == [1: path])
    }

    @Test("badgeRange finds the badge under an offset")
    func badgeRange() throws {
        // The emoji is 2 UTF-16 units: the badge spans 3..<20.
        let text = "😀 [Pasted image #7] x [Pasted image #]"
        let hit = try #require(ComposerPastedImages.badgeRange(at: 3, in: text))
        #expect(hit.0 == NSRange(location: 3, length: 17))
        #expect(hit.1 == 7)
        #expect(ComposerPastedImages.badgeRange(at: 19, in: text)?.1 == 7)
        for offset in [2, 20, 25, 30] {
            #expect(ComposerPastedImages.badgeRange(at: offset, in: text) == nil, "\(offset)")
        }
    }
}
