// CommandSuggestions.swift
// PopGuy — TerminalComposer
//
// Pure model behind the composer's command suggestion popup: which triggers an agent uses,
// the trigger token at the caret, ranking of catalog items, and the text that accepting one
// inserts. Offsets are UTF-16 (NSString), matching NSTextView.
//
// Isolation: nonisolated — pure functions over values, usable from any context.

import Foundation

nonisolated enum CommandSuggestions {

    /// The trigger token at the caret: `range` covers the trigger up to the caret.
    nonisolated struct Token: Equatable, Sendable {
        let range: NSRange
        let trigger: Character
        let query: String
    }

    /// Claude Code and Gemini use "/" as the first token of the prompt; Codex uses "$" anywhere.
    static func triggers(forAgent agent: String) -> (triggers: Set<Character>, slashOnlyAtStart: Bool) {
        switch agent {
        case "claude", "gemini": return (["/"], true)
        case "codex": return (["$"], false)
        default: return ([], false)
        }
    }

    /// A trigger at the start of the text or after whitespace, followed by non-whitespace up to
    /// the caret. With `slashOnlyAtStart`, "/" counts only when only whitespace precedes it.
    static func activeToken(
        in text: String, caret: Int, triggers: Set<Character>, slashOnlyAtStart: Bool
    ) -> Token? {
        let string = text as NSString
        guard caret > 0, caret <= string.length else { return nil }

        var start = caret
        while start > 0, !isWhitespace(string.character(at: start - 1)) {
            start -= 1
        }
        guard start < caret,
              let scalar = Unicode.Scalar(string.character(at: start)) else { return nil }
        let trigger = Character(scalar)
        guard triggers.contains(trigger) else { return nil }

        if trigger == "/", slashOnlyAtStart {
            let before = string.substring(to: start)
            guard before.allSatisfy(\.isWhitespace) else { return nil }
        }

        let range = NSRange(location: start, length: caret - start)
        let query = string.substring(with: NSRange(location: start + 1, length: caret - start - 1))
        return Token(range: range, trigger: trigger, query: query)
    }

    /// Items with `trigger` whose name contains `query` (case- and diacritic-insensitive):
    /// name prefix first, then name substring; ties by a description match, then by name.
    static func rank(
        _ items: [ComposerCatalogItem], trigger: Character, query: String
    ) -> [ComposerCatalogItem] {
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive]
        func contains(_ text: String) -> Bool {
            query.isEmpty || text.range(of: query, options: options) != nil
        }

        struct Ranked {
            let item: ComposerCatalogItem
            let isPrefix: Bool
            let descriptionMatches: Bool
        }
        let ranked = items
            .filter { $0.trigger == String(trigger) && contains($0.name) }
            .map { item in
                Ranked(
                    item: item,
                    isPrefix: query.isEmpty
                        || item.name.range(of: query, options: options.union(.anchored)) != nil,
                    descriptionMatches: !query.isEmpty && contains(item.description))
            }
        return ranked.sorted { lhs, rhs in
            if lhs.isPrefix != rhs.isPrefix { return lhs.isPrefix }
            if lhs.descriptionMatches != rhs.descriptionMatches { return lhs.descriptionMatches }
            return lhs.item.name.caseInsensitiveCompare(rhs.item.name) == .orderedAscending
        }.map(\.item)
    }

    /// Replaces the token with `<trigger><name> `.
    static func acceptEdit(for token: Token, item: ComposerCatalogItem) -> (range: NSRange, text: String) {
        (token.range, item.trigger + item.name + " ")
    }

    private static func isWhitespace(_ unit: unichar) -> Bool {
        guard let scalar = Unicode.Scalar(unit) else { return false }
        return CharacterSet.whitespacesAndNewlines.contains(scalar)
    }
}
