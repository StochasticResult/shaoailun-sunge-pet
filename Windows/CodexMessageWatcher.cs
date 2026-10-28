using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ShaolanPet;

// 配置只在启动时读取；每个聊天独立处理，单个坏源不能阻塞其他源。
sealed class CodexMessageWatcher : IDisposable
{
    private const int MaxChats = 8;
    private readonly Action<string> onHumanMessage;
    private readonly string codexHome, configPath;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<CodexChatSource> sources = new();
    private bool configured, selectionWarnings, started, disposed;

    public string Status { get; private set; } = "not-started";
    public int SelectedChatCount { get { lock (gate) return sources.Count; } }
    public int WatchingChatCount { get { lock (gate) return sources.Count(s => s.Status == "watching-selected-chat"); } }

    public CodexMessageWatcher(Action<string> onHumanMessage)
        : this(onHumanMessage,
            Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaolanPet", "codex-reaction-source.json"),
            () => DateTimeOffset.UtcNow) { }

    internal CodexMessageWatcher(Action<string> onHumanMessage, string codexHome, string configPath, Func<DateTimeOffset> clock)
    {
        this.onHumanMessage = onHumanMessage;
        this.codexHome = codexHome;
        this.configPath = configPath;
        this.clock = clock;
    }

    public void Start()
    {
        lock (gate)
        {
            if (started || disposed) return;
            started = true;
            var token = cancellation.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    PollOnce();
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }
    }

    internal void PollOnce()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!configured)
            {
                configured = true;
                try { LoadSources(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
                { Status = "invalid-source-selection"; }
            }
            foreach (var source in sources) source.PollOnce();
            if (sources.Count == 0) return;
            int watching = sources.Count(s => s.Status == "watching-selected-chat");
            Status = sources.Count == 1 && !selectionWarnings ? sources[0].Status : watching == 0 ? "selected-chats-unavailable" :
                watching == sources.Count && !selectionWarnings ? "watching-selected-chats" : "partially-watching-selected-chats";
        }
    }

    private void LoadSources()
    {
        if (!File.Exists(configPath)) { Status = "source-not-configured"; return; }
        if (new FileInfo(configPath).Length > 16 * 1024) { Status = "invalid-source-selection"; return; }
        using var config = JsonDocument.Parse(File.ReadAllBytes(configPath));
        var root = config.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { Status = "invalid-source-selection"; return; }
        var selected = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("threadId", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var guid))
            { selectionWarnings = true; return; }
            string threadId = guid.ToString("D");
            if (item.TryGetProperty("rolloutPath", out var path) && path.ValueKind != JsonValueKind.String)
            { selectionWarnings = true; return; }
            if (!selected.Add(threadId)) { selectionWarnings = true; return; }
            sources.Add(new CodexChatSource(onHumanMessage, codexHome, threadId, path.ValueKind == JsonValueKind.String ? path.GetString() : null, clock));
        }
        if (root.TryGetProperty("chats", out var chats))
        {
            if (chats.ValueKind != JsonValueKind.Array) { Status = "invalid-source-selection"; return; }
            int count = chats.GetArrayLength();
            selectionWarnings = count > MaxChats;
            for (int i = 0; i < Math.Min(count, MaxChats); i++) Add(chats[i]);
            Status = sources.Count == 0 ? selectionWarnings ? "invalid-source-selection" : "no-selected-chats" : "not-started";
        }
        else
        {
            Add(root);
            if (sources.Count == 0) Status = "invalid-source-selection";
        }
    }

    // 保留单源安全回归入口；实际每个源仍逐次核验其打开句柄。
    internal bool IsSafeOpenedSource(FileStream stream)
    { lock (gate) return sources.Count == 1 && sources[0].IsSafeOpenedSource(stream); }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel(); cancellation.Dispose();
            foreach (var source in sources) source.Dispose();
            Status = "stopped";
        }
    }
}

// 只观察本机已选聊天的结构化元数据，不读取消息正文或调用 Codex。
sealed class CodexChatSource : IDisposable
{
    private const int MaxLine = 4 * 1024 * 1024;
    private const string SupportedVersion = "0.165.0-alpha.1.2";
    private readonly Action<string> onHumanMessage;
    private readonly string sessionsRoot, selectedThreadId;
    private readonly string? selectedRolloutPath;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly MemoryStream pending = new();
    private readonly Dictionary<string, bool> turns = new(StringComparer.Ordinal);
    private readonly Queue<string> turnOrder = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private string? threadId, rolloutPath, fileIdentity;
    private long position;
    private bool disposed, configured, validHeader, skippingLine, awaitingHeader;
    private long headerLengthAtCheck = -1;
    private DateTimeOffset startedAt;

