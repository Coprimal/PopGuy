// ComposerPastedImages.swift
// PopGuy
//
// Short badges for PopGuy's own pasted images in the Terminal Composer. A mention of a
// file directly inside the image store (ComposerImageStore) shows in the editor as
// "[Pasted image #N]" instead of its long cache path, and is expanded back to the
// full mention wherever the text leaves the composer (Done, copy, drafts).
//
// N is per composer session: the caller keeps the mapping (number → path) for the
// whole session, so a deleted badge brought back by undo still expands. Numbers are
// given in text order; a known path keeps its number, a new one gets the highest
// number + 1. A badge without a mapping stays literal.
//
// Pure (text → text). The store test is lexical only (no file system lookup): the
// path's parent, with empty and "." components dropped, must equal the store
// directory; any ".." component disqualifies it.

import Foundation

nonisolated enum ComposerPastedImages {
    /// Longest badge `badgeRange` looks back for (prefix, digits and bracket).
    private static let maxBadgeLength = 64

    static func badge(_ n: Int) -> String {
        "[Pasted image #\(n)]"
    }

    /// Replaces each mention of a file directly in `storeDirectory` with its badge,
    /// adding new paths to `mapping`.
    static func collapse(_ text: String, storeDirectory: String, mapping: inout [Int: String]) -> String {
        guard let store = components(storeDirectory) else { return text }
        var numbers: [String: Int] = [:]
        for (number, path) in mapping where numbers[path].map({ number < $0 }) ?? true {
            numbers[path] = number
        }
        var next = (mapping.keys.max() ?? 0) + 1
        var replacements: [(range: NSRange, text: String)] = []
        for item in ComposerAttachments.parse(text, limit: .max) {
            guard case .file(let path) = item.kind, isDirectChild(path, of: store) else { continue }
            let number: Int
            if let known = numbers[path] {
                number = known
            } else {
                number = next
                next += 1
                numbers[path] = number
                mapping[number] = path
            }
            replacements.append((item.range, badge(number)))
        }
        return replacing(replacements, in: text)
    }

    /// Replaces each badge with a mapping by the full mention of its path. A badge is
    /// found anywhere, but a mention needs whitespace (or the text's start) before it,
    /// and an unquoted one whitespace (or the text's end) after it: a space is added
    /// where the text around the badge has none. A quoted mention ends at its quote,
    /// so text after it stays glued (collapse output round-trips exactly).
    static func expand(_ text: String, mapping: [Int: String]) -> String {
        guard !mapping.isEmpty else { return text }
        let ns = text as NSString
        let replacements = ComposerAttachments.parse(text, limit: .max).compactMap { item -> (range: NSRange, text: String)? in
            guard case .pastedImage(let number) = item.kind, let path = mapping[number] else { return nil }
            let mention = ComposerImageStore.mention(path)
            let before = item.range.location > 0 && !AttachmentScanner.isWhitespace(ns.character(at: item.range.location - 1))
            let after = !mention.hasPrefix("@\"") && NSMaxRange(item.range) < ns.length
                && !AttachmentScanner.isWhitespace(ns.character(at: NSMaxRange(item.range)))
            return (item.range, (before ? " " : "") + mention + (after ? " " : ""))
        }
        return replacing(replacements, in: text)
    }

    /// The badge covering the UTF-16 offset and its number, or nil. Scans only the
    /// code units around the offset (the hover runs on every mouse move).
    static func badgeRange(at utf16Offset: Int, in text: String) -> (NSRange, Int)? {
        let ns = text as NSString
        guard utf16Offset >= 0, utf16Offset < ns.length else { return nil }
        let windowStart = max(0, utf16Offset - maxBadgeLength + 1)
        let windowEnd = min(ns.length, utf16Offset + maxBadgeLength)
        var chars = [unichar](repeating: 0, count: windowEnd - windowStart)
        ns.getCharacters(&chars, range: NSRange(location: windowStart, length: chars.count))
        let scanner = AttachmentScanner(chars: chars)
        let offset = utf16Offset - windowStart
        var index = 0
        while index <= offset {
            if chars[index] == AttachmentScanner.openBracket,
               let found = scanner.numbered(at: index, prefix: AttachmentScanner.pastedImagePrefix) {
                if offset < found.end {
                    return (NSRange(location: windowStart + index, length: found.end - index), found.number)
                }
                index = found.end
            } else {
                index += 1
            }
        }
        return nil
    }

    // MARK: - Helpers

    /// The path's components without empty and "." ones; nil when it is not absolute
    /// or has a ".." component.
    private static func components(_ path: String) -> [Substring]? {
        guard path.hasPrefix("/") else { return nil }
        let parts = path.split(separator: "/").filter { $0 != "." }
        return parts.contains("..") ? nil : parts
    }

    private static func isDirectChild(_ path: String, of store: [Substring]) -> Bool {
        guard let parts = components(path), parts.count == store.count + 1 else { return false }
        return parts.dropLast().elementsEqual(store)
    }

    /// Applies non-overlapping replacements given in text order, from the last one back.
    private static func replacing(_ replacements: [(range: NSRange, text: String)], in text: String) -> String {
        guard !replacements.isEmpty else { return text }
        let result = NSMutableString(string: text)
        for replacement in replacements.reversed() {
            result.replaceCharacters(in: replacement.range, with: replacement.text)
        }
        return result as String
    }
}
