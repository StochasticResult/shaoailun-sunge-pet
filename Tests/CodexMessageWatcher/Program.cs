using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using ShaolanPet;

static partial class WatcherTests
{
    const string ThreadId = "00000000-0000-0000-0000-000000000001";
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    static int assertions;
    static void Check(bool value, string name)
    {
        assertions++;
        if (!value) throw new Exception(name);
    }
    static string Json(object value) => JsonSerializer.Serialize(value);
    static string Header(string version = "0.165.0-alpha.1.2", string thread = ThreadId) => Json(new { type = "session_meta", payload = new { id = thread, originator = "Codex Desktop", cli_version = version } });
    static string Turn(string id = "turn-a", string trigger = "composer") => Json(new { type = "event_msg", payload = new { type = "task_started", turn_id = id, turn_attribution = new { turn_id = id, turn_trigger = trigger, initiating_agent_path = (string?)null, parent_turn_id = (string?)null } } });
    static string Message(string id, string turn = "turn-a", string kind = "user.text", string provenance = "user", double? time = null, bool metadata = true, string type = "message", string role = "user") => Json(new { timestamp = Now.ToString("O"), type = "response_item", payload = new { id, type, role, content = new[] { new { type = "input_text", text = "synthetic fixture" } }, internal_chat_message_metadata_passthrough = metadata ? new { turn_id = turn, create_time = time ?? Now.ToUnixTimeMilliseconds() / 1000.0, content_item_kinds = new[] { kind }, content_item_metadata = new[] { new { provenance = new { type = provenance } } } } : null } });

    static void ReviewPathRegression(string root)
    {
        string home = Path.Combine(root, "link-case"), selected = Path.Combine(home, "sessions", "selected");
        string outside = Path.Combine(home, "outside"), config = Path.Combine(home, "source.json");
        Directory.CreateDirectory(selected); Directory.CreateDirectory(outside);
        string file = Path.Combine(selected, "rollout.jsonl"), external = Path.Combine(outside, "rollout.jsonl");
        File.WriteAllText(file, Header() + "\n"); File.WriteAllText(external, Header() + "\n");
        File.WriteAllText(config, Json(new { threadId = ThreadId, rolloutPath = file }));
        var ids = new ConcurrentQueue<string>();
        using var watcher = new CodexMessageWatcher(ids.Enqueue, home, config, () => Now.AddSeconds(-1));
        watcher.PollOnce();
        using (var outsideHandle = File.OpenRead(external)) Check(!watcher.IsSafeOpenedSource(outsideHandle), "opened handle must match selected path");
        Directory.Move(selected, selected + ".original");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(selected); start.ArgumentList.Add(outside);
        using (var process = Process.Start(start)!) { process.WaitForExit(); Check(process.ExitCode == 0, "isolated junction fixture created"); }
        try
        {
            watcher.PollOnce();
            Check(watcher.Status == "linked-source-rejected", "runtime ancestor junction is rejected before header read");
            File.AppendAllText(external, Turn() + "\n" + Message("outside-source") + "\n"); watcher.PollOnce();
            Check(ids.Count == 0, "linked target never emits input");
        }
        finally { Directory.Delete(selected); }
        Directory.Move(selected + ".original", selected);
        watcher.PollOnce();
        File.AppendAllText(file, Turn() + "\n" + Message("restored-source") + "\n"); watcher.PollOnce();
        Check(ids.Count == 1, "restored normal source rebaselines and resumes");
    }

