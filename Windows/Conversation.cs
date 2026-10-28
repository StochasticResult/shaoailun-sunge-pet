namespace ShaolanPet;

// Only one conversation is active. A new click or stop invalidates old media events.
readonly record struct ConversationTurn(long Generation, string Speaker, string Root, int Hop);

sealed class Conversation
{
    private readonly Func<string, bool> canReply;
    private readonly Func<ConversationTurn, bool> playReply;
    private long generation;
    private ConversationTurn? current;
    public bool Enabled { get; private set; }

    public Conversation(bool enabled, Func<string, bool> canReply, Func<ConversationTurn, bool> playReply)
    {
        Enabled = enabled; this.canReply = canReply; this.playReply = playReply;
    }
    public ConversationTurn Start(string speaker, bool allowReplies = true)
    {
        var turn = new ConversationTurn(++generation, speaker, allowReplies ? speaker : "codex", 0);
        current = turn;
        return turn;
    }
    public void SetEnabled(bool enabled) { Enabled = enabled; Cancel(); }
    public void Cancel() { generation++; current = null; }
    public void CancelIfCurrent(ConversationTurn turn) { if (current == turn) Cancel(); }

    public void Ended(ConversationTurn turn)
    {
        if (!Enabled || current != turn) return;
        // Match the Mac protocol: 薄肌→太对了→峰哥→太对了, or 峰哥→太对了.
        string? next = turn switch {
            { Speaker: "shaolan", Hop: 0, Root: "shaolan" } => "sunpet",
            { Speaker: "sunpet", Hop: 1, Root: "shaolan" } => "fengge",
            { Speaker: "fengge", Hop: 2, Root: "shaolan" } => "sunpet",
            { Speaker: "fengge", Hop: 0, Root: "fengge" } => "sunpet",
            _ => null
        };
        if (next == null || !canReply(next)) { Cancel(); return; }
        var reply = new ConversationTurn(turn.Generation, next, turn.Root, turn.Hop + 1);
        current = reply;
        if (!playReply(reply)) Cancel();
    }
}

// Called only on the UI thread. Excess bursts are dropped instead of building a stale backlog.
sealed class CodexReactionQueue
{
    private readonly Queue<string> pending = new();
    private readonly Queue<string> recent = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Func<bool> canPlay;
    private readonly Func<bool> play;
    private bool enabled;
    public int PendingCount => pending.Count;

    public CodexReactionQueue(bool enabled, Func<bool> canPlay, Func<bool> play)
    {
        this.enabled = enabled; this.canPlay = canPlay; this.play = play;
    }
    public bool Enqueue(string eventId)
    {
        if (!enabled || string.IsNullOrWhiteSpace(eventId) || eventId.Length > 512 || seen.Contains(eventId) || pending.Count >= 16) return false;
        seen.Add(eventId); recent.Enqueue(eventId); pending.Enqueue(eventId);
        if (recent.Count > 256) seen.Remove(recent.Dequeue());
        return true;
    }
    public void SetEnabled(bool value) { enabled = value; if (!value) Clear(); }
    public void Clear() => pending.Clear();
    public void Drain()
    {
        if (!enabled || pending.Count == 0 || !canPlay()) return;
        pending.Dequeue();
        play();
    }
}

static class ConversationTests
{
    public static void Verify()
    {
        var played = new List<ConversationTurn>();
        var available = new HashSet<string> { "shaolan", "sunpet", "fengge" };
        var chain = new Conversation(true, available.Contains, turn => { played.Add(turn); return true; });
        var first = chain.Start("shaolan");
        chain.Ended(first);
        if (played.Count != 1 || played[0] != new ConversationTurn(first.Generation, "sunpet", "shaolan", 1)) throw new Exception("薄肌接太对了失败");
        chain.Ended(played[0]);
        if (played.Count != 2 || played[1].Speaker != "fengge" || played[1].Hop != 2) throw new Exception("太对了接峰哥失败");
        chain.Ended(played[1]);
        if (played.Count != 3 || played[2].Speaker != "sunpet" || played[2].Hop != 3) throw new Exception("峰哥接太对了失败");
        chain.Ended(played[2]); chain.Ended(played[2]);
        if (played.Count != 3) throw new Exception("接话没有停止");
        played.Clear();
        var feng = chain.Start("fengge"); chain.Ended(feng);
        if (played.Count != 1 || played[0].Speaker != "sunpet") throw new Exception("峰哥起头失败");
        chain.Ended(played[0]);
        if (played.Count != 1) throw new Exception("峰哥起头没有停止");
        played.Clear();
        var sun = chain.Start("sunpet"); chain.Ended(sun);
        if (played.Count != 0) throw new Exception("太对了起头不应接话");
        var codex = chain.Start("sunpet", allowReplies: false); chain.Ended(codex); chain.Ended(codex);
        if (played.Count != 0) throw new Exception("Codex 反应不应接话");
        var old = chain.Start("shaolan");
        var newer = chain.Start("shaolan");
        chain.Ended(old);
        if (played.Count != 0) throw new Exception("连点后旧接话仍然触发");
        chain.CancelIfCurrent(old); chain.Ended(newer);
        if (played.Count != 1) throw new Exception("旧播放不能取消新接话");
        played.Clear();
        var cancelled = chain.Start("shaolan"); chain.Cancel(); chain.Ended(cancelled);
        if (played.Count != 0) throw new Exception("停止后仍然接话");
        var disabled = chain.Start("shaolan"); chain.SetEnabled(false); chain.Ended(disabled);
        if (played.Count != 0) throw new Exception("关闭联动后仍然接话");
        chain.SetEnabled(true); available.Remove("sunpet");
        var missing = chain.Start("shaolan"); chain.Ended(missing);
        if (played.Count != 0) throw new Exception("角色关闭后仍然接话");
        available.Add("sunpet"); chain.Ended(missing);
        if (played.Count != 0) throw new Exception("角色重新开启触发旧接话");
        var failure = new Conversation(true, _ => true, _ => false);
        var bad = failure.Start("shaolan"); failure.Ended(bad); failure.Ended(bad);
    }
}
