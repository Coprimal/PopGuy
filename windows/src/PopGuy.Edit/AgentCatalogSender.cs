// The "/" or "$" catalog that popguy-edit.exe sends after "open": it finds the calling agent (temp file name, else the
// nearest claude.exe or codex.exe ancestor process), builds its items with ComposerCatalog, and writes one "catalog"
// line unless the app has already replied. Best effort and silent: any failure just sends nothing, never changes the
// exit code and never delays the exit. Same rules as macos/PopGuyEdit/main.swift.

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PopGuy.Core.Composer;

namespace PopGuy.Edit;

internal static partial class Program
{
    /// <summary>Ancestors looked at, nearest first.</summary>
    private const int MaxAncestorLevels = 8;
    /// <summary>Whole codex app-server exchange: start, requests and the skills/list reply.</summary>
    private static readonly TimeSpan CodexTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Longest codex stdout line read; a longer one ends the read with no catalog.</summary>
    private const int MaxCodexLineBytes = 16 * 1024 * 1024;

    /// <summary>Guards <see cref="_codexChild"/>'s start and <see cref="_exiting"/>.</summary>
    private static readonly Lock ChildLock = new();
    private static Process? _codexChild;
    private static bool _exiting;

    /// <summary>Fire and forget: the task swallows every exception and nobody waits for it.</summary>
    [SupportedOSPlatform("windows")]
    private static void StartCatalogSender(Stream pipe, string path)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (BuildCatalogLine(path) is { } line)
                    WriteCatalogIfNoReply(pipe, line);
            }
            catch (Exception)
            {
                // Best effort: no catalog.
            }
        });
    }

    /// <summary>The encoded catalog line, or null when there is no agent, no item, or the catalog is invalid.</summary>
    [SupportedOSPlatform("windows")]
    private static byte[]? BuildCatalogLine(string path)
    {
        var cwd = Environment.CurrentDirectory;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string agent;
        IReadOnlyList<CatalogItem> items;
        string? codexPath = null;
        if (ComposerCatalog.DetectAgentFromFile(path) is { } fromFile)
            agent = fromFile;
        else if (AgentAncestor() is { } ancestor)
            (agent, codexPath) = ancestor;
        else
            return null;

        switch (agent)
        {
            case "claude":
                // The global config (.claude.json, which names the signed-in account) sits in CLAUDE_CONFIG_DIR, else
                // in the profile folder beside .claude.
                var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                string globalConfig;
                if (string.IsNullOrEmpty(configDir))
                {
                    if (home.Length == 0)
                        return null;
                    configDir = Path.Combine(home, ".claude");
                    globalConfig = Path.Combine(home, ".claude.json");
                }
                else
                {
                    globalConfig = Path.Combine(configDir, ".claude.json");
                }
                items = ComposerCatalog.ClaudeItems(configDir, cwd, globalConfig);
                break;
            case "gemini":
                if (home.Length == 0)
                    return null;
                items = ComposerCatalog.GeminiItems(Path.Combine(home, ".gemini"), cwd);
                break;
            case "codex" when codexPath is not null:
                items = CodexItems(codexPath, cwd);
                break;
            default:
                return null;
        }
        if (items.Count == 0)
            return null;
        try
        {
            return ComposerMessage.EncodeLine(new ComposerMessage.Catalog(agent, items));
        }
        catch (ComposerProtocolException)
        {
            return null;
        }
    }

    // Codex: its own app-server answers skills/list.

    /// <summary>Starts <c>&lt;codex&gt; app-server</c>, sends initialize, initialized and skills/list, and parses the
    /// reply to id 2. Empty on timeout or any failure; the child is always killed before returning.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<CatalogItem> CodexItems(string codexPath, string cwd)
    {
        var clock = Stopwatch.StartNew();
        var child = StartCodexChild(codexPath, cwd);
        if (child is null)
            return [];
        try
        {
            // Drained and dropped, so a chatty stderr can never fill its pipe and block the child.
            child.ErrorDataReceived += static (_, _) => { };
            child.BeginErrorReadLine();

            // Raw UTF-8 bytes: the default stdin encoding is the console code page, and a writer could add a BOM.
            var stdin = child.StandardInput.BaseStream;
            stdin.Write(JsonLine(static w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WriteNumber("id", 1);
                w.WriteString("method", "initialize");
                w.WriteStartObject("params");
                w.WriteStartObject("clientInfo");
                w.WriteString("name", "popguy-edit");
                w.WriteString("version", "1");
                w.WriteEndObject();
                w.WriteEndObject();
            }));
            stdin.Write(JsonLine(static w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WriteString("method", "initialized");
            }));
            stdin.Write(JsonLine(w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WriteNumber("id", 2);
                w.WriteString("method", "skills/list");
                w.WriteStartObject("params");
                w.WriteStartArray("cwds");
                w.WriteStringValue(cwd);
                w.WriteEndArray();
                w.WriteEndObject();
            }));
            stdin.Flush();

            // The redirected stdout is synchronous: the read runs on its own task, and killing the child (finally)
            // is what ends it after a timeout.
            var stdout = child.StandardOutput.BaseStream;
            var read = Task.Run(() => ReadSkillsListReply(stdout));
            var remaining = CodexTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || !read.Wait(remaining))
                return [];
            return read.Result is { } reply ? ComposerCatalog.ParseSkillsListReply(reply) : [];
        }
        finally
        {
            KillCodexChild();
        }
    }

    /// <summary>The first stdout line that answers request 2 (numeric id 2, no method), or null at end of output or
    /// at a line over <see cref="MaxCodexLineBytes"/>. Notifications, server requests and non-JSON lines are skipped.</summary>
    private static string? ReadSkillsListReply(Stream stdout)
    {
        var line = new ArrayBufferWriter<byte>();
        var chunk = new byte[64 * 1024];
        int count;
        while ((count = stdout.Read(chunk)) > 0)
        {
            var rest = chunk.AsSpan(0, count);
            for (var end = rest.IndexOf((byte)'\n'); end >= 0; end = rest.IndexOf((byte)'\n'))
            {
                if (line.WrittenCount + end > MaxCodexLineBytes)
                    return null;
                line.Write(rest[..end]);
                if (SkillsListReply(line.WrittenMemory) is { } reply)
                    return reply;
                line.ResetWrittenCount();
                rest = rest[(end + 1)..];
            }
            if (line.WrittenCount + rest.Length > MaxCodexLineBytes)
                return null;
            line.Write(rest);
        }
        // The last line may end without "\n".
        return SkillsListReply(line.WrittenMemory);
    }

    /// <summary>The line as text when it is the reply to request 2, else null.</summary>
    private static string? SkillsListReply(ReadOnlyMemory<byte> line)
    {
        if (line.Span.EndsWith("\r"u8))
            line = line[..^1];
        if (line.IsEmpty)
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && !root.TryGetProperty("method", out _)
                && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value == 2)
                return Encoding.UTF8.GetString(line.Span);
        }
        catch (JsonException)
        {
            // Not a JSON-RPC line.
        }
        return null;
    }

    /// <summary>One JSON object written by <paramref name="write"/>, followed by "\n".</summary>
    private static byte[] JsonLine(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Starts the child and registers it for <see cref="StopCodexChild"/>; null once the CLI is exiting or
    /// when the start fails.</summary>
    [SupportedOSPlatform("windows")]
    private static Process? StartCodexChild(string codexPath, string cwd)
    {
        var info = new ProcessStartInfo(codexPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add("app-server");

        lock (ChildLock)
        {
            // Started under the lock, so the exit path either sees the child or stops it from starting.
            if (_exiting)
                return null;
            var child = new Process { StartInfo = info };
            try
            {
                if (!child.Start())
                {
                    child.Dispose();
                    return null;
                }
            }
            catch (Exception)
            {
                child.Dispose();
                return null;
            }
            _codexChild = child;
            return child;
        }
    }

    /// <summary>Exit path: no child starts after this, and a running one is killed.</summary>
    private static void StopCodexChild()
    {
        lock (ChildLock)
            _exiting = true;
        KillCodexChild();
    }

    /// <summary>Kills and disposes the registered child; whoever takes it from the field owns it.</summary>
    private static void KillCodexChild()
    {
        if (Interlocked.Exchange(ref _codexChild, null) is not { } child)
            return;
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
        child.Dispose();
    }

    // Ancestry: Toolhelp32 for parent PIDs, the process's image path and creation time to guard against PID reuse.

    /// <summary>The nearest claude.exe or codex.exe ancestor (≤ <see cref="MaxAncestorLevels"/> levels) with its full
    /// path. The walk stops at a process it cannot open and at a "parent" created after its child (a reused PID).</summary>
    [SupportedOSPlatform("windows")]
    private static (string Agent, string Path)? AgentAncestor()
    {
        if (ParentProcessIds() is not { } parents)
            return null;
        var pid = (uint)Environment.ProcessId;
        if (QueryProcess(pid) is not { } self)
            return null;
        var childCreated = self.Created;
        for (var level = 0; level < MaxAncestorLevels; level++)
        {
            if (!parents.TryGetValue(pid, out var parent) || parent == 0 || parent == pid)
                return null;
            if (QueryProcess(parent) is not { } info || info.Created > childCreated)
                return null;
            if (info.Path is { } imagePath)
            {
                var name = Path.GetFileName(imagePath);
                if (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
                    return ("claude", imagePath);
                if (name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
                    return ("codex", imagePath);
            }
            pid = parent;
            childCreated = info.Created;
        }
        return null;
    }

    /// <summary>Process ID → parent process ID for every process, or null when the snapshot fails.</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe Dictionary<uint, uint>? ParentProcessIds()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
            return null;
        try
        {
            var parents = new Dictionary<uint, uint>();
            var entry = new ProcessEntry32W { Size = (uint)sizeof(ProcessEntry32W) };
            for (var more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
                parents[entry.ProcessId] = entry.ParentProcessId;
            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>A process's creation time (FILETIME ticks) and image path (null if unreadable); null when the process
    /// cannot be opened or its times cannot be read.</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe (long Created, string? Path)? QueryProcess(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
            return null;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                return null;
            var buffer = new char[32_768];
            var size = (uint)buffer.Length;
            string? path = null;
            fixed (char* chars = buffer)
            {
                if (QueryFullProcessImageNameW(handle, 0, chars, &size) && size > 0)
                    path = new string(chars, 0, (int)size);
            }
            return (created, path);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly nint InvalidHandleValue = -1;

    /// <summary>PROCESSENTRY32W, blittable: passed by pointer; the name buffer is never read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed ushort ExeFile[260];
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32FirstW(nint snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32NextW(nint snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* exeName, uint* size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
