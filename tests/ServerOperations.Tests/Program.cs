using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

Assembly asm = Assembly.Load("GTerm");
Type ops = asm.GetType("GTerm.MCP.ServerOperations", true)!;
const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
object? Invoke(string method, params object?[] args) => ops.GetMethod(method, flags)!.Invoke(null, args);
bool Reject(string method, JObject arg) { try { Invoke(method, arg); return false; } catch (TargetInvocationException e) { return e.InnerException is ArgumentException; } }
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }

JObject user = new() { ["user_id"] = 7 };
string snapshot = (string)Invoke("Snapshot")!;
string hookInspect = (string)Invoke("Hooks", new JObject { ["event"] = "Think", ["limit"] = 8 })!;
string chat = (string)Invoke("Chat", new JObject(user) { ["message"] = "Hello \"Veejay\" \\ world" })!;
string hold = (string)Invoke("Hold", new JObject(user) { ["seconds"] = 2 })!;
string release = (string)Invoke("Hold", new JObject(user) { ["action"] = "release" })!;
Check(snapshot.Contains("math.min(#users,8)") && !snapshot.Contains("SteamID"), "bounded snapshot excludes account IDs");
Check(chat.Contains("\\\"Veejay\\\"") && chat.Contains("\\\\ world"), "chat escapes quotes and backslashes");
Check(hold.Contains("timer.Create") && hold.Contains("timer_creation_failed") && hold.Contains("player_already_frozen"), "hold includes timed cleanup and ownership guards");
Check(release.Contains("no_owned_hold") && release.Contains("timer.Remove"), "release only targets an owned hold");
Check(hookInspect.Contains("GTERM_LUA_HOOKS") && hookInspect.Contains("debug.getinfo"), "hook inspection emits bounded source metadata");
Check(Reject("Hooks", new JObject { ["event"] = "Think;evil" }), "hook event injection rejected");
Check(Reject("Hooks", new JObject { ["event"] = "Think", ["limit"] = 17 }), "hook result limit enforced");
Check(Reject("Chat", new JObject { ["user_id"] = 0, ["message"] = "x" }), "invalid player ID rejected");
Check(Reject("Chat", new JObject { ["user_id"] = 1.5, ["message"] = "x" }), "fractional player ID rejected");
Check(Reject("Chat", new JObject(user) { ["message"] = "one\ntwo" }), "control characters rejected");
Check(Reject("Chat", new JObject(user) { ["message"] = new string('a', 161) }), "oversized message rejected");
Check(Reject("Hold", new JObject(user) { ["seconds"] = 0 }), "zero-duration hold rejected");
Check(Reject("Hold", new JObject(user) { ["seconds"] = 16 }), "excessive hold rejected");
Check(Reject("Hold", new JObject(user) { ["action"] = "other" }), "unknown hold action rejected");
Type monitor = asm.GetType("GTerm.MCP.ServerChatMonitor", true)!;
object? Monitor(JObject args) => monitor.GetMethod("Build", flags)!.Invoke(null, new object?[] { args });
bool RejectMonitor(JObject args) { try { Monitor(args); return false; } catch (TargetInvocationException e) { return e.InnerException is ArgumentException; } }
string monitorStart = (string)Monitor(new JObject { ["action"] = "start", ["seconds"] = 10 })!;
string monitorRead = (string)Monitor(new JObject { ["action"] = "read", ["epoch"] = "123", ["after_sequence"] = 0 })!;
string monitorStop = (string)Monitor(new JObject { ["action"] = "stop", ["epoch"] = "123" })!;
Check(monitorStart.Contains("#own.records>64") && monitorStart.Contains("timer.Create") && monitorStart.Contains("PlayerSay"), "chat monitor is explicit, bounded and expiring");
Check(RejectMonitor(new JObject { ["action"] = "start", ["seconds"] = 121 }), "chat monitor duration cap enforced");
Check(RejectMonitor(new JObject { ["action"] = "read", ["epoch"] = "1;evil" }), "chat monitor epoch injection rejected");
Check(RejectMonitor(new JObject { ["action"] = "read", ["epoch"] = "123", ["limit"] = 5 }), "chat monitor page limit enforced");
Check(RejectMonitor(new JObject { ["action"] = "stop" }), "chat monitor stop requires epoch");
if (args.Length == 2 && args[0] == "--emit")
    File.WriteAllText(args[1], JsonConvert.SerializeObject(new { snapshot, hookInspect, chat, hold, release, monitorStart, monitorRead, monitorStop }));
Type requestType = asm.GetType("GTerm.MCP.RequestResult", true)!;
Type outcomeType = asm.GetType("GTerm.MCP.RequestOutcome", true)!;
object answered = Activator.CreateInstance(requestType, true)!;
requestType.GetProperty("Outcome", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(answered, Enum.Parse(outcomeType, "Answered"));
Type sourceType = typeof(TaskCompletionSource<>).MakeGenericType(requestType);
object source = Activator.CreateInstance(sourceType)!;
object decisionTask = sourceType.GetProperty("Task")!.GetValue(source)!;
MethodInfo waitForDecision = asm.GetType("GTerm.MCP.ToolPackages", true)!.GetMethod("WaitForDecisionAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
Task pendingWait = (Task)waitForDecision.Invoke(null, new[] { decisionTask, (object)20 })!;
await pendingWait;
object pending = pendingWait.GetType().GetProperty("Result")!.GetValue(pendingWait)!;
Check(requestType.GetProperty("Outcome", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending)!.ToString() == "Pending", "unanswered consent returns pending");
sourceType.GetMethod("SetResult")!.Invoke(source, new[] { answered });
Task answeredWait = (Task)waitForDecision.Invoke(null, new[] { decisionTask, (object)20 })!;
await answeredWait;
object returned = answeredWait.GetType().GetProperty("Result")!.GetValue(answeredWait)!;
Check(ReferenceEquals(returned, answered), "completed consent returns actual decision");
Console.WriteLine($"Server operations: {passed} checks passed.");
