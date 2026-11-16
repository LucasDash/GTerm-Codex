using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

static void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
}
static object? FieldOrProperty(object target, string name)
{
    return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(Path.Combine(args[0], "GTerm.dll")))
    throw new ArgumentException("Provide the absolute directory of a built GTerm.dll.");
string appDirectory = args[0];
string fixture = Path.Combine(Path.GetTempPath(), "GTerm Dedicated Test " + Guid.NewGuid().ToString("N"));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string dependency = Path.Combine(appDirectory, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDirectory, "GTerm.dll"));
Type configType = app.GetType("GTerm.Config", true)!;
ConstructorInfo configConstructor = configType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string[])], null)!;
object config = configConstructor.Invoke([new string[] {"--GmodRoot", fixture, "--ModulePort", "27524", "--ModuleTransport", "WINDOWS_NAMED_PIPE", "--MCPPort", "27523", "--MCP", "1", "--skipmodule", "--skipgamefiles", "--skipmoduleupdate", "--MCPSecret", "MiXeD-Secret"}]);
Check((string?)FieldOrProperty(config, "GmodRoot") == fixture, "CLI root must preserve exact case and spacing");
Check((int)FieldOrProperty(config, "ModulePort")! == 27524, "module port");
Check(FieldOrProperty(config, "ModuleTransport")!.ToString() == "WindowsNamedPipe", "module transport CLI parsing must be case insensitive");
Check((int)FieldOrProperty(config, "MCPPort")! == 27523, "nullable MCP port");
Check((string?)FieldOrProperty(config, "MCPSecret") == "MiXeD-Secret", "CLI secret case");
Check((bool)FieldOrProperty(config, "MCP")!, "nullable MCP flag");
Check((bool)FieldOrProperty(config, "SkipModule")! && (bool)FieldOrProperty(config, "SkipGameFiles")!
    && (bool)FieldOrProperty(config, "SkipModuleUpdate")!, "multiple skip flags");
