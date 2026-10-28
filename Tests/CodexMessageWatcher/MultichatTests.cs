using System.Collections.Concurrent;
using ShaolanPet;

static partial class WatcherTests
{
    static void MultiChatRegression(string root)
    {
        const string secondId = "00000000-0000-0000-0000-000000000002";
        string home = Path.Combine(root, "multi-case"), sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string first = Path.Combine(sessions, "one.jsonl"), second = Path.Combine(sessions, "two.jsonl"), config = Path.Combine(home, "source.json");
        File.WriteAllText(first, Header() + "\n" + Turn() + "\n" + Message("old") + "\n");
        File.WriteAllText(second, Header(thread: secondId) + "\n" + Turn() + "\n" + Message("old") + "\n");
        void Save() => File.WriteAllText(config, Json(new { chats = new[] { new { threadId = ThreadId, rolloutPath = first }, new { threadId = secondId, rolloutPath = second } } }));
        void Add(string file, params string[] rows) => File.AppendAllText(file, string.Join("\n", rows) + "\n");
        Save();
        var events = new ConcurrentQueue<string>();
        using var watcher = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1));
        watcher.PollOnce();
        Check(events.Count == 0, "two startup histories skipped");
        Check(watcher.SelectedChatCount == 2 && watcher.WatchingChatCount == 2 && watcher.Status == "watching-selected-chats", "two healthy selections reported");
        Add(first, Turn(), Message("same-id")); Add(second, Turn(), Message("same-id")); watcher.PollOnce();
        Check(events.Count == 2 && events.Distinct().Count() == 2, "same message ID in two chats emits once per chat");
        Add(first, Message("same-id")); Add(second, Message("same-id")); watcher.PollOnce();
        Check(events.Count == 2, "duplicates suppressed independently");
        Add(first, Turn("queued", "queue"), Message("cross-queue", "queued"));
        Add(second, Turn("queued", "queue"), Message("cross-queue", "queued")); watcher.PollOnce();
        Check(events.Count == 4, "cross-chat inputs reach shared callback independently");
        Add(first, Turn("auto", "automation_heartbeat_scheduled"), Message("automatic", "auto"));
        Add(second, Turn("auto", "app_tool_send_message"), Message("delegation", "auto"), Message("heartbeat", "queued", kind: "user.heartbeat")); watcher.PollOnce();
        Check(events.Count == 4, "automatic messages rejected in both chats");
        File.Move(second, second + ".old"); File.WriteAllText(second, ""); watcher.PollOnce();
        Check(watcher.SelectedChatCount == 2 && watcher.WatchingChatCount == 1 && watcher.Status == "partially-watching-selected-chats", "pending source exposes partial status");
        Add(first, Message("other-source-pending", "queued")); watcher.PollOnce();
        Check(events.Count == 5, "pending header in one chat does not block the other");
        Add(second, Header(thread: secondId), Turn(), Message("replacement-history")); watcher.PollOnce();
        Check(events.Count == 5, "replacement source history skipped independently");
        Add(second, Turn(), Message("after-replacement")); watcher.PollOnce();
        Check(events.Count == 6, "replaced source recovers without losing healthy source");
        File.Move(second, second + ".hidden"); watcher.PollOnce();
        Add(first, Message("other-source-missing", "queued")); watcher.PollOnce();
        Check(events.Count == 7, "missing source does not block healthy source");
        File.Move(second + ".hidden", second); watcher.PollOnce();
        File.WriteAllText(second, Header("future-version", secondId) + "\n"); watcher.PollOnce();
        Add(first, Message("other-source-bad-header", "queued")); Add(second, Turn(), Message("unverified-version")); watcher.PollOnce();
        Check(events.Count == 8, "unsupported source remains isolated");
        watcher.Dispose(); Add(first, Message("after-dispose", "queued")); watcher.PollOnce();
        Check(events.Count == 8, "Dispose cancels all selected sources");
        Check(watcher.WatchingChatCount == 0 && watcher.Status == "stopped", "Dispose clears active source count");
        File.WriteAllText(second, Header(thread: secondId) + "\n" + Turn() + "\n" + Message("reconfigured-history") + "\n"); Save();
        using var restarted = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1)); restarted.PollOnce();
        Check(events.Count == 8, "config save and restart skip accumulated histories");
        Add(first, Turn(), Message("after-restart")); Add(second, Turn(), Message("after-restart")); restarted.PollOnce();
        Check(events.Count == 10, "both old and new selections remain live after restart");
        Add(first, Turn("first-only"), Message("first-only-message", "first-only"));
        Add(second, Message("undeclared-second-turn", "first-only")); restarted.PollOnce();
        Check(events.Count == 11, "turn attribution cannot cross chat boundary");
        string partial = Message("partial-first");
        File.AppendAllText(first, partial[..20]); restarted.PollOnce();
        Add(second, Message("whole-second")); restarted.PollOnce();
        Check(events.Count == 12, "one partial record does not block another chat");
        File.AppendAllText(first, partial[20..] + "\n"); restarted.PollOnce();
        Check(events.Count == 13, "partial buffers stay independent");

        // 有效条目继续工作；重复、无效条目不创建额外读取者。
        File.WriteAllText(config, Json(new { chats = new object[] { new { threadId = ThreadId, rolloutPath = first }, new { threadId = secondId, rolloutPath = second }, new { threadId = ThreadId.ToUpperInvariant(), rolloutPath = second }, new { threadId = "not-a-guid" }, "invalid-entry" } }));
        using var mixed = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1)); mixed.PollOnce();
        Check(mixed.SelectedChatCount == 2 && mixed.WatchingChatCount == 2 && mixed.Status == "partially-watching-selected-chats", "invalid and duplicate selections give bounded warning");
        Add(first, Turn(), Message("mixed-one")); Add(second, Turn(), Message("mixed-two")); mixed.PollOnce();
        Check(events.Count == 15, "invalid entries cannot disable valid entries or duplicate callbacks");
        File.WriteAllText(config, Json(new { chats = "invalid", threadId = ThreadId, rolloutPath = first }));
        using var wrongShape = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1)); wrongShape.PollOnce();
        Check(wrongShape.SelectedChatCount == 0 && wrongShape.Status == "invalid-source-selection", "malformed list cannot fall back to hidden legacy selection");
        File.WriteAllText(config, Json(new { chats = Array.Empty<object>() }));
        using var empty = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1)); empty.PollOnce();
        Check(empty.SelectedChatCount == 0 && empty.Status == "no-selected-chats", "empty list explicitly disables sources");

        string late = Path.Combine(sessions, "late.jsonl");
        File.WriteAllText(config, Json(new { chats = new[] { new { threadId = ThreadId, rolloutPath = first }, new { threadId = secondId, rolloutPath = late } } }));
        using var initialMissing = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1)); initialMissing.PollOnce();
        Add(first, Turn(), Message("initially-missing-other")); initialMissing.PollOnce();
        Check(initialMissing.SelectedChatCount == 2 && initialMissing.WatchingChatCount == 1 && events.Count == 16, "initially missing path cannot block valid source");
        File.WriteAllText(late, Header(thread: secondId) + "\n" + Turn() + "\n" + Message("late-history") + "\n"); initialMissing.PollOnce();
        Check(initialMissing.WatchingChatCount == 2 && events.Count == 16, "late source establishes its own EOF baseline");
        Add(late, Turn(), Message("late-live")); initialMissing.PollOnce();
        Check(events.Count == 17, "late source accepts only future input");

        File.WriteAllText(config, Json(new { chats = Enumerable.Range(1, 9).Select(i => new { threadId = $"00000000-0000-0000-0000-{i:D12}", rolloutPath = Path.Combine(sessions, "unused-" + i + ".jsonl") }).ToArray() }));
        using var bounded = new CodexMessageWatcher(events.Enqueue, home, config, () => Now.AddSeconds(-1)); bounded.PollOnce();
        Check(bounded.SelectedChatCount == 8 && bounded.WatchingChatCount == 0, "selection list has a fixed eight-source bound");
    }
}
