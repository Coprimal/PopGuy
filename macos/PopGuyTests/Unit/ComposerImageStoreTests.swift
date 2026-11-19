// ComposerImageStoreTests.swift
// PopGuyTests
//
// Tests for pasting images into the Terminal Composer:
//   - a pasted image is saved as a private PNG file and pasted as an "@path" mention
//     (quoted when the path has a space), which Claude Code attaches as an image
//   - copied image files are mentioned in place, without a copy
//   - text on the pasteboard wins (a lone URL next to an image, as browsers copy, does not)
//   - files older than 7 days are pruned

import AppKit
import Testing
@testable import PopGuy

@Suite("Terminal Composer image paste")
struct ComposerImageStoreTests {

    private static func temporaryStore() -> ComposerImageStore {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PopGuyTests-\(UUID().uuidString)", isDirectory: true)
        return ComposerImageStore(directory: directory)
    }

    private static func pasteboard(_ fill: (NSPasteboard) -> Void) -> NSPasteboard {
        let pasteboard = NSPasteboard(name: NSPasteboard.Name("PopGuyTests-\(UUID().uuidString)"))
        pasteboard.clearContents()
        fill(pasteboard)
        return pasteboard
    }

    private static func imageRep() -> NSBitmapImageRep {
        NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 4, pixelsHigh: 4, bitsPerSample: 8,
                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    }

    private static let pngSignature = Data([0x89, 0x50, 0x4E, 0x47])

    @Test("mentions are @path, quoted when the path has a space")
    func mentionFormat() {
        #expect(ComposerImageStore.mention("/tmp/a.png") == "@/tmp/a.png")
        #expect(ComposerImageStore.mention("/tmp/my shots/a.png") == "@\"/tmp/my shots/a.png\"")
    }

    @Test("the inserted text is spaced from the text before it and ends with a space")
    func insertionSpacing() {
        #expect(ComposerImageStore.insertion(for: ["/a.png"], after: nil) == "@/a.png ")
        #expect(ComposerImageStore.insertion(for: ["/a.png"], after: "\n") == "@/a.png ")
        #expect(ComposerImageStore.insertion(for: ["/a.png"], after: " ") == "@/a.png ")
        #expect(ComposerImageStore.insertion(for: ["/a.png", "/b.png"], after: "x") == " @/a.png @/b.png ")
    }

    @Test("a pasted PNG is saved as a private file and its path returned")
    func savesPNG() throws {
        let store = Self.temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        let png = try #require(Self.imageRep().representation(using: .png, properties: [:]))
        let pasteboard = Self.pasteboard { $0.setData(png, forType: .png) }
        defer { pasteboard.releaseGlobally() }

        let paths = try #require(store.imagePaths(from: pasteboard))

        #expect(paths.count == 1)
        let path = try #require(paths.first)
        #expect(path.hasPrefix(store.directory.path + "/") && path.hasSuffix(".png"))
        #expect(FileManager.default.contents(atPath: path) == png)
        let fileMode = try FileManager.default.attributesOfItem(atPath: path)[.posixPermissions] as? Int
        let directoryMode = try FileManager.default.attributesOfItem(atPath: store.directory.path)[.posixPermissions] as? Int
        #expect(fileMode == 0o600)
        #expect(directoryMode == 0o700)
    }

    @Test("a TIFF-only image is converted to PNG")
    func convertsTIFF() throws {
        let store = Self.temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        let tiff = try #require(Self.imageRep().tiffRepresentation)
        let pasteboard = Self.pasteboard { $0.setData(tiff, forType: .tiff) }
        defer { pasteboard.releaseGlobally() }

        let path = try #require(store.imagePaths(from: pasteboard)?.first)

        #expect(FileManager.default.contents(atPath: path)?.prefix(4) == Self.pngSignature)
    }

    @Test("text wins over an image, except a lone URL")
    func textWins() throws {
        let store = Self.temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        let png = try #require(Self.imageRep().representation(using: .png, properties: [:]))

        let withText = Self.pasteboard {
            $0.setString("a table cell", forType: .string)
            $0.setData(png, forType: .png)
        }
        defer { withText.releaseGlobally() }
        #expect(store.imagePaths(from: withText) == nil)

        let textOnly = Self.pasteboard { $0.setString("hello", forType: .string) }
        defer { textOnly.releaseGlobally() }
        #expect(store.imagePaths(from: textOnly) == nil)

        let withURL = Self.pasteboard {
            $0.setString("https://example.com/cat.png", forType: .string)
            $0.setData(png, forType: .png)
        }
        defer { withURL.releaseGlobally() }
        #expect(store.imagePaths(from: withURL)?.count == 1)
    }

    @Test("copied image files are mentioned in place; other files paste as usual")
    func copiedFiles() {
        let store = Self.temporaryStore()
        let images = Self.pasteboard {
            $0.writeObjects([URL(fileURLWithPath: "/tmp/a.png") as NSURL, URL(fileURLWithPath: "/tmp/b c.JPG") as NSURL])
        }
        defer { images.releaseGlobally() }
        #expect(store.imagePaths(from: images) == ["/tmp/a.png", "/tmp/b c.JPG"])

        let mixed = Self.pasteboard {
            $0.writeObjects([URL(fileURLWithPath: "/tmp/a.png") as NSURL, URL(fileURLWithPath: "/tmp/notes.txt") as NSURL])
        }
        defer { mixed.releaseGlobally() }
        #expect(store.imagePaths(from: mixed) == nil)

        // Only the formats Claude Code attaches from a mention.
        let heic = Self.pasteboard { $0.writeObjects([URL(fileURLWithPath: "/tmp/photo.heic") as NSURL]) }
        defer { heic.releaseGlobally() }
        #expect(store.imagePaths(from: heic) == nil)
        #expect(!FileManager.default.fileExists(atPath: store.directory.path))
    }

    @Test("prune deletes images older than 7 days")
    func pruneOld() throws {
        let store = Self.temporaryStore()
        defer { try? FileManager.default.removeItem(at: store.directory) }
        let png = try #require(Self.imageRep().representation(using: .png, properties: [:]))
        let old = try #require(store.save(png: png))
        let fresh = try #require(store.save(png: png))
        try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-8 * 24 * 3600)],
                                              ofItemAtPath: old)

        store.prune()

        #expect(!FileManager.default.fileExists(atPath: old))
        #expect(FileManager.default.fileExists(atPath: fresh))
    }
}
