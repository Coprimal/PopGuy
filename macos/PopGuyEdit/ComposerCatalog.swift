// ComposerCatalog.swift
//
// Terminal Composer command catalog: the calling agent's "/" or "$" commands, read from
// this machine by the popguy-edit CLI (macos/PopGuyEdit/) and tested through the PopGuy
// app's copy (macos/PopGuy/Modules/TerminalComposer/).
// MIRROR: both copies must stay byte-identical. Foundation only (no AppKit, no packages),
// so it compiles in the command-line tool target too. The Windows twin
// (windows/src/PopGuy.Core/Composer/ComposerCatalog.cs) follows the same rules.
//
// Sources (names and descriptions only):
//   claude "/": <cwd>/.claude, then <configDir> (commands/**/*.md, skills/*/SKILL.md), then
//               enabled plugins' commands and skills named "<plugin>:<name>"
//   gemini "/": <cwd>/.gemini (unless DO_NOT_TRUST), ~/.gemini, extensions: commands/**/*.toml
//   codex  "$": the enabled skills of an app-server skills/list reply
// Walks skip hidden entries, never enter a symlinked directory below a source, go at
// most 4 directory levels below it and visit at most 2,000 entries per source. Command
// files are read up to 64 KB, JSON configs up to 1 MB; IO errors are ignored (an
// unreadable command keeps its name). `finish` makes every item valid for the protocol.

import Foundation

