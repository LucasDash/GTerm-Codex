using GTerm.Listeners;

namespace GTerm.MCP
{
    internal sealed record ConsoleEntry(long Sequence, OutputLine Line);
    internal sealed record ConsolePage(string Epoch, long Latest, long Oldest,
        long Next, long? DroppedBefore, bool EpochChanged, IReadOnlyList<ConsoleEntry> Entries);

    /// <summary>
    /// An always-on ring buffer of recent console output. The console is a live stream: by the time
    /// an agent decides to look at it, the lines it wants have already scrolled past. Keeping a
    /// backlog lets "what just printed?" be answered instantly and looking BACKWARDS (newest first),
    /// instead of racing a forward capture window that can only ever see the future.
    /// </summary>
    internal sealed class ConsoleHistory
    {
        private const int Capacity = 1000;

        private readonly LinkedList<ConsoleEntry> Buffer = new();
        private readonly object Locker = new();
        private readonly string Epoch = Guid.NewGuid().ToString("N");
        private long Sequence;

        internal ConsoleHistory(ILogListener listener)
        {
            listener.OnLog += OnLog;
        }

        private void OnLog(object sender, LogEventArgs args)
        {
            // GTerm's own probe/result markers are plumbing, not console output.
            if (GTermSentinels.IsSentinel(args.Message)) return;

            lock (this.Locker)
            {
                OutputLine line = new()
                {
                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    Message = args.Message,
                    Color = new ColorInfo { R = args.Color.R, G = args.Color.G, B = args.Color.B, A = args.Color.A },
                };
                this.Buffer.AddLast(new ConsoleEntry(++this.Sequence, line));

                while (this.Buffer.Count > Capacity) this.Buffer.RemoveFirst();
            }
        }

        /// <summary>Returns up to <paramref name="count"/> of the most recent lines, newest first.</summary>
        internal List<OutputLine> GetRecent(int count)
        {
            lock (this.Locker)
            {
                List<OutputLine> result = new(Math.Min(count, this.Buffer.Count));

                for (LinkedListNode<ConsoleEntry>? node = this.Buffer.Last; node != null && result.Count < count; node = node.Previous)
                    result.Add(node.Value.Line);

                return result;
            }
        }

        /// <summary>Read retained events oldest first. A cursor belongs to exactly one history epoch.</summary>
        internal ConsolePage ReadAfter(string? epoch, long after, int limit)
        {
            if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
            if (limit < 1 || limit > 20) throw new ArgumentOutOfRangeException(nameof(limit));
            if (epoch == null && after != 0) throw new ArgumentException("epoch is required with a nonzero cursor");

            lock (this.Locker)
            {
                bool changed = epoch != null && epoch != this.Epoch;
                if (changed) after = 0;
                if (after > this.Sequence) throw new ArgumentOutOfRangeException(nameof(after), "cursor is ahead of the current history");
                long oldest = this.Buffer.First?.Value.Sequence ?? this.Sequence + 1;
                long? droppedBefore = after < oldest - 1 ? oldest - 1 : null;
                List<ConsoleEntry> entries = new(limit);
                foreach (ConsoleEntry entry in this.Buffer)
                {
                    if (entry.Sequence <= after) continue;
                    entries.Add(entry);
                    if (entries.Count == limit) break;
                }
                long next = entries.Count > 0 ? entries[^1].Sequence : after;
                return new ConsolePage(this.Epoch, this.Sequence, oldest, next, droppedBefore, changed, entries);
            }
        }

        internal int Count
        {
            get { lock (this.Locker) return this.Buffer.Count; }
        }
    }
}
