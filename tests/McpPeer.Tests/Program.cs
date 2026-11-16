using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

// Exercise the actual request handler and HTTP.sys using a disposable listener.
// An uninitialized instance is sufficient for these requests with no secret.
Type type = Assembly.Load("GTerm").GetType("GTerm.MCP.MCPServer", true)!;
object server = RuntimeHelpers.GetUninitializedObject(type);
MethodInfo handle = type.GetMethod("HandleRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
Type peerType = Assembly.Load("GTerm").GetType("GTerm.HttpPeer", true)!;
MethodInfo isLoopback = peerType.GetMethod("IsLoopback", BindingFlags.Static | BindingFlags.NonPublic)!;
bool CheckPeer(IPEndPoint? endpoint) => (bool)isLoopback.Invoke(null, [endpoint])!;
if (CheckPeer(null)) throw new Exception("Missing peer accepted");
if (!CheckPeer(new IPEndPoint(IPAddress.IPv6Loopback, 1))) throw new Exception("IPv6 loopback rejected");
if (!CheckPeer(new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 1))) throw new Exception("Mapped loopback rejected");
if (CheckPeer(new IPEndPoint(IPAddress.Parse("192.0.2.1").MapToIPv6(), 1))) throw new Exception("Mapped remote peer accepted");
using TcpListener reservation = new(IPAddress.Loopback, 0);
reservation.Start();
int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using HttpListener listener = new();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Start();

async Task<int> Exchange(IPAddress destination, string method, string query = "")
{
    using TcpClient client = new(destination.AddressFamily);
    await client.ConnectAsync(destination, port).WaitAsync(TimeSpan.FromSeconds(5));
    using NetworkStream stream = client.GetStream();
    string body = method == "POST" ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}" : "";
    byte[] request = Encoding.ASCII.GetBytes($"{method} /{query} HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
    await stream.WriteAsync(request);
    HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Task handled = (Task)handle.Invoke(server, [context])!;
    await handled.WaitAsync(TimeSpan.FromSeconds(5));
    using StreamReader reader = new(stream, Encoding.ASCII);
    string status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "";
    return int.Parse(status.Split(' ')[1]);
}

if (await Exchange(IPAddress.Loopback, "OPTIONS") != 200) throw new Exception("Local OPTIONS rejected");
if (await Exchange(IPAddress.Loopback, "GET") != 405) throw new Exception("Local GET rejected");
if (await Exchange(IPAddress.Loopback, "POST") != 200) throw new Exception("Local POST rejected");
IPAddress? lan = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Select(a => a.Address)
    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."));
if (lan == null) throw new Exception("A non-loopback IPv4 address is required for the peer boundary check");
if (await Exchange(lan, "OPTIONS") != 403) throw new Exception("Non-loopback OPTIONS accepted");
if (await Exchange(lan, "GET") != 403) throw new Exception("Non-loopback GET accepted");
if (await Exchange(lan, "POST") != 403) throw new Exception("Non-loopback POST accepted");
type.GetField("Secret", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, "fixture-secret");
if (await Exchange(IPAddress.Loopback, "POST") != 403) throw new Exception("Missing secret accepted locally");
if (await Exchange(IPAddress.Loopback, "POST", "?secret=fixture-secret") != 200) throw new Exception("Valid secret rejected locally");
if (await Exchange(lan, "POST", "?secret=fixture-secret") != 403) throw new Exception("Non-loopback POST accepted with valid secret");

// Exercise the actual optional WebSocket API, including the upgrade decision.
using TcpListener wsReservation = new(IPAddress.Loopback, 0);
wsReservation.Start();
int wsPort = ((IPEndPoint)wsReservation.LocalEndpoint).Port;
wsReservation.Stop();
Type wsType = Assembly.Load("GTerm").GetType("GTerm.WebSocketAPI", true)!;
ConstructorInfo ctor = wsType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
object wsServer = ctor.Invoke([null, wsPort, "fixture-secret"]);
Task wsRun = (Task)wsType.GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wsServer, null)!;
async Task<int> WebSocketHandshake(IPAddress destination)
{
    using TcpClient client = new(destination.AddressFamily);
    await client.ConnectAsync(destination, wsPort).WaitAsync(TimeSpan.FromSeconds(5));
    using NetworkStream stream = client.GetStream();
    string request = $"GET /ws/?secret=fixture-secret HTTP/1.1\r\nHost: localhost:{wsPort}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    using StreamReader reader = new(stream, Encoding.ASCII);
    string status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "";
    return int.Parse(status.Split(' ')[1]);
}
if (wsRun.IsFaulted) await wsRun;
if (await WebSocketHandshake(lan) != 403) throw new Exception("Non-loopback WebSocket accepted");
if (await WebSocketHandshake(IPAddress.Loopback) != 101) throw new Exception("Local WebSocket rejected");
wsType.GetMethod("Stop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wsServer, null);
Console.WriteLine("MCP and WebSocket peer boundary: local requests accepted; LAN requests with localhost Host rejected.");
