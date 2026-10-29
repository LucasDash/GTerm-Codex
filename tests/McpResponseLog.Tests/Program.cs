using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

string executable = Process.GetCurrentProcess().MainModule!.FileName;
if (!Path.GetFileNameWithoutExtension(executable).Equals("McpResponseLog.Tests", StringComparison.OrdinalIgnoreCase))
    throw new Exception("Run the test apphost from an isolated artifacts directory; do not append to the dotnet host log.");
string log = Path.Combine(Path.GetDirectoryName(executable)!, "gterm.log");
string before = File.Exists(log) ? File.ReadAllText(log) : "";
Type server = Assembly.Load("GTerm").GetType("GTerm.MCP.MCPServer", true)!;
const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
MethodInfo send = server.GetMethod("SendResponse", flags)!;
MethodInfo error = server.GetMethod("SendError", flags)!;
using TcpListener reservation = new(IPAddress.Loopback, 0);
reservation.Start();
int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using HttpListener listener = new();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };

async Task<string> Exchange(MethodInfo method, params object?[] tail)
{
    Task<string> received = client.GetStringAsync($"http://127.0.0.1:{port}/");
    HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Task sent = (Task)method.Invoke(null, [context.Response, ..tail])!;
    await sent.WaitAsync(TimeSpan.FromSeconds(10));
    return await received.WaitAsync(TimeSpan.FromSeconds(10));
}

const string image = "PRIVATE_IMAGE_BASE64_ABC123";
const string chat = "PRIVATE_CHAT_CONTENT_DEF456";
const string id = "PRIVATE_ID_GHI789";
const string message = "PRIVATE_ERROR_JKL012";
object result = new { content = new object[] { new { type = "image", data = image, mimeType = "image/jpeg" }, new { type = "text", text = chat } } };
string response = await Exchange(send, result, JValue.CreateString(id));
JObject parsed = JObject.Parse(response);
if ((string?)parsed["id"] != id || (string?)parsed["result"]?["content"]?[0]?["data"] != image ||
    (string?)parsed["result"]?["content"]?[1]?["text"] != chat)
    throw new Exception("Response payload changed on wire.");
string errorResponse = await Exchange(error, -32603, message, JValue.CreateString(id));
JObject parsedError = JObject.Parse(errorResponse);
if ((string?)parsedError["id"] != id || (int?)parsedError["error"]?["code"] != -32603 ||
    (string?)parsedError["error"]?["message"] != message)
    throw new Exception("Error payload changed on wire.");
string after = File.ReadAllText(log);
if (!after.StartsWith(before, StringComparison.Ordinal)) throw new Exception("Log changed unexpectedly.");
string appended = after[before.Length..];
if (appended.Contains(image) || appended.Contains(chat) || appended.Contains(id) || appended.Contains(message))
    throw new Exception("A response payload appeared in the local log.");
if (!appended.Contains($"MCP response: {Encoding.UTF8.GetByteCount(response)} bytes (payload omitted)") ||
    !appended.Contains($"MCP error response: {Encoding.UTF8.GetByteCount(errorResponse)} bytes (payload omitted)"))
    throw new Exception("Expected size-only log entries missing.");
Console.WriteLine("MCP response logging: wire payload preserved; response and error logs contain byte counts only.");
