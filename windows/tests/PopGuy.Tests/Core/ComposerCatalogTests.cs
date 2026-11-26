using PopGuy.Core.Composer;

namespace PopGuy.Tests.Core;

// Contract: popguy-edit's "/" and "$" catalog, read from the machine (Claude and Gemini folders, Codex's skills/list
// reply). Same behaviours as macos/PopGuyTests/Unit/ComposerCatalogTests.swift. Every result must encode as a
// catalog line, so the protocol's own caps are the oracle.
public sealed class ComposerCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PopGuyTests-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static void AssertEncodes(string agent, IReadOnlyList<CatalogItem> items) =>
        ComposerMessage.EncodeLine(new ComposerMessage.Catalog(agent, items));

    [Fact]
    public void ClaudeReadsCommandsSkillsAndEnabledPlugins()
    {
        var config = Path.Combine(_root, "config");
        var cwd = Path.Combine(_root, "project");
        Write("config/commands/git/commit.md", "---\r\ndescription: \"Write a commit\"\r\n---\r\nBody");
        Write("config/skills/fold/SKILL.md", "---\nname: cf-fold\ndescription: >\n  Folded over\n  two lines\n---\n");
        Write("config/skills/internal/SKILL.md", "---\nname: internal\nuser-invocable: false\n---\n");
        Write("config/skills/spaced/SKILL.md", "---\nname: bad name\ndescription: dropped\n---\n");
        // Over the description cap, multi-line, with an emoji straddling the cut.
        Write("project/.claude/commands/long.md",
            "---\ndescription: |\n  " + new string('a', 299) + "🎉\n  second line\n---\n");

        var onPath = Path.Combine(_root, "plugins", "on");
        var offPath = Path.Combine(_root, "plugins", "off");
        Write("plugins/on/skills/review/SKILL.md", "---\nname: review\ndescription: 'Review it'\n---\n");
        Write("plugins/off/commands/hidden.md", "# no frontmatter");
        Write("config/plugins/installed_plugins.json",
            "{\"version\":2,\"plugins\":{"
            + "\"on@market\":[{\"scope\":\"user\",\"installPath\":" + Json(onPath) + "}],"
            + "\"off@market\":[{\"scope\":\"user\",\"installPath\":" + Json(offPath) + "}]}}");
        // Later settings files win per key: "off" is enabled by the user and disabled by the project.
        Write("config/settings.json", """{"enabledPlugins":{"on@market":true,"off@market":true}}""");
        Write("project/.claude/settings.local.json", """{"enabledPlugins":{"off@market":false}}""");

        var items = ComposerCatalog.ClaudeItems(config, cwd);

        Assert.Contains(new CatalogItem("/", "git:commit", "Write a commit"), items);
        Assert.Contains(new CatalogItem("/", "cf-fold", "Folded over two lines"), items);
        Assert.Contains(new CatalogItem("/", "on:review", "Review it"), items);
        Assert.DoesNotContain(items, item => item.Name is "internal" or "off:hidden" or "bad name");
        var longItem = Assert.Single(items, item => item.Name == "long");
        Assert.Equal(new string('a', 299), longItem.Description);
        AssertEncodes("claude", items);
    }

    [Fact]
    public void GeminiReadsTomlDescriptionsAndSkipsUntrustedProjects()
    {
        var gemini = Path.Combine(_root, "gemini");
        var cwd = Path.Combine(_root, "work", "project");
        Write("gemini/commands/git/push.toml", "# comment\ndescription = \"Push \\\"it\\\"\"\nprompt = \"\"\"\nGo\n\"\"\"\n");
        Write("gemini/extensions/ext/commands/deploy.toml", "description = 'Deploy now'\n[other]\ndescription = \"no\"\n");
        Write("work/project/.gemini/commands/local.toml", "prompt = \"x\"\n");

        var trusted = ComposerCatalog.GeminiItems(gemini, cwd);
        Assert.Contains(new CatalogItem("/", "git:push", "Push \"it\""), trusted);
        Assert.Contains(new CatalogItem("/", "deploy", "Deploy now"), trusted);
        Assert.Contains(new CatalogItem("/", "local", ""), trusted);
        AssertEncodes("gemini", trusted);

        // A parent folder marked DO_NOT_TRUST hides the project's commands.
        Write("gemini/trustedFolders.json",
            "{" + Json(Path.Combine(_root, "work")) + ":\"DO_NOT_TRUST\"}");
        var untrusted = ComposerCatalog.GeminiItems(gemini, cwd);
        Assert.DoesNotContain(untrusted, item => item.Name == "local");
        Assert.Contains(untrusted, item => item.Name == "git:push");
    }

    [Fact]
    public void SkillsListReplyKeepsEnabledSkillsOnly()
    {
        const string reply = """
            {"id":2,"result":{"data":[{"cwd":"/w","skills":[
              {"name":"cf-plan","description":"Plan a feature","enabled":true,"scope":"user"},
              {"name":"off","description":"Disabled","enabled":false},
              {"name":42,"enabled":true},
              {"name":"system-skill","enabled":true,"scope":"system"}]}]}}
            """;

        var items = ComposerCatalog.ParseSkillsListReply(reply);

        Assert.Equal([new CatalogItem("$", "cf-plan", "Plan a feature"), new CatalogItem("$", "system-skill", "")], items);
        Assert.Empty(ComposerCatalog.ParseSkillsListReply("not json"));
        AssertEncodes("codex", items);
    }

    [Theory]
    [InlineData("claude-prompt-1234.md", "claude")]
    [InlineData("gemini-edit-AbC/prompt.md", "gemini")]
    [InlineData("repo/.git/COMMIT_EDITMSG", null)]
    public void DetectsAgentFromTheFileName(string relativePath, string? expected) =>
        Assert.Equal(expected, ComposerCatalog.DetectAgentFromFile(Path.Combine(_root, relativePath)));
}
