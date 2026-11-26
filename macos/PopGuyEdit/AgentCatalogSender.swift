// AgentCatalogSender.swift — popguy-edit
//
// Sends the calling agent's command catalog on the open composer connection, from a
// background queue, while the main flow waits for the composer's reply. The agent comes
// from the temp file name, else from the nearest `claude` or `codex` ancestor process
// (sysctl and libproc). Codex skills come from the ancestor's own `app-server`
// (JSON-RPC lines on stdin/stdout, 3 s timeout). The job never prints, never changes the
// exit code and never delays exit: every error drops the catalog.

import Foundation

// MARK: - Shared state

/// The composer socket. Every write takes one lock, so a catalog line and the "written"
/// ack never interleave; the catalog is sent only while no reply has been read.
final class ComposerLink: @unchecked Sendable {
    private let lock = NSLock()
    private let fd: Int32
    private var replied = false

    init(fd: Int32) {
        self.fd = fd
        // A Codex child must not keep the socket open: the app reads EOF as "client gone".
        let flags = fcntl(fd, F_GETFD)
        if flags >= 0 { _ = fcntl(fd, F_SETFD, flags | FD_CLOEXEC) }
    }

    /// Writes one line; false when the connection is gone.
    func write(_ line: Data) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return writeAll(fd, line)
    }

    /// Called as soon as a reply line was read: no catalog is sent after it.
    func markReplied() {
        lock.lock()
        replied = true
        lock.unlock()
    }

    /// Writes the catalog line unless a reply already arrived.
    func writeCatalog(_ line: Data) {
        lock.lock()
        defer { lock.unlock() }
        guard !replied else { return }
        _ = writeAll(fd, line)
    }
}

/// The running `codex app-server` child, if any. `stopAll` (every exit path) kills it and
/// keeps a later one from starting.
final class ChildProcessRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var stopped = false

    /// Launches `process` unless the CLI is exiting; false when it was not started.
    func launch(_ process: Process) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !stopped else { return false }
        do {
            try process.run()
        } catch {
            return false
        }
        self.process = process
        return true
    }

    /// Stops the child started by `launch`, if it still runs.
    func stop(_ process: Process) {
        lock.lock()
        let isCurrent = self.process === process
        if isCurrent { self.process = nil }
        lock.unlock()
        if isCurrent { Self.kill(process) }
    }

    /// Stops the current child and refuses new ones. Called before every exit.
    func stopAll() {
        lock.lock()
        stopped = true
        let current = process
        process = nil
        lock.unlock()
        if let current { Self.kill(current) }
    }

    /// SIGTERM, then SIGKILL when the child still runs after up to 100 ms.
    private static func kill(_ process: Process) {
        guard process.isRunning else { return }
        process.terminate()
        for _ in 0..<10 where process.isRunning {
            usleep(10_000)
        }
        if process.isRunning {
            Darwin.kill(process.processIdentifier, SIGKILL)
        }
    }
}

let codexChildren = ChildProcessRegistry()

// MARK: - Job

/// Starts the catalog job for the session on `link`. Returns at once.
func startCatalogJob(link: ComposerLink, filePath: String) {
    DispatchQueue.global(qos: .utility).async {
        guard let catalog = buildCatalog(filePath: filePath), !catalog.items.isEmpty,
              let line = try? ComposerMessage.encodeLine(.catalog(agent: catalog.agent, items: catalog.items))
        else { return }
        link.writeCatalog(line)
    }
}

private func buildCatalog(filePath: String) -> (agent: String, items: [ComposerCatalogItem])? {
    let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
    let home = FileManager.default.homeDirectoryForCurrentUser
    let agent: String
    var codexPath: String?
    if let named = ComposerCatalog.detectAgent(filePath: filePath) {
        agent = named
    } else if let ancestor = agentAncestor() {
        agent = ancestor.agent
        codexPath = ancestor.agent == "codex" ? ancestor.path : nil
    } else {
        return nil
    }

    switch agent {
    case "claude":
        let configured = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"] ?? ""
        let configDir = configured.isEmpty
            ? home.appendingPathComponent(".claude", isDirectory: true)
            : URL(fileURLWithPath: configured, isDirectory: true)
        return (agent, ComposerCatalog.claudeItems(configDir: configDir, cwd: cwd))
    case "gemini":
        let geminiDir = home.appendingPathComponent(".gemini", isDirectory: true)
        return (agent, ComposerCatalog.geminiItems(geminiDir: geminiDir, cwd: cwd))
    case "codex":
        guard let codexPath else { return nil }
        return (agent, codexSkills(executable: codexPath, cwd: cwd))
    default:
        return nil
    }
}

