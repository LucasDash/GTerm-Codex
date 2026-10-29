using System.Collections;
using System.Reflection;

Assembly asm = Assembly.Load("GTerm");
Type T(string name) => asm.GetType("GTerm.MCP." + name, true)!;
const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
object New(string name) => Activator.CreateInstance(T(name), true)!;
object? Get(object o, string prop) => o.GetType().GetProperty(prop, flags)!.GetValue(o);
object? Call(Type t, string method, params object?[] a) => t.GetMethod(method, flags)!.Invoke(null, a);
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
object Result(string? marker = null, string? payload = null) {
    object r = New("CommandResult");
    if (marker != null) {
        object sent = Activator.CreateInstance(T("Sentinel"), flags, null, new object[] { marker, payload! }, null)!;
        ((IList)Get(r, "Sentinels")!).Add(sent);
    }
    return r;
}
object Interpret(string? server, string? client) => Call(T("GameStatusProbe"), "Interpret", Result(server == null ? null : "STATUS", server), Result(client == null ? null : "CLIENT", client), null)!;
string[] Names(object o) => ((IEnumerable)Get(o, "OfferedPackages")!).Cast<string>().ToArray();
object dedicated = Interpret("{\"d\":true,\"pk\":[\"agent_bridge.lua\",\"other.lua\",\"BAD.lua\"],\"pg\":[\"other.lua\"]}", null);
Check((bool)Get(dedicated, "UseDedicatedPackages")!, "dedicated server discovers in server realm");
Check((string)Get(dedicated, "Scope")! == "local-dedicated", "dedicated consent scope is separate");
Check(Names(dedicated).SequenceEqual(new[]{"agent_bridge", "other"}), "dedicated offers sanitized, deduplicated packages");
object missing = Interpret("{\"d\":true,\"pk\":[]}", null);
Check(Names(missing).Length == 0, "missing package is not offered");
object listen = Interpret("{\"d\":false,\"pk\":[\"agent_bridge.lua\"]}", null);
Check(!(bool)Get(listen, "UseDedicatedPackages")! && Names(listen).Length == 0, "listen server cannot use dedicated discovery");
object remote = Interpret(null, "{\"d\":true,\"ip\":\"10.0.0.2:27015\",\"pk\":[\"agent_bridge.lua\",\"other.lua\"]}");
Check(!(bool)Get(remote, "UseDedicatedPackages")! && Names(remote).Length == 2, "remote client keeps existing client discovery");
Check((string)Get(remote, "Scope")! == "10.0.0.2", "remote client keeps server-address consent scope");
object local = Interpret("{\"d\":false}", "{\"d\":false,\"ip\":\"loopback\",\"pk\":[\"agent_bridge.lua\"]}");
Check(!(bool)Get(local, "UseDedicatedPackages")! && Names(local).SequenceEqual(new[]{"agent_bridge"}), "local client discovery preserved");
Check((string)Get(local, "Scope")! == "local", "local client consent scope preserved");
object? dedicatedGate = Call(T("ToolPackages"), "Precheck", dedicated);
Check(dedicatedGate == null, "precheck permits dedicated server");
Check(Call(T("ToolPackages"), "Precheck", listen) != null, "precheck rejects blocked listen client");
Check(Call(T("ToolPackages"), "Precheck", remote) == null, "precheck permits remote client");
string DiscoveryRealm(object status) => Call(T("MCPServer"), "PackageDiscoveryRealm", status)!.ToString()!;
Check(DiscoveryRealm(dedicated) == "Server", "MCP package gate selects local dedicated server realm");
Check(DiscoveryRealm(remote) == "Client", "MCP package gate keeps remote client realm");
Check(DiscoveryRealm(local) == "Client", "MCP package gate keeps local client realm");
Check(DiscoveryRealm(listen) == "Client", "MCP package gate does not promote a blocked listen server");
string hash = new string('a',64);
string def = "{\"hash\":\"" + hash + "\",\"description\":\"original\",\"tools\":[" +
    "{\"name\":\"status\",\"description\":\"status\",\"realm\":\"server\",\"has_run\":true}," +
    "{\"name\":\"capture_view\",\"description\":\"capture\",\"realm\":\"client\",\"has_run\":true}]}";
object?[] parse = { "agent_bridge", def, null };
Check(Call(T("ToolPackages"), "TryParseDefinition", parse) == null && parse[2] != null, "full package definition parses");
object manifest = parse[2]!;
object restricted = Call(T("ToolPackages"), "RestrictForStatus", manifest, dedicated)!;
object filtered = restricted.GetType().GetField("Item1")!.GetValue(restricted)!;
var tools = ((IEnumerable)Get(filtered, "Tools")!).Cast<object>().ToArray();
Check(tools.Length == 1 && (string)Get(tools[0], "Name")! == "status", "dedicated manifest hides client-only tools");
Check((string)Get(filtered, "Hash")! == hash, "dedicated manifest retains full bytecode hash");
object normal = Call(T("ToolPackages"), "RestrictForStatus", manifest, local)!;
Check(ReferenceEquals(normal.GetType().GetField("Item1")!.GetValue(normal), manifest), "client manifest remains unchanged");
object?[] otherParse = { "other", def, null };
Call(T("ToolPackages"), "TryParseDefinition", otherParse);
object otherResult = Call(T("ToolPackages"), "RestrictForStatus", otherParse[2], dedicated)!;
object other = otherResult.GetType().GetField("Item1")!.GetValue(otherResult)!;
Check(((IEnumerable)Get(other, "Tools")!).Cast<object>().Count() == 1, "dedicated filters client tools from any package");
string clientOnly = "{\"hash\":\"" + hash + "\",\"tools\":[{\"name\":\"view\",\"description\":\"view\",\"realm\":\"client\",\"has_run\":true}]}";
object?[] clientParse = { "client_only", clientOnly, null };
Call(T("ToolPackages"), "TryParseDefinition", clientParse);
object noServerTools = Call(T("ToolPackages"), "RestrictForStatus", clientParse[2], dedicated)!;
Check(noServerTools.GetType().GetField("Item1")!.GetValue(noServerTools) == null, "client-only package is unusable on dedicated server");
string serverCommand = (string)T("GameStatusProbe").GetField("ServerProbeCommand", flags)!.GetValue(null)!;
Check(serverCommand.Length < 512, "server probe fits console command limit");
Console.WriteLine("All dedicated package checks passed.");