    public string Status { get; private set; } = "not-started";

    internal CodexChatSource(Action<string> onHumanMessage, string codexHome, string threadId, string? rolloutPath, Func<DateTimeOffset> clock)
    {
        this.onHumanMessage = onHumanMessage;
        selectedThreadId = threadId;
        selectedRolloutPath = rolloutPath;
        this.clock = clock;
        try { sessionsRoot = Path.GetFullPath(Path.Combine(codexHome, "sessions")); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { sessionsRoot = ""; }
    }

    internal void PollOnce()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                if (!configured)
                {
                    configured = true;
                    startedAt = clock();
                    LoadSource();
                }
                if (rolloutPath == null) return;
                if (!SourceHasNoLinks(rolloutPath)) { RejectLinkedSource(); return; }
                using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!IsSafeOpenedSource(stream)) { RejectLinkedSource(); return; }
                string identity = Identity(stream);
                if (identity != fileIdentity || stream.Length < position)
                {
                    // 新文件/截断只设当前水位，不重放旧记录或沿用旧回合来源。
                    fileIdentity = identity;
                    ResetAtEnd(stream);
                    validHeader = false;
                    awaitingHeader = true;
                    headerLengthAtCheck = -1;
                }
                if (awaitingHeader)
                {
                    if (stream.Length == headerLengthAtCheck) return;
                    var state = ValidateHeader(stream);
                    headerLengthAtCheck = stream.Length;
                    awaitingHeader = state == HeaderState.Pending;
                    validHeader = state == HeaderState.Supported;
                    // 补齐头时重新设水位；补齐之前的记录属于历史，不触发。
                    ResetAtEnd(stream);
                    Status = awaitingHeader ? "waiting-for-session-header" : validHeader ? "watching-selected-chat" : "unsupported-session-metadata";
                    return;
                }
                if (!validHeader) return;
                stream.Position = position;
                byte[] buffer = new byte[64 * 1024];
                int budget = 1024 * 1024;
                while (budget > 0)
                {
                    int count = stream.Read(buffer, 0, Math.Min(buffer.Length, budget));
                    if (count == 0) break;
                    position += count; budget -= count;
                    Consume(buffer.AsSpan(0, count));
                }
                Status = "watching-selected-chat";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 不记录异常消息，避免路径或会话内容进入日志。
                Status = "source-unavailable";
            }
        }
    }

    private void LoadSource()
    {
        if (sessionsRoot.Length == 0) { Status = "invalid-sessions-root"; return; }
        threadId = selectedThreadId;
        string? selected = selectedRolloutPath;
        if (selected == null && Directory.Exists(sessionsRoot))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var matches = Directory.EnumerateFiles(sessionsRoot, "*" + threadId + ".jsonl", options).Take(2).ToArray();
            if (matches.Length == 1) selected = matches[0];
        }
        if (selected == null || !Path.IsPathFullyQualified(selected)) { Status = "source-not-found"; return; }
        string full = Path.GetFullPath(selected);
        if (!full.StartsWith(sessionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        { Status = "invalid-source-selection"; return; }
        rolloutPath = full;
    }

    private bool SourceHasNoLinks(string file)
    {
        for (string? part = file; part != null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(part, sessionsRoot, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal bool IsSafeOpenedSource(FileStream stream)
    {
        // 先核验句柄最终路径，再读取字节；路径在检查与打开之间变化也不能越界。
        var path = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, path, (uint)path.Capacity, 0);
        if (length == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity)
        {
            if (length > 32767) return false;
            path.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(stream.SafeFileHandle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity) return false;
        }
        string resolved = path.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        resolved = Path.GetFullPath(resolved);
        return string.Equals(resolved, rolloutPath, StringComparison.OrdinalIgnoreCase) &&
            resolved.StartsWith(sessionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && SourceHasNoLinks(rolloutPath!);
    }

    private void RejectLinkedSource()
    {
        fileIdentity = null; validHeader = false; awaitingHeader = false;
        pending.SetLength(0); turns.Clear(); turnOrder.Clear();
        Status = "linked-source-rejected";
    }

    private void ResetAtEnd(FileStream stream)
    {
        position = stream.Length;
        pending.SetLength(0);
        skippingLine = position > 0 && ReadLastByte(stream, position) != (byte)'\n';
        turns.Clear(); turnOrder.Clear();
    }

    private static byte ReadLastByte(FileStream stream, long length)
    { stream.Position = length - 1; return (byte)stream.ReadByte(); }

    private enum HeaderState { Pending, Supported, Unsupported }

    private HeaderState ValidateHeader(FileStream stream)
    {
        stream.Position = 0;
        using var header = new MemoryStream();
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
        {
            if (header.Length == MaxLine) return HeaderState.Unsupported;
            header.WriteByte((byte)b);
        }
        if (b != '\n') return HeaderState.Pending;
        try
        {
            using var doc = JsonDocument.Parse(header.ToArray());
            var root = doc.RootElement;
            if (Text(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload)) return HeaderState.Unsupported;
            return Text(payload, "id") == threadId && Text(payload, "originator") == "Codex Desktop" && Text(payload, "cli_version") == SupportedVersion ? HeaderState.Supported : HeaderState.Unsupported;
        }
        catch (JsonException) { return HeaderState.Unsupported; }
    }

    private void Consume(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int newline = bytes.IndexOf((byte)'\n');
            var segment = newline < 0 ? bytes : bytes[..newline];
            if (!skippingLine)
            {
                if (pending.Length + segment.Length > MaxLine) { skippingLine = true; pending.SetLength(0); }
                else pending.Write(segment);
            }
            if (newline < 0) return;
            if (!skippingLine && pending.Length > 0)
            {
                try { ProcessRecord(pending.GetBuffer().AsMemory(0, (int)pending.Length)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException) { }
            }
            pending.SetLength(0); skippingLine = false;
            bytes = bytes[(newline + 1)..];
        }
    }

    private void ProcessRecord(ReadOnlyMemory<byte> line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
        string? recordType = Text(root, "type");
        if (recordType == "event_msg" && Text(payload, "type") == "task_started")
        {
            string? id = Text(payload, "turn_id");
            if (!ValidId(id)) return;
            bool eligible = payload.TryGetProperty("turn_attribution", out var a) && a.ValueKind == JsonValueKind.Object && Text(a, "turn_id") == id &&
                Text(a, "turn_trigger") is "composer" or "queue" && ExplicitNull(a, "initiating_agent_path") && ExplicitNull(a, "parent_turn_id");
            if (!turns.ContainsKey(id!)) turnOrder.Enqueue(id!);
            turns[id!] = eligible;
            while (turnOrder.Count > 128) turns.Remove(turnOrder.Dequeue());
            return;
        }
        if (recordType != "response_item" || Text(payload, "type") != "message" || Text(payload, "role") != "user") return;
        string? messageId = Text(payload, "id");
        if (!ValidId(messageId) || !payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var meta) || meta.ValueKind != JsonValueKind.Object) return;
        string? turnId = Text(meta, "turn_id");
        if (turnId == null || !turns.TryGetValue(turnId, out bool humanTurn) || !humanTurn) return;
        if (!meta.TryGetProperty("content_item_kinds", out var kinds) || kinds.ValueKind != JsonValueKind.Array || kinds.GetArrayLength() == 0 ||
            !meta.TryGetProperty("content_item_metadata", out var origins) || origins.ValueKind != JsonValueKind.Array || origins.GetArrayLength() != kinds.GetArrayLength()) return;
        for (int i = 0; i < kinds.GetArrayLength(); i++)
        {
            if (kinds[i].ValueKind != JsonValueKind.String || kinds[i].GetString() is not ("user.text" or "user.image") || origins[i].ValueKind != JsonValueKind.Object ||
                !origins[i].TryGetProperty("provenance", out var provenance) || Text(provenance, "type") != "user") return;
        }
        if (!meta.TryGetProperty("create_time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out double seconds) || !double.IsFinite(seconds)) return;
        double start = startedAt.ToUnixTimeMilliseconds() / 1000.0, now = clock().ToUnixTimeMilliseconds() / 1000.0;
        if (seconds < start || seconds > now + 30 || !DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp) || stamp < startedAt || stamp > clock().AddSeconds(30)) return;
        string eventId = "codex-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(threadId + "\n" + messageId)));
        if (!seen.Add(eventId)) return;
        seenOrder.Enqueue(eventId);
        while (seenOrder.Count > 512) seen.Remove(seenOrder.Dequeue());
        try { onHumanMessage(eventId); }
        catch { Status = "callback-failed"; }
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool ExplicitNull(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Null;
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256;

    private static string Identity(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}:{info.FileIndexLow:X8}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending.Dispose();
            turns.Clear(); turnOrder.Clear(); seen.Clear(); seenOrder.Clear();
            Status = "stopped";
        }
    }
}