object disabledConfig = configConstructor.Invoke([new string[] {"--MCP", "false"}]);
Check(!(bool)FieldOrProperty(disabledConfig, "MCP")!, "explicit false must not enable MCP");
Check(FieldOrProperty(disabledConfig, "ModuleTransport")!.ToString() == "Tcp", "module transport defaults to legacy TCP");
try { configConstructor.Invoke([new string[] {"--mcp", "unexpected"}]); throw new Exception("accepted invalid boolean"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }
try { configConstructor.Invoke([new string[] {"--moduleport", "0"}]); throw new Exception("accepted invalid module port"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { }
try { configConstructor.Invoke([new string[] {"--moduletransport", "fallback"}]); throw new Exception("accepted unknown module transport"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }

Type interop = app.GetType("GTerm.GmodInterop", true)!;
MethodInfo configure = interop.GetMethod("ConfigureTargetRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
MethodInfo resolve = interop.GetMethod("TryGetGmodPath", BindingFlags.Static | BindingFlags.NonPublic)!;
configure.Invoke(null, [null]);
configure.Invoke(null, [""]);
try { configure.Invoke(null, ["   "]); throw new Exception("accepted whitespace-only explicit root"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }
try { configure.Invoke(null, ["relative-server"]); throw new Exception("accepted relative explicit root"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }
try { configure.Invoke(null, [Path.Combine(fixture, "missing")]); throw new Exception("accepted invalid explicit root"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }
Directory.CreateDirectory(Path.Combine(fixture, "garrysmod", "lua", "includes"));
File.WriteAllText(Path.Combine(fixture, "garrysmod", "lua", "includes", "init.lua"), "-- fixture");
File.WriteAllText(Path.Combine(fixture, "srcds_console_win64.exe"), "");
configure.Invoke(null, [fixture]);
configure.Invoke(null, [""]);
Check(interop.GetField("TargetRoot", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) == null,
    "empty root must clear explicit target");
configure.Invoke(null, [fixture]);
object?[] rootArgs = ["", false];
Check((bool)resolve.Invoke(null, rootArgs)! && (string?)rootArgs[0] == fixture, "dedicated root");
object?[] exeArgs = ["", true];
Check((bool)resolve.Invoke(null, exeArgs)! && ((string)exeArgs[0]!).EndsWith("srcds_console_win64.exe"), "dedicated executable");
File.Delete(Path.Combine(fixture, "garrysmod", "lua", "includes", "init.lua"));
object?[] goneArgs = ["", false];
Check(!(bool)resolve.Invoke(null, goneArgs)! && (string?)goneArgs[0] == "", "invalidated root must not fall back");
configure.Invoke(null, [null]);

Type listenerType = app.GetType("GTerm.Listeners.TcpLogListener", true)!;
ConstructorInfo listenerConstructor = listenerType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(int)], null)!;
try { listenerConstructor.Invoke([0]); throw new Exception("accepted invalid listener port"); }
catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { }
using var server = new TcpListener(IPAddress.Loopback, 0);
server.Start();
int port = ((IPEndPoint)server.LocalEndpoint).Port;
object listener = listenerConstructor.Invoke([port]);
listenerType.GetMethod("Start")!.Invoke(listener, null);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using TcpClient client = await server.AcceptTcpClientAsync(timeout.Token);
while (!(bool)listenerType.GetProperty("IsConnected")!.GetValue(listener)!)
    await Task.Delay(10, timeout.Token);
MethodInfo write = listenerType.GetMethod("WriteMessage")!;
await (Task)write.Invoke(listener, ["status"])!;
byte[] header = new byte[4];
await client.GetStream().ReadExactlyAsync(header, timeout.Token);
int length = BitConverter.ToInt32(header);
byte[] body = new byte[length];
await client.GetStream().ReadExactlyAsync(body, timeout.Token);
Check(Encoding.UTF8.GetString(body) == "status", "framed command over selected port");
// A forged length must be rejected before allocating the announced payload.
await client.GetStream().WriteAsync(new byte[] { 0xff, 0xff, 0xff, 0xff }, timeout.Token);
Check(await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0, "oversized frame must disconnect");
using TcpClient reconnected = await server.AcceptTcpClientAsync(timeout.Token);
await reconnected.GetStream().WriteAsync(BitConverter.GetBytes(8 * 1024 * 1024 + 1), timeout.Token);
Check(await reconnected.GetStream().ReadAsync(new byte[1], timeout.Token) == 0, "one-byte-over-limit frame must disconnect");
using TcpClient afterOversized = await server.AcceptTcpClientAsync(timeout.Token);
// Send a malformed short log; it should disconnect without preventing a later reconnect.
await afterOversized.GetStream().WriteAsync(new byte[] { 1, 0, 0, 0, 0 }, timeout.Token);
Check(await afterOversized.GetStream().ReadAsync(new byte[1], timeout.Token) == 0, "truncated log frame must disconnect");
using TcpClient afterMalformed = await server.AcceptTcpClientAsync(timeout.Token);
while (!(bool)listenerType.GetProperty("IsConnected")!.GetValue(listener)!)
    await Task.Delay(10, timeout.Token);
await (Task)write.Invoke(listener, ["status"])!;
await afterMalformed.GetStream().ReadExactlyAsync(header, timeout.Token);
length = BitConverter.ToInt32(header);
body = new byte[length];
await afterMalformed.GetStream().ReadExactlyAsync(body, timeout.Token);
Check(Encoding.UTF8.GetString(body) == "status", "reconnect after malformed frame");
File.Delete(Path.Combine(fixture, "srcds_console_win64.exe"));
Directory.Delete(Path.Combine(fixture, "garrysmod", "lua", "includes"));
Directory.Delete(Path.Combine(fixture, "garrysmod", "lua"));
Directory.Delete(Path.Combine(fixture, "garrysmod"));
Directory.Delete(fixture);
Console.WriteLine("DEDICATED_TARGET_TESTS_PASS config, transport selection, no-fallback, listener port, framing and reconnect");