    static void ReviewHeaderRegression(string root)
    {
        string home = Path.Combine(root, "header-case"), folder = Path.Combine(home, "sessions");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "rollout.jsonl"), config = Path.Combine(home, "source.json");
        File.WriteAllText(config, Json(new { threadId = ThreadId, rolloutPath = file }));
        File.WriteAllText(file, Header() + "\n");
        var ids = new ConcurrentQueue<string>();
        using var watcher = new CodexMessageWatcher(ids.Enqueue, home, config, () => Now.AddSeconds(-1));
        watcher.PollOnce();
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 0) File.Move(file, file + ".old");
            File.WriteAllText(file, ""); watcher.PollOnce();
            File.AppendAllText(file, Header()[..20]); watcher.PollOnce();
            File.AppendAllText(file, Header()[20..]); watcher.PollOnce();
            Check(ids.Count == pass, "header without newline cannot emit history");
            File.AppendAllText(file, "\n" + Turn() + "\n" + Message("history-after-header-" + pass) + "\n"); watcher.PollOnce();
            Check(ids.Count == pass, "newly completed header rebaselines at EOF");
            File.AppendAllText(file, Turn() + "\n" + Message("live-after-header-" + pass) + "\n"); watcher.PollOnce();
            Check(ids.Count == pass + 1, "partial replacement/truncated header recovers without replay");
        }
    }

    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ShaolanPet-watcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        try
        {
            if (args.Contains("--multichat")) { MultiChatRegression(root); Console.WriteLine($"PASS: {assertions} multichat assertions"); return 0; }
            if (args.Contains("--review-link")) { ReviewPathRegression(root); Console.WriteLine($"PASS: {assertions} path regression assertions"); return 0; }
            if (args.Contains("--review-header")) { ReviewHeaderRegression(root); Console.WriteLine($"PASS: {assertions} header regression assertions"); return 0; }
            string file = Path.Combine(root, "sessions", "rollout.jsonl"), config = Path.Combine(root, "source.json");
            File.WriteAllText(file, Header() + "\n" + Turn() + "\n" + Message("historical") + "\n");
            File.WriteAllText(config, Json(new { threadId = ThreadId, rolloutPath = file }));
            var ids = new ConcurrentQueue<string>();
            using var watcher = new CodexMessageWatcher(ids.Enqueue, root, config, () => Now.AddSeconds(-1));
            void Append(params string[] rows) { File.AppendAllText(file, string.Join("\n", rows) + "\n"); watcher.PollOnce(); }
            watcher.PollOnce();
            Check(ids.Count == 0, "startup must not replay");
            Append(Message("unknown-turn"));
            Check(ids.Count == 0, "old attribution must not be inferred");
            Append(Turn(), Message("one"), Message("one"), Message("two", kind: "user.image"));
            Check(ids.Count == 2 && ids.Distinct().Count() == 2, "new messages once, separate messages per turn");
            Check(ids.All(x => !x.Contains(ThreadId) && !x.Contains("fixture")), "callback contains opaque IDs only");
            Append(Message("heartbeat", kind: "user.heartbeat"), Message("tool-kind", kind: "additional_content.codex_apps_open_page"), Message("harness", provenance: "harness"), Message("no-meta", metadata: false), Message("assistant", role: "assistant"), Message("delegation", type: "agent_message"), Message("old", time: Now.AddMinutes(-1).ToUnixTimeSeconds()));
            foreach (string trigger in new[] { "automation_heartbeat_scheduled", "automation", "app_tool_send_message", "app_tool_create_thread", "unknown" }) Append(Turn("rejected", trigger), Message("not-human-" + trigger, "rejected"));
            Check(ids.Count == 2, "nonhuman and unknown inputs must be rejected");
            Append(Turn("queued", "queue"), Message("queued-one", "queued"));
            Check(ids.Count == 3, "queued human message");
            string partial = Message("partial", "queued");
            File.AppendAllText(file, partial[..(partial.Length / 2)]); watcher.PollOnce();
            Check(ids.Count == 3, "partial record must wait");
            File.AppendAllText(file, partial[(partial.Length / 2)..] + "\n"); watcher.PollOnce();
            Check(ids.Count == 4, "completed partial record accepted once");
            Append("{bad json", Message("after-bad", "queued"));
            Check(ids.Count == 5, "malformed line cannot break following records");
            File.Move(file, file + ".old");
            File.WriteAllText(file, Header() + "\n" + Turn() + "\n" + Message("replacement-history") + "\n"); watcher.PollOnce();
            Check(ids.Count == 5, "replacement is baselined");
            Append(Turn(), Message("after-replace"));
            Check(ids.Count == 6, "replacement follows fresh records");
            File.WriteAllText(file, Header() + "\n"); watcher.PollOnce();
            Append(Turn(), Message("after-truncate"));
            Check(ids.Count == 7, "truncate rebaselines without replay");
            watcher.Dispose(); Append(Turn(), Message("disposed"));
            Check(ids.Count == 7, "disposed watcher emits nothing");
            using var restart = new CodexMessageWatcher(ids.Enqueue, root, config, () => Now.AddSeconds(-1)); restart.PollOnce();
            Check(ids.Count == 7, "restart does not replay");
            File.WriteAllText(config, Json(new { threadId = ThreadId, rolloutPath = Path.Combine(root, "outside.jsonl") }));
            using var outside = new CodexMessageWatcher(ids.Enqueue, root, config, () => Now); outside.PollOnce();
            Check(ids.Count == 7, "path outside sessions rejected");
            Check(outside.Status == "invalid-source-selection", "invalid selection has a safe status");
            File.WriteAllText(config, Json(new { threadId = ThreadId, rolloutPath = file }));
            File.WriteAllText(file, Header("future-version") + "\n");
            using var future = new CodexMessageWatcher(ids.Enqueue, root, config, () => Now.AddSeconds(-1)); future.PollOnce();
            Append(Turn(), Message("future-version")); future.PollOnce();
            Check(ids.Count == 7 && future.Status == "unsupported-session-metadata", "unknown version fails closed");
            using var missing = new CodexMessageWatcher(ids.Enqueue, root, config + ".missing", () => Now); missing.PollOnce();
            Check(missing.Status == "source-not-configured", "missing configuration is inert");
            File.WriteAllText(file, Header() + "\n");
            int callbackAttempts = 0;
            using var resilient = new CodexMessageWatcher(_ => { callbackAttempts++; throw new Exception("synthetic callback failure"); }, root, config, () => Now.AddSeconds(-1)); resilient.PollOnce();
            Append(Turn(), Message("throw")); resilient.PollOnce();
            Check(ids.Count == 7 && callbackAttempts == 1, "callback exception stays in collector");
            File.Move(file, file + ".replacement");
            File.WriteAllText(file, "{broken header}\n"); resilient.PollOnce();
            Append(Turn(), Message("bad-header")); resilient.PollOnce();
            Check(ids.Count == 7 && callbackAttempts == 1, "bad replacement header cannot reuse previous validation");
            File.Move(file, file + ".broken");
            File.WriteAllText(file, Header() + "\n");
            using var asyncWatcher = new CodexMessageWatcher(ids.Enqueue, root, config, () => Now.AddSeconds(-1));
            asyncWatcher.Start(); asyncWatcher.Start();
            SpinWait.SpinUntil(() => asyncWatcher.Status == "watching-selected-chat", 3000);
            File.AppendAllText(file, Turn() + "\n" + Message("async") + "\n");
            Check(SpinWait.SpinUntil(() => ids.Count == 8, 3000), "Start observes new append without blocking caller");
            asyncWatcher.Dispose();
            File.AppendAllText(file, Message("async-disposed") + "\n");
            Thread.Sleep(600);
            Check(ids.Count == 8 && asyncWatcher.Status == "stopped", "Dispose stops background callback");
            var burstIds = new ConcurrentQueue<string>();
            using var burst = new CodexMessageWatcher(burstIds.Enqueue, root, config, () => Now.AddSeconds(-1)); burst.PollOnce();
            File.AppendAllText(file, Turn() + "\n" + string.Join("\n", Enumerable.Range(0, 32).Select(i => Message("burst-" + i))) + "\n"); burst.PollOnce();
            Check(burstIds.Count == 32 && burstIds.Distinct().Count() == 32, "rapid messages remain independent");
            File.AppendAllText(file, new string('x', 4 * 1024 * 1024 + 255) + "\n" + Message("after-oversize") + "\n");
            for (int i = 0; i < 6; i++) burst.PollOnce();
            Check(burstIds.Count == 33, "oversized line is skipped without losing next input");
            File.WriteAllText(config, Json(new { threadId = ThreadId }));
            string named = Path.Combine(root, "sessions", "rollout-date-" + ThreadId + ".jsonl");
            File.WriteAllText(named, Header() + "\n");
            using var selectedIdOnly = new CodexMessageWatcher(burstIds.Enqueue, root, config, () => Now.AddSeconds(-1)); selectedIdOnly.PollOnce();
            File.AppendAllText(named, Turn() + "\n" + Message("id-only") + "\n"); selectedIdOnly.PollOnce();
            Check(burstIds.Count == 34, "thread-only configuration resolves exact filename");
            ReviewPathRegression(root);
            ReviewHeaderRegression(root);
            MultiChatRegression(root);
            Console.WriteLine($"PASS: {assertions} collector assertions");
            return 0;
        }
        finally
        {
            string verified = Path.GetFullPath(root);
            if (verified.StartsWith(Path.Combine(Path.GetTempPath(), "ShaolanPet-watcher-test-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(verified, true);
        }
    }
}
