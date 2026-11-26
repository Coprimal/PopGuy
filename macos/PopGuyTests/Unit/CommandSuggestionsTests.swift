// CommandSuggestionsTests.swift
// PopGuyTests
//
// Tests for the Terminal Composer command suggestion model (CommandSuggestions.swift).
// Covers:
//   - Token detection: "/" only as the first token (Claude), "$" at a word start (Codex),
//     UTF-16 ranges
//   - Ranking: name prefix before name substring; a description-only match is not listed
//   - Accept edit: replaces exactly the token range with "<trigger><name> "

import Foundation
import Testing
@testable import PopGuy

struct CommandSuggestionsTests {

    // MARK: - Token detection

    @Test func slashTokenIsFoundOnlyAsTheFirstTokenForClaude() {
        let (triggers, slashOnlyAtStart) = CommandSuggestions.triggers(forAgent: "claude")
        func token(_ text: String) -> CommandSuggestions.Token? {
            CommandSuggestions.activeToken(
                in: text, caret: (text as NSString).length,
                triggers: triggers, slashOnlyAtStart: slashOnlyAtStart)
        }

        #expect(token("/rev") == .init(range: NSRange(location: 0, length: 4), trigger: "/", query: "rev"))
        #expect(token("  /re") == .init(range: NSRange(location: 2, length: 3), trigger: "/", query: "re"))
        #expect(token("fix this /rev") == nil)
    }

    @Test func dollarTokenIsFoundAtAWordStartForCodex() {
        let (triggers, slashOnlyAtStart) = CommandSuggestions.triggers(forAgent: "codex")
        func token(_ text: String) -> CommandSuggestions.Token? {
            CommandSuggestions.activeToken(
                in: text, caret: (text as NSString).length,
                triggers: triggers, slashOnlyAtStart: slashOnlyAtStart)
        }

        #expect(token("$sk") == .init(range: NSRange(location: 0, length: 3), trigger: "$", query: "sk"))
        // "👍" is two UTF-16 units: the range is in NSString offsets, as NSTextView uses.
        #expect(token("👍 use $pl") == .init(range: NSRange(location: 7, length: 3), trigger: "$", query: "pl"))
        #expect(token("line\n$") == .init(range: NSRange(location: 5, length: 1), trigger: "$", query: ""))
        #expect(token("a$b") == nil)
    }

    // MARK: - Ranking

    @Test func rankingPutsNamePrefixFirstAndIgnoresDescriptionOnlyMatches() {
        let items = [
            ComposerCatalogItem(trigger: "/", name: "code-review", description: ""),
            ComposerCatalogItem(trigger: "/", name: "Review", description: ""),
            ComposerCatalogItem(trigger: "/", name: "deploy", description: "Review then deploy"),
            ComposerCatalogItem(trigger: "$", name: "review-pr", description: ""),
        ]

        let ranked = CommandSuggestions.rank(items, trigger: "/", query: "rev")

        #expect(ranked.map(\.name) == ["Review", "code-review"])
    }

    // MARK: - Accept edit

    @Test func acceptEditReplacesExactlyTheTokenRange() {
        let token = CommandSuggestions.Token(
            range: NSRange(location: 2, length: 3), trigger: "/", query: "re")
        let item = ComposerCatalogItem(trigger: "/", name: "review", description: "Review code")

        let edit = CommandSuggestions.acceptEdit(for: token, item: item)

        #expect(edit.range == NSRange(location: 2, length: 3))
        #expect(edit.text == "/review ")
    }
}
