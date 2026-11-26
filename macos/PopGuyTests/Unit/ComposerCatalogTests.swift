// ComposerCatalogTests.swift
// PopGuyTests
//
// Tests for the Terminal Composer catalog scanner (app copy of ComposerCatalog.swift,
// MIRROR of macos/PopGuyEdit/ComposerCatalog.swift), on temp-folder fixtures.
// Covers:
//   - Claude: nested command names, folded skill descriptions, user-invocable: false,
//     enabled vs disabled plugins, a plugin name not prefixed twice, the signed-in
//     account's synced skills only (non-UUID account ids ignored, a global config over
//     1 MiB still read), the "synced" folder skipped in the user config only
//   - Gemini: TOML description, project commands skipped in a DO_NOT_TRUST folder
//   - Codex: skills/list reply keeps enabled skills only
//   - Agent detection from the temp file path
// Every returned catalog must be accepted by the protocol encoder.

import Foundation
import Testing
@testable import PopGuy

@Suite("Terminal Composer catalog scanner")
struct ComposerCatalogTests {

    @Test("Claude: commands, skills and enabled plugins, project before user")
    func claudeItems() throws {
        let root = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: root) }
        let config = root.appendingPathComponent("config")
        let cwd = root.appendingPathComponent("project")

        try write("---\ndescription: Commit staged changes\n---\nBody", config, "commands/git/commit.md")
        try write("---\nname: review\ndescription: >\n  Review the\n  current diff\nuser-invocable: true\n---\n",
                  config, "skills/review/SKILL.md")
        try write("---\nname: hidden\ndescription: Internal\nuser-invocable: false\n---\n",
                  config, "skills/hidden/SKILL.md")
        // The project item wins over the user item with the same name.
        try write("---\ndescription: 'Project commit'\n---\n", cwd, ".claude/commands/git/commit.md")

        let enabledPath = root.appendingPathComponent("plugins/cf").path
        let disabledPath = root.appendingPathComponent("plugins/off").path
        try write("---\nname: cf-plan\ndescription: \"Plan a feature\"\n---\n",
                  root, "plugins/cf/skills/plan/SKILL.md")
        // A plugin skill already named "<plugin>:…" is not prefixed twice.
        try write("---\nname: coding-friend:fancy\n---\n", root, "plugins/cf/skills/fancy/SKILL.md")
        try write("---\ndescription: Off\n---\n", root, "plugins/off/commands/nope.md")
        try write("""
            {"version":2,"plugins":{
              "coding-friend@market":[{"scope":"user","installPath":"\(enabledPath)"}],
              "offplug@market":[{"scope":"user","installPath":"\(disabledPath)"}]}}
            """, config, "plugins/installed_plugins.json")
        try write(#"{"enabledPlugins":{"coding-friend@market":false,"offplug@market":true}}"#,
                  config, "settings.json")
        // Later settings win per key.
        try write(#"{"enabledPlugins":{"coding-friend@market":true}}"#, cwd, ".claude/settings.json")
        try write(#"{"enabledPlugins":{"offplug@market":false}}"#, cwd, ".claude/settings.local.json")

        // Synced claude.ai skills: only the signed-in account's folder counts.
        let org = "0A1B2C3D-0000-4000-8000-000000000001", account = "0a1b2c3d-0000-4000-8000-000000000002"
        try write("---\nname: pdf\ndescription: PDF tools\n---\n", config, "skills/synced/\(org)_\(account)/pdf/SKILL.md")
        try write("---\nname: other\n---\n", config, "skills/synced/\(org)_\(org)/other/SKILL.md")
        try write("---\nname: leak\n---\n", config, "skills/x_y/leak/SKILL.md")
        // Only the user config's "synced" folder is skipped; a project skill folder named so is listed.
        try write("---\nname: synced\n---\n", config, "skills/synced/SKILL.md")
        try write("---\nname: project-synced\n---\n", cwd, ".claude/skills/synced/SKILL.md")
        // The global config grows past the 1 MiB JSON cap of the other config files.
        let padding = String(repeating: "x", count: 1_100_000)
        let globalConfig = root.appendingPathComponent("claude.json")
        try write(#"{"pad":"\#(padding)","oauthAccount":{"organizationUuid":"\#(org)","accountUuid":"\#(account)"}}"#,
                  root, "claude.json")

        let items = ComposerCatalog.claudeItems(configDir: config, cwd: cwd, globalConfigFile: globalConfig)

        #expect(items == [
            ComposerCatalogItem(trigger: "/", name: "anthropic-skills:pdf", description: "PDF tools"),
            ComposerCatalogItem(trigger: "/", name: "coding-friend:cf-plan", description: "Plan a feature"),
            ComposerCatalogItem(trigger: "/", name: "coding-friend:fancy", description: ""),
            ComposerCatalogItem(trigger: "/", name: "git:commit", description: "Project commit"),
            ComposerCatalogItem(trigger: "/", name: "project-synced", description: ""),
            ComposerCatalogItem(trigger: "/", name: "review", description: "Review the current diff"),
        ])
        _ = try ComposerMessage.encodeLine(.catalog(agent: "claude", items: items))

        // A non-UUID account id never becomes a path ("skills/synced/../x_y" holds "leak").
        try write(#"{"oauthAccount":{"organizationUuid":"../x","accountUuid":"y"}}"#, root, "claude.json")
        let unsafe = ComposerCatalog.claudeItems(configDir: config, cwd: cwd, globalConfigFile: globalConfig)
        #expect(!unsafe.contains { $0.name.hasPrefix("anthropic-skills:") })
    }

    @Test("Gemini: TOML descriptions, project commands skipped in an untrusted folder")
    func geminiItems() throws {
        let root = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: root) }
        let gemini = root.appendingPathComponent("gemini")
        let cwd = root.appendingPathComponent("work/project")

        try write("description = \"Run the \\\"tests\\\"\"\nprompt = \"\"\"\nRun them\n\"\"\"\n",
                  gemini, "commands/test/run.toml")
        try write("prompt = \"Deploy\"\n[extra]\ndescription = \"not top-level\"\n",
                  gemini, "extensions/ops/commands/deploy.toml")
        try write("description = 'Project only'\n", cwd, ".gemini/commands/local.toml")

        let trusted = ComposerCatalog.geminiItems(geminiDir: gemini, cwd: cwd)
        #expect(trusted == [
            ComposerCatalogItem(trigger: "/", name: "deploy", description: ""),
            ComposerCatalogItem(trigger: "/", name: "local", description: "Project only"),
            ComposerCatalogItem(trigger: "/", name: "test:run", description: "Run the \"tests\""),
        ])
        _ = try ComposerMessage.encodeLine(.catalog(agent: "gemini", items: trusted))

        // A parent folder marked DO_NOT_TRUST hides the project's commands.
        let parent = root.appendingPathComponent("work").path
        try write(#"{"\#(parent)":"DO_NOT_TRUST"}"#, gemini, "trustedFolders.json")
        let untrusted = ComposerCatalog.geminiItems(geminiDir: gemini, cwd: cwd)
        #expect(untrusted.map(\.name) == ["deploy", "test:run"])
    }

    @Test("Codex: skills/list reply keeps enabled skills only")
    func codexSkillsListReply() throws {
        let reply = #"""
            {"id":2,"result":{"data":[{"cwd":"/tmp/p","skills":[
              {"name":"cf-plan","description":"Plan\na feature","enabled":true,"scope":"user"},
              {"name":"old","description":"Disabled","enabled":false},
              {"name":"numeric","description":"Not a bool","enabled":1},
              {"description":"No name","enabled":true},
              {"name":"imagegen","description":"Images","enabled":true,"scope":"system"}]}]}}
            """#
        let items = ComposerCatalog.parseSkillsListReply(Data(reply.utf8))
        #expect(items == [
            ComposerCatalogItem(trigger: "$", name: "cf-plan", description: "Plan a feature"),
            ComposerCatalogItem(trigger: "$", name: "imagegen", description: "Images"),
        ])
        _ = try ComposerMessage.encodeLine(.catalog(agent: "codex", items: items))
    }

    @Test("agent detected from the temp file name or its folder")
    func detectAgent() {
        #expect(ComposerCatalog.detectAgent(filePath: "/tmp/claude-prompt-1a2b.md") == "claude")
        #expect(ComposerCatalog.detectAgent(filePath: "/tmp/gemini-edit-Xy12/prompt.md") == "gemini")
        #expect(ComposerCatalog.detectAgent(filePath: "/repo/.git/COMMIT_EDITMSG") == nil)
    }

    // MARK: - Helpers

    private func makeTempDir() throws -> URL {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ComposerCatalogTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private func write(_ text: String, _ base: URL, _ relativePath: String) throws {
        let url = base.appendingPathComponent(relativePath)
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true
        )
        try Data(text.utf8).write(to: url)
    }
}
