using System.Drawing;
using GTerm.Listeners;
using GTerm.MCP;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

FakeListener listener = new();
ConsoleHistory history = new(listener);
ConsolePage empty = history.ReadAfter(null, 0, 10);
Check(empty.Latest == 0 && empty.Next == 0 && empty.Entries.Count == 0, "empty history");

listener.Emit("first");
listener.Emit("<<GTERM:STATUS>>internal<<END>>");
listener.Emit("second");
ConsolePage first = history.ReadAfter(null, 0, 1);
ConsolePage second = history.ReadAfter(first.Epoch, first.Next, 1);
Check(first.Entries.Single().Line.Message == "first" && first.Next == 1, "first page");
Check(second.Entries.Single().Line.Message == "second" && second.Next == 2, "second page and sentinel filter");
Check(history.GetRecent(2).Select(x => x.Message).SequenceEqual(new[] { "second", "first" }), "existing newest-first API");
Check(history.ReadAfter(first.Epoch, first.Next, 1).Entries.Single().Sequence == 2, "repeatable cursor");
Check(history.ReadAfter(first.Epoch, second.Next, 1).Entries.Count == 0, "caught up");

for (int i = 0; i < 1001; i++) listener.Emit("line " + i);
ConsolePage overflow = history.ReadAfter(first.Epoch, 0, 20);
Check(overflow.Oldest == 4 && overflow.DroppedBefore == 3 && overflow.Entries.Count == 20, "bounded history and gap");
Check(overflow.Next == 23 && overflow.Latest == 1003, "overflow pagination");
ConsolePage changed = new ConsoleHistory(new FakeListener()).ReadAfter(first.Epoch, 1003, 10);
Check(changed.EpochChanged && changed.Next == 0 && changed.Entries.Count == 0, "epoch restart");
try { history.ReadAfter(first.Epoch, 1004, 1); throw new Exception("accepted future cursor"); }
catch (ArgumentOutOfRangeException) { }
try { history.ReadAfter(null, 2, 1); throw new Exception("accepted cursor without epoch"); }
catch (ArgumentException) { }
try { history.ReadAfter(first.Epoch, 0, 21); throw new Exception("accepted oversized page"); }
catch (ArgumentOutOfRangeException) { }

FakeListener concurrentListener = new();
ConsoleHistory concurrent = new(concurrentListener);
Parallel.For(0, 2000, i => concurrentListener.Emit("event " + i));
ConsolePage concurrentPage = concurrent.ReadAfter(null, 0, 20);
Check(concurrentPage.Latest == 2000 && concurrentPage.Oldest == 1001
    && concurrentPage.DroppedBefore == 1000, "concurrent publication remains ordered and bounded");
Check(concurrentPage.Entries.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1001, 20).Select(x => (long)x)), "sequence order");
Console.WriteLine("ConsoleHistory tests passed");

sealed class FakeListener : ILogListener
{
    public event EventHandler? OnConnected;
    public event EventHandler? OnDisconnected;
    public event ErrorEventHandler? OnError;
    public event LogEventHandler? OnLog;
    public bool IsConnected => true;
    public void Start() { }
    public Task WriteMessage(string input) => Task.CompletedTask;
    public void Emit(string message) => OnLog?.Invoke(this, new LogEventArgs(0, 0, "", Color.White, message));
}

namespace GTerm.MCP
{
    // The history only needs these data containers from CommandCollector.
    internal class OutputLine
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ColorInfo Color { get; set; } = new();
    }
    internal class ColorInfo
    {
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }
        public int A { get; set; }
    }
}
