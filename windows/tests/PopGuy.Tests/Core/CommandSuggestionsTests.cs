using PopGuy.Core.Composer;

namespace PopGuy.Tests.Core;

// The Terminal Composer's command suggestions (pure model), as in
// macos/PopGuyTests/Unit/CommandSuggestionsTests.swift: the "/" or "$" token at the caret, the ranking of the CLI's
// catalog items, and the edit that accepts one.
public sealed class CommandSuggestionsTests
{
    private static CommandSuggestions.Token? TokenFor(string agent, string text, int? caret = null)
    {
        var (triggers, slashOnlyAtStart) = CommandSuggestions.Triggers(agent);
        return CommandSuggestions.ActiveToken(text, caret ?? text.Length, triggers, slashOnlyAtStart);
    }

    [Fact]
    public void ClaudeSlashTokenOnlyAsTheFirstTokenOfThePrompt()
    {
        Assert.Equal(new CommandSuggestions.Token(0, 4, '/', "rev"), TokenFor("claude", "/rev"));
        Assert.Equal(new CommandSuggestions.Token(3, 3, '/', "re"), TokenFor("claude", " \r /re"));
        Assert.Null(TokenFor("claude", "fix this /rev"));
        Assert.Null(TokenFor("claude", "$rev"));
    }

    [Fact]
    public void CodexDollarTokenAtAnyTokenStartButNotInsideAWord()
    {
        Assert.Equal(new CommandSuggestions.Token(0, 3, '$', "pl"), TokenFor("codex", "$pl"));
        Assert.Equal(new CommandSuggestions.Token(6, 4, '$', "pla"), TokenFor("codex", "first\r$pla"));
        Assert.Equal(new CommandSuggestions.Token(4, 2, '$', "p"), TokenFor("codex", "use $p and", caret: 6));
        Assert.Null(TokenFor("codex", "a$b"));
        Assert.Null(TokenFor("codex", "$pl ", caret: 4));
        Assert.Null(TokenFor("codex", "$pl", caret: 0));
    }

    [Fact]
    public void RankPutsNamePrefixBeforeNameSubstringAndDropsDescriptionOnlyMatches()
    {
        var items = new[]
        {
            new CatalogItem("/", "code-review", "Review the diff"),
            new CatalogItem("/", "commit", "Write a review-ready commit"),
            new CatalogItem("/", "Review-PR", "Open the pull request"),
            new CatalogItem("$", "review", "A Codex skill"),
        };

        var ranked = CommandSuggestions.Rank(items, '/', "review");

        Assert.Equal(["Review-PR", "code-review"], ranked.Select(i => i.Name));
    }

    [Fact]
    public void AcceptEditReplacesExactlyTheTokenWithTheTriggerAndName()
    {
        const string text = "/rev then";
        var token = CommandSuggestions.ActiveToken(text, 4, new HashSet<char> { '/' }, slashOnlyAtStart: true)!.Value;

        var (start, length, insert) = CommandSuggestions.AcceptEdit(token, new CatalogItem("/", "review", "Review"));

        Assert.Equal("/review  then", text[..start] + insert + text[(start + length)..]);
    }
}
