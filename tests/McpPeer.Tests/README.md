# MCP peer checks

Run `dotnet run --project tests/McpPeer.Tests` on Windows with a non-loopback
IPv4 address. A disposable HTTP listener invokes the actual MCP request handler
for loopback requests and requests sent to the machine's LAN address with a
`Host: localhost` header. It verifies that OPTIONS and GET from a non-loopback
peer receive 403, including a JSON-RPC ping. It also starts the optional
WebSocket API and checks actual upgrade responses for both peers. This is a
local HTTP.sys check, not an external-host or native-module socket test.