nonisolated enum ComposerCatalog {

    static let maxWalkDepth = 4
    static let maxEntriesPerSource = 2_000
    static let maxCommandFileBytes = 65_536
    static let maxConfigFileBytes = 1_048_576

    // MARK: - Agent

    /// The agent named by the editor's temp file: `claude-prompt-*.md` (Claude Code) or a
    /// file inside a `gemini-edit-*` folder (Gemini CLI). Nil when the path names neither.
    static func detectAgent(filePath: String) -> String? {
        let url = URL(fileURLWithPath: filePath)
        let fileName = url.lastPathComponent
        if fileName.hasPrefix("claude-prompt-"), fileName.hasSuffix(".md") { return "claude" }
        if url.deletingLastPathComponent().lastPathComponent.hasPrefix("gemini-edit-") { return "gemini" }
        return nil
    }

    // MARK: - Claude Code

    /// Claude Code commands and skills. `configDir` is $CLAUDE_CONFIG_DIR or ~/.claude.
    /// Project items come first, so they win over user and plugin items with the same name.
    static func claudeItems(configDir: URL, cwd: URL) -> [ComposerCatalogItem] {
        let project = cwd.appendingPathComponent(".claude")
        var items = claudeSourceItems(project, prefix: "")
        items += claudeSourceItems(configDir, prefix: "")

        let enabled = enabledPluginKeys([
            configDir.appendingPathComponent("settings.json"),
            project.appendingPathComponent("settings.json"),
            project.appendingPathComponent("settings.local.json"),
        ])
        for plugin in installedPlugins(configDir) where enabled.contains(plugin.key) {
            let name = plugin.key.split(separator: "@", maxSplits: 1, omittingEmptySubsequences: false)[0]
            guard !name.isEmpty else { continue }
            items += claudeSourceItems(plugin.installPath, prefix: String(name) + ":")
        }
        return finish(items)
    }

    /// Commands (`commands/**/*.md`) then skills (`skills/*/SKILL.md`) of one Claude folder.
    /// An unreadable command file keeps its name with an empty description.
    private static func claudeSourceItems(_ base: URL, prefix: String) -> [ComposerCatalogItem] {
        var items: [ComposerCatalogItem] = []
        for file in walk(base.appendingPathComponent("commands"), fileExtension: "md") {
            items.append(ComposerCatalogItem(
                trigger: "/", name: prefix + file.components.joined(separator: ":"),
                description: frontmatter(readText(file.url) ?? "")["description"] ?? ""
            ))
        }
        for skill in childDirectories(base.appendingPathComponent("skills")) {
            guard let text = readText(skill.appendingPathComponent("SKILL.md")) else { continue }
            let fields = frontmatter(text)
            if fields["user-invocable"]?.lowercased() == "false" { continue }
            let name = fields["name"].flatMap { $0.isEmpty ? nil : $0 } ?? skill.lastPathComponent
            items.append(ComposerCatalogItem(
                trigger: "/", name: prefix + name, description: fields["description"] ?? ""
            ))
        }
        return items
    }

    /// `enabledPlugins` keys set to true, merged over the settings files (later wins per key).
    private static func enabledPluginKeys(_ settingsFiles: [URL]) -> Set<String> {
        var merged: [String: Bool] = [:]
        for file in settingsFiles {
            guard let root = readJSON(file) as? [String: Any],
                  let plugins = root["enabledPlugins"] as? [String: Any] else { continue }
            for (key, value) in plugins { merged[key] = isJSONTrue(value) }
        }
        return Set(merged.filter(\.value).keys)
    }

    /// `plugins/installed_plugins.json` (version 2): "<plugin>@<marketplace>" → its first
    /// install path that exists, sorted by key.
    private static func installedPlugins(_ configDir: URL) -> [InstalledPlugin] {
        let file = configDir.appendingPathComponent("plugins").appendingPathComponent("installed_plugins.json")
        guard let root = readJSON(file) as? [String: Any],
              let plugins = root["plugins"] as? [String: Any] else { return [] }
        var result: [InstalledPlugin] = []
        for key in plugins.keys.sorted() {
            guard let entries = plugins[key] as? [Any] else { continue }
            let paths = entries.compactMap { ($0 as? [String: Any])?["installPath"] as? String }
            guard let path = paths.first(where: isDirectory) else { continue }
            result.append(InstalledPlugin(key: key, installPath: URL(fileURLWithPath: path)))
        }
        return result
    }

    private nonisolated struct InstalledPlugin {
        let key: String
        let installPath: URL
    }

    // MARK: - Gemini CLI

    /// Gemini CLI custom commands. `geminiDir` is ~/.gemini. Project commands come first
    /// and are left out when trustedFolders.json marks the cwd or a parent DO_NOT_TRUST.
    static func geminiItems(geminiDir: URL, cwd: URL) -> [ComposerCatalogItem] {
        var items: [ComposerCatalogItem] = []
        if !isUntrusted(cwd, geminiDir: geminiDir) {
            items += geminiCommands(cwd.appendingPathComponent(".gemini").appendingPathComponent("commands"))
        }
        items += geminiCommands(geminiDir.appendingPathComponent("commands"))
        for extensionDir in childDirectories(geminiDir.appendingPathComponent("extensions")) {
            items += geminiCommands(extensionDir.appendingPathComponent("commands"))
        }
        return finish(items)
    }

    private static func geminiCommands(_ root: URL) -> [ComposerCatalogItem] {
        walk(root, fileExtension: "toml").map { file in
            ComposerCatalogItem(
                trigger: "/", name: file.components.joined(separator: ":"),
                description: tomlDescription(readText(file.url) ?? "")
            )
        }
    }

    private static func isUntrusted(_ cwd: URL, geminiDir: URL) -> Bool {
        guard let rules = readJSON(geminiDir.appendingPathComponent("trustedFolders.json"))
                as? [String: Any] else { return false }
        let targets = pathForms(cwd)
        for (path, value) in rules where value as? String == "DO_NOT_TRUST" {
            for rule in pathForms(URL(fileURLWithPath: path)) {
                if targets.contains(where: { $0.starts(with: rule) }) { return true }
            }
        }
        return false
    }

    /// Path components as written and with symlinks resolved (e.g. /var vs /private/var).
    private static func pathForms(_ url: URL) -> [[String]] {
        [url.standardizedFileURL.pathComponents, url.resolvingSymlinksInPath().pathComponents]
    }

    /// The top-level `description = "…"` or `'…'` single-line value; empty when missing,
    /// multi-line or after the first `[table]` header. Lines inside a `"""` or `'''`
    /// string are skipped. Quoted as in `quotedValue`.
    private static func tomlDescription(_ text: String) -> String {
        var inMultiLine = false
        for rawLine in lines(text) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            let wasInMultiLine = inMultiLine
            if (occurrences(of: "\"\"\"", in: line) + occurrences(of: "'''", in: line)) % 2 == 1 {
                inMultiLine.toggle()
            }
            if wasInMultiLine || line.isEmpty || line.hasPrefix("#") { continue }
            if line.hasPrefix("[") { return "" }
            guard line.hasPrefix("description") else { continue }
            let afterKey = line.dropFirst("description".count).drop { $0 == " " || $0 == "\t" }
            guard afterKey.first == "=" else { continue }
            let value = String(afterKey.dropFirst().drop { $0 == " " || $0 == "\t" })
            if value.hasPrefix("\"\"\"") || value.hasPrefix("'''") { return "" }
            return quotedValue(value) ?? ""
        }
        return ""
    }

    private static func occurrences(of token: String, in line: String) -> Int {
        line.components(separatedBy: token).count - 1
    }

    // MARK: - Codex

    /// The enabled skills of a Codex app-server `skills/list` JSON-RPC reply
    /// (`result.data[].skills[]`). Malformed entries are skipped.
    static func parseSkillsListReply(_ data: Data) -> [ComposerCatalogItem] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let result = root["result"] as? [String: Any],
              let entries = result["data"] as? [Any] else { return [] }
        var items: [ComposerCatalogItem] = []
        for case let entry as [String: Any] in entries {
            guard let skills = entry["skills"] as? [Any] else { continue }
            for case let skill as [String: Any] in skills where isJSONTrue(skill["enabled"]) {
                guard let name = skill["name"] as? String else { continue }
                items.append(ComposerCatalogItem(
                    trigger: "$", name: name, description: skill["description"] as? String ?? ""
                ))
            }
        }
        return finish(items)
    }

    // MARK: - Finish

    /// Makes a catalog valid for the protocol: single-line descriptions of at most 300
    /// UTF-16 units, valid names and triggers only, first item per trigger + name kept,
    /// sorted by name (case-insensitive, ordinal), at most 500 items.
    static func finish(_ items: [ComposerCatalogItem]) -> [ComposerCatalogItem] {
        var seen: Set<String> = []
        var kept: [ComposerCatalogItem] = []
        for item in items where ComposerMessage.catalogTriggers.contains(item.trigger) && isValidName(item.name) {
            guard seen.insert(item.trigger + item.name).inserted else { continue }
            kept.append(ComposerCatalogItem(
                trigger: item.trigger, name: item.name, description: sanitizeDescription(item.description)
            ))
        }
        kept.sort { lhs, rhs in
            let left = lhs.name.uppercased(), right = rhs.name.uppercased()
            if left != right { return left.utf16.lexicographicallyPrecedes(right.utf16) }
            if lhs.name != rhs.name { return lhs.name.utf16.lexicographicallyPrecedes(rhs.name.utf16) }
            return lhs.trigger.utf16.lexicographicallyPrecedes(rhs.trigger.utf16)
        }
        return Array(kept.prefix(ComposerMessage.maxCatalogItems))
    }

    private static func isValidName(_ name: String) -> Bool {
        !name.isEmpty && name.utf16.count <= ComposerMessage.maxItemNameLength
            && name.unicodeScalars.allSatisfy {
                !$0.properties.isWhitespace && $0.properties.generalCategory != .control
            }
    }

    /// Control characters and line/paragraph separators become spaces, whitespace runs
    /// collapse to one space, ends are trimmed, and the text is cut on a character
    /// boundary at the description cap.
    private static func sanitizeDescription(_ text: String) -> String {
        var collapsed = String.UnicodeScalarView()
        var pendingSpace = false
        for scalar in text.unicodeScalars {
            if scalar.properties.isWhitespace || scalar.properties.generalCategory == .control
                || scalar == "\u{2028}" || scalar == "\u{2029}" {
                pendingSpace = true
                continue
            }
            if pendingSpace, !collapsed.isEmpty { collapsed.append(" ") }
            pendingSpace = false
            collapsed.append(scalar)
        }
        var result = ""
        var units = 0
        for character in String(collapsed) {
            let count = character.utf16.count
            guard units + count <= ComposerMessage.maxItemDescriptionLength else { break }
            result.append(character)
            units += count
        }
        while result.last == " " { result.removeLast() }
        return result
    }

    // MARK: - Frontmatter

    /// Top-level keys of a frontmatter block (first line "---", up to the next "---" line).
    /// Values: plain (a " #" comment dropped), single- or double-quoted, folded `>` (lines
    /// joined with spaces) or literal `|` block scalars. Continuation lines are the blank
    /// or indented lines after a key; plain and quoted values join them with spaces.
    /// Nested maps and lists are not understood. No block means no keys.
    private static func frontmatter(_ text: String) -> [String: String] {
        let allLines = lines(text)
        guard let first = allLines.first, isMarker(first),
              let end = allLines.indices.dropFirst().first(where: { isMarker(allLines[$0]) })
        else { return [:] }

        var fields: [String: String] = [:]
        var index = 1
        while index < end {
            let line = allLines[index]
            index += 1
            guard let lead = line.first, !lead.isWhitespace, lead != "#",
                  let colon = line.firstIndex(of: ":"), colon != line.startIndex else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces)
            let head = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            var continuation: [Substring] = []
            while index < end, allLines[index].first?.isWhitespace ?? true {
                continuation.append(allLines[index])
                index += 1
            }
            switch head.first {
            case ">":
                fields[key] = blockLines(continuation).filter { !$0.isEmpty }.joined(separator: " ")
            case "|":
                var literal = blockLines(continuation).joined(separator: "\n")
                while literal.last == "\n" { literal.removeLast() }
                fields[key] = literal
            case "\"", "'":
                fields[key] = quotedValue(joined(head, continuation)) ?? ""
            default:
                fields[key] = joined(stripComment(head), continuation)
            }
        }
        return fields
    }

    private static func isMarker(_ line: Substring) -> Bool {
        trimEnd(line) == "---"
    }

    /// A block scalar's lines without their common indentation, trailing whitespace trimmed.
    private static func blockLines(_ lines: [Substring]) -> [String] {
        let indent = lines.filter { !$0.allSatisfy(\.isWhitespace) }
            .map { $0.prefix(while: \.isWhitespace).count }
            .min() ?? 0
        return lines.map { $0.count >= indent ? trimEnd($0.dropFirst(indent)) : "" }
    }

    /// `head` and the trimmed, non-empty continuation lines, joined with single spaces.
    private static func joined(_ head: String, _ continuation: [Substring]) -> String {
        ([head] + continuation.map { $0.trimmingCharacters(in: .whitespaces) })
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    private static func stripComment(_ plain: String) -> String {
        guard let comment = plain.range(of: " #") else { return plain }
        return trimEnd(plain[..<comment.lowerBound])
    }

    /// The string a leading `"…"` (escapes \n \t \r; any other escaped character stands for
    /// itself) or `'…'` (`''` for a quote) holds; nil when not quoted or never closed.
    /// Used for YAML frontmatter and TOML values alike.
    private static func quotedValue(_ text: String) -> String? {
        guard let quote = text.first, quote == "\"" || quote == "'" else { return nil }
        var result = ""
        var rest = text.dropFirst()
        while let character = rest.popFirst() {
            if character == quote {
                guard quote == "'", rest.first == "'" else { return result }
                rest.removeFirst()
                result.append("'")
            } else if quote == "\"", character == "\\", let escaped = rest.popFirst() {
                switch escaped {
                case "n": result.append("\n")
                case "t": result.append("\t")
                case "r": result.append("\r")
                default: result.append(escaped)
                }
            } else {
                result.append(character)
            }
        }
        return nil
    }

    private static func trimEnd<Text: StringProtocol>(_ text: Text) -> String {
        var result = String(text)
        while result.last?.isWhitespace == true { result.removeLast() }
        return result
    }

    // MARK: - Files

    private nonisolated struct WalkedFile {
        let url: URL
        /// Path below the source root, the last component without its extension.
        let components: [String]
    }

    /// Files ending in `.<fileExtension>` (any case) below `root`, in name order. Hidden
    /// entries are skipped and symlinked directories below the root are not entered (so
    /// there are no loops); at most `maxWalkDepth` directory levels below the root and
    /// `maxEntriesPerSource` entries visited.
    private static func walk(_ root: URL, fileExtension: String) -> [WalkedFile] {
        let suffix = "." + fileExtension.lowercased()
        var files: [WalkedFile] = []
        var budget = maxEntriesPerSource
        func visit(_ directory: URL, _ prefix: [String], depth: Int) {
            guard let names = try? FileManager.default.contentsOfDirectory(atPath: directory.path) else { return }
            for name in names.sorted() {
                guard budget > 0 else { return }
                budget -= 1
                guard !name.hasPrefix(".") else { continue }
                let url = directory.appendingPathComponent(name)
                var info = stat()
                guard lstat(url.path, &info) == 0 else { continue }
                switch info.st_mode & S_IFMT {
                case S_IFDIR:
                    if depth < maxWalkDepth { visit(url, prefix + [name], depth: depth + 1) }
                case S_IFLNK where isDirectory(url.path):
                    continue
                default:
                    guard name.lowercased().hasSuffix(suffix) else { continue }
                    files.append(WalkedFile(url: url, components: prefix + [String(name.dropLast(suffix.count))]))
                }
            }
        }
        visit(root, [], depth: 0)
        return files
    }

    /// Non-hidden subdirectories (symlinks followed) of `directory`, in name order.
    private static func childDirectories(_ directory: URL) -> [URL] {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: directory.path) else {
            return []
        }
        return names.sorted().prefix(maxEntriesPerSource)
            .filter { !$0.hasPrefix(".") }
            .map { directory.appendingPathComponent($0) }
            .filter { isDirectory($0.path) }
    }

    private static func isDirectory(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) && isDirectory.boolValue
    }

    /// Up to `limit` bytes of a regular file (a symlink is judged by its target). The file
    /// is opened non-blocking and checked with fstat, so a FIFO or device is never read.
    private static func readFile(_ url: URL, limit: Int) -> Data? {
        let fd = open(url.path, O_RDONLY | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { return nil }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { return nil }
        return try? handle.read(upToCount: limit) ?? Data()
    }

    /// The first 64 KB of a text file, decoded leniently (a cut character becomes U+FFFD).
    private static func readText(_ url: URL) -> String? {
        guard let data = readFile(url, limit: maxCommandFileBytes) else { return nil }
        let text = String(decoding: data, as: UTF8.self)
        return text.hasPrefix("\u{FEFF}") ? String(text.dropFirst()) : text
    }

    /// A JSON config file; nil when missing, invalid or larger than `maxConfigFileBytes`.
    private static func readJSON(_ url: URL) -> Any? {
        guard let data = readFile(url, limit: maxConfigFileBytes + 1),
              data.count <= maxConfigFileBytes else { return nil }
        return try? JSONSerialization.jsonObject(with: data)
    }

    /// True only for a JSON boolean true (not the number 1).
    private static func isJSONTrue(_ value: Any?) -> Bool {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else {
            return false
        }
        return number.boolValue
    }

    /// Lines split on LF, CRLF or CR.
    private static func lines(_ text: String) -> [Substring] {
        text.split(omittingEmptySubsequences: false) { $0 == "\n" || $0 == "\r\n" || $0 == "\r" }
    }
}
