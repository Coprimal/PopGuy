// ComposerAttachments.swift
// PopGuy
//
// Finds the attachments in the Terminal Composer's plain text, for the image strip:
// image mentions written by `ComposerImageStore.mention`, the "[Image #N]"
// placeholders a CLI (Claude Code) puts in its prompt, and the composer's own
// "[Pasted image #N]" badges (ComposerPastedImages). Pure (text → items).
//
// Rules (one pass over the UTF-16 code units, no regex):
//   - A mention starts with "@" at the start of the text or right after whitespace
//     (space, tab, LF, CR, VT).
//   - "@path" ends at the first whitespace; "@\"path\"" ends at the next quote on the
//     same line (no escaping). An unquoted mention, or a closed quoted one, is skipped
//     whole even when it does not qualify; an unclosed quote (line or text ends first)
//     is no mention and the scan goes on at the next code unit.
//   - A mention qualifies when its path starts with "/" (absolute local; no "~", no
//     relative path) and its extension (after the last "." of the last "/" component,
//     case-insensitive) is an image extension. No punctuation is trimmed.
//   - "[Image #<ASCII digits>]" is a CLI image placeholder, "[Pasted image #<ASCII
//     digits>]" a pasted image badge (a number that overflows Int is no token).
//   - One item per occurrence, in text order, at most `maxItems` (or the given limit).
// Ranges are UTF-16 NSRanges into the original string (every delimiter is ASCII, so
// no surrogate is ever split).

import Foundation

nonisolated enum ComposerAttachments {
    nonisolated enum Kind: Equatable, Sendable {
        case file(path: String)
        case cliImage(number: Int)
        case pastedImage(number: Int)
    }

    nonisolated struct Item: Equatable, Sendable {
        let kind: Kind
        let range: NSRange
        let token: String
    }

    static let maxItems = 50

    /// `limit`: the most items returned (the strip shows `maxItems`; rewriting the
    /// text needs them all).
    static func parse(_ text: String, limit: Int = maxItems) -> [Item] {
        let ns = text as NSString
        var chars = [unichar](repeating: 0, count: ns.length)
        ns.getCharacters(&chars, range: NSRange(location: 0, length: ns.length))
        let scanner = AttachmentScanner(chars: chars)

        var items: [Item] = []
        var index = 0
        while index < chars.count, items.count < limit {
            let found: (end: Int, kind: Kind?)?
            if chars[index] == AttachmentScanner.at, index == 0 || AttachmentScanner.isWhitespace(chars[index - 1]) {
                found = scanner.mention(at: index)
            } else if chars[index] == AttachmentScanner.openBracket {
                found = scanner.placeholder(at: index)
            } else {
                found = nil
            }
            guard let found else {
                index += 1
                continue
            }
            if let kind = found.kind {
                let range = NSRange(location: index, length: found.end - index)
                items.append(Item(kind: kind, range: range, token: ns.substring(with: range)))
            }
            index = found.end
        }
        return items
    }

    /// The range to delete for an item: its token plus one adjacent space (the one
    /// after it, else the one before it), or the token alone.
    static func removalRange(for range: NSRange, in text: String) -> NSRange {
        let ns = text as NSString
        guard range.location >= 0, NSMaxRange(range) <= ns.length else { return range }
        if NSMaxRange(range) < ns.length, ns.character(at: NSMaxRange(range)) == AttachmentScanner.space {
            return NSRange(location: range.location, length: range.length + 1)
        }
        if range.location > 0, ns.character(at: range.location - 1) == AttachmentScanner.space {
            return NSRange(location: range.location - 1, length: range.length + 1)
        }
        return range
    }
}

/// Scans the whole text as UTF-16 code units (also used by ComposerPastedImages).
nonisolated struct AttachmentScanner {
    let chars: [unichar]

    static let at = unichar(UInt8(ascii: "@"))
    static let quote = unichar(UInt8(ascii: "\""))
    static let openBracket = unichar(UInt8(ascii: "["))
    static let closeBracket = unichar(UInt8(ascii: "]"))
    static let hash = unichar(UInt8(ascii: "#"))
    static let slash = unichar(UInt8(ascii: "/"))
    static let dot = unichar(UInt8(ascii: "."))
    static let space = unichar(UInt8(ascii: " "))
    private static let imagePrefix = Array("[Image #".utf16)
    static let pastedImagePrefix = Array("[Pasted image #".utf16)

    static func isWhitespace(_ char: unichar) -> Bool {
        char == space || char == 0x09 || char == 0x0A || char == 0x0B || char == 0x0D
    }

    private static func isLineBreak(_ char: unichar) -> Bool {
        char == 0x0A || char == 0x0D
    }

    static func isDigit(_ char: unichar) -> Bool {
        (48...57).contains(char)
    }

    /// The end of the mention starting with "@" at `start` and its kind (nil when it
    /// does not qualify), or nil when it is no mention (an unclosed quote).
    func mention(at start: Int) -> (end: Int, kind: ComposerAttachments.Kind?)? {
        let pathStart = start + 1
        if pathStart < chars.count, chars[pathStart] == Self.quote {
            var close = pathStart + 1
            while close < chars.count, chars[close] != Self.quote {
                if Self.isLineBreak(chars[close]) { return nil }
                close += 1
            }
            guard close < chars.count else { return nil }
            return (close + 1, fileKind(pathStart + 1..<close))
        }
        var end = pathStart
        while end < chars.count, !Self.isWhitespace(chars[end]) { end += 1 }
        return (end, fileKind(pathStart..<end))
    }

    /// The end of a "[Image #<digits>]" or "[Pasted image #<digits>]" placeholder at
    /// `start` and its kind, or nil.
    func placeholder(at start: Int) -> (end: Int, kind: ComposerAttachments.Kind?)? {
        if let found = numbered(at: start, prefix: Self.imagePrefix) {
            return (found.end, .cliImage(number: found.number))
        }
        if let found = numbered(at: start, prefix: Self.pastedImagePrefix) {
            return (found.end, .pastedImage(number: found.number))
        }
        return nil
    }

    /// The end and number of "<prefix><digits>]" at `start`, or nil (also when the
    /// number overflows Int).
    func numbered(at start: Int, prefix: [unichar]) -> (end: Int, number: Int)? {
        let digitsStart = start + prefix.count
        guard digitsStart <= chars.count,
              chars[start..<digitsStart].elementsEqual(prefix) else { return nil }
        var digitsEnd = digitsStart
        while digitsEnd < chars.count, Self.isDigit(chars[digitsEnd]) { digitsEnd += 1 }
        guard digitsEnd > digitsStart, digitsEnd < chars.count, chars[digitsEnd] == Self.closeBracket,
              let number = Int(String(decoding: chars[digitsStart..<digitsEnd], as: UTF16.self)) else { return nil }
        return (digitsEnd + 1, number)
    }

    /// `.file` when the path is absolute and has an image extension.
    private func fileKind(_ path: Range<Int>) -> ComposerAttachments.Kind? {
        guard !path.isEmpty, chars[path.lowerBound] == Self.slash else { return nil }
        let nameStart = (chars[path].lastIndex(of: Self.slash) ?? path.lowerBound) + 1
        guard let dot = chars[nameStart..<path.upperBound].lastIndex(of: Self.dot) else { return nil }
        let ext = String(decoding: chars[(dot + 1)..<path.upperBound], as: UTF16.self).lowercased()
        guard ComposerImageStore.imageExtensions.contains(ext) else { return nil }
        return .file(path: String(decoding: chars[path], as: UTF16.self))
    }
}
