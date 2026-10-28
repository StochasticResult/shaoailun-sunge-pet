namespace ShaolanPet;

static class CodexReactionTests
{
    public static void Verify()
    {
        bool busy = true;
        int played = 0;
        var queue = new CodexReactionQueue(true, () => !busy, () => { played++; busy = true; return true; });
        if (!queue.Enqueue("one") || queue.Enqueue("one")) throw new Exception("Codex 事件去重失败");
        queue.Drain();
        if (played != 0 || queue.PendingCount != 1) throw new Exception("Codex 打断现有音频");
        busy = false; queue.Drain(); queue.Drain();
        if (played != 1 || queue.PendingCount != 0) throw new Exception("Codex 重复播放");
        queue.Enqueue("two"); queue.Enqueue("three");
        queue.SetEnabled(false); busy = false; queue.Drain();
        if (played != 1 || queue.PendingCount != 0 || queue.Enqueue("disabled")) throw new Exception("关闭后仍有 Codex 反应");
        queue.SetEnabled(true);
        for (int i = 0; i < 16; i++)
            if (!queue.Enqueue("burst-" + i)) throw new Exception("Codex 小队列丢事件");
        if (queue.Enqueue("overflow") || queue.PendingCount != 16) throw new Exception("Codex 队列未限界");
        for (int i = 0; i < 16; i++) { busy = false; queue.Drain(); }
        if (played != 17 || queue.PendingCount != 0) throw new Exception("Codex 快速提交播放次数错误");
        queue.Enqueue("stop"); queue.Clear(); busy = false; queue.Drain();
        if (played != 17) throw new Exception("停止未清理 Codex 队列");
        if (queue.Enqueue("") || queue.Enqueue(new string('x', 513))) throw new Exception("Codex 事件标识未限界");
        var failing = new CodexReactionQueue(true, () => true, () => false);
        failing.Enqueue("bad-media"); failing.Drain(); failing.Drain();
        if (failing.PendingCount != 0) throw new Exception("Codex 播放失败后无限重试");
    }
}