// MARK: - Process ancestry

/// The nearest ancestor (at most 8 levels up) whose executable file is named `claude` or
/// `codex`, with that executable's path.
private func agentAncestor() -> (agent: String, path: String)? {
    var pid = getppid()
    for _ in 0..<8 {
        guard pid > 1 else { return nil }
        if let path = executablePath(of: pid) {
            switch URL(fileURLWithPath: path).lastPathComponent {
            case "claude": return ("claude", path)
            case "codex": return ("codex", path)
            default: break
            }
        }
        guard let parent = parentPID(of: pid) else { return nil }
        pid = parent
    }
    return nil
}

private func parentPID(of pid: pid_t) -> pid_t? {
    var info = kinfo_proc()
    var size = MemoryLayout<kinfo_proc>.stride
    var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
    guard sysctl(&mib, u_int(mib.count), &info, &size, nil, 0) == 0, size > 0 else { return nil }
    return info.kp_eproc.e_ppid
}

private func executablePath(of pid: pid_t) -> String? {
    // PROC_PIDPATHINFO_MAXSIZE (4 * MAXPATHLEN).
    var buffer = [UInt8](repeating: 0, count: 4 * Int(MAXPATHLEN))
    let length = proc_pidpath(pid, &buffer, UInt32(buffer.count))
    guard length > 0 else { return nil }
    return String(decoding: buffer.prefix(Int(length)), as: UTF8.self)
}

// MARK: - Codex

private let codexTimeout: TimeInterval = 3
private let codexReplyCap = 16 * 1024 * 1024

/// Codex skills from `<executable> app-server`: initialize, initialized, then
/// `skills/list` for `cwd`; the reply with id 2 is parsed. Empty on any failure or after
/// 3 s. The child is stopped before returning.
private func codexSkills(executable: String, cwd: URL) -> [ComposerCatalogItem] {
    let process = Process()
    let input = Pipe()
    let output = Pipe()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = ["app-server"]
    process.currentDirectoryURL = cwd
    process.standardInput = input
    process.standardOutput = output
    process.standardError = FileHandle.nullDevice
    guard codexChildren.launch(process) else { return [] }
    defer {
        codexChildren.stop(process)
        try? input.fileHandleForWriting.close()
        try? output.fileHandleForReading.close()
    }
    let deadline = Date().addingTimeInterval(codexTimeout)

    let requests: [[String: Any]] = [
        ["jsonrpc": "2.0", "id": 1, "method": "initialize",
         "params": ["clientInfo": ["name": "popguy-edit", "version": "1"]]],
        ["jsonrpc": "2.0", "method": "initialized"],
        ["jsonrpc": "2.0", "id": 2, "method": "skills/list", "params": ["cwds": [cwd.path]]],
    ]
    var payload = Data()
    for request in requests {
        guard let json = try? JSONSerialization.data(withJSONObject: request) else { return [] }
        payload.append(json)
        payload.append(0x0A)
    }
    guard writeAll(input.fileHandleForWriting.fileDescriptor, payload) else { return [] }

    guard let reply = readLine(withID: 2, from: output.fileHandleForReading.fileDescriptor, deadline: deadline)
    else { return [] }
    return ComposerCatalog.parseSkillsListReply(reply)
}

/// Reads "\n"-framed JSON-RPC lines until one has `"id": <id>`; nil on EOF, error,
/// the size cap or the deadline.
private func readLine(withID id: Int, from fd: Int32, deadline: Date) -> Data? {
    var buffer = Data()
    var chunk = [UInt8](repeating: 0, count: 64 * 1024)
    while true {
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = Data(buffer[buffer.startIndex..<newline])
            buffer.removeSubrange(buffer.startIndex...newline)
            if let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
               let lineID = object["id"] as? Int, lineID == id {
                return line
            }
        }
        if buffer.count > codexReplyCap { return nil }

        let remaining = deadline.timeIntervalSinceNow
        guard remaining > 0 else { return nil }
        var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
        let ready = poll(&descriptor, 1, Int32(max(1, (remaining * 1000).rounded(.up))))
        if ready < 0 {
            if errno == EINTR { continue }
            return nil
        }
        if ready == 0 { return nil }
        let count = chunk.withUnsafeMutableBytes { read(fd, $0.baseAddress, $0.count) }
        if count < 0 {
            if errno == EINTR || errno == EAGAIN { continue }
            return nil
        }
        if count == 0 { return nil }
        buffer.append(contentsOf: chunk[0..<count])
    }
}
