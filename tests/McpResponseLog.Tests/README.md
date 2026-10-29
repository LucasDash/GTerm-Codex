# MCP response log checks

Run `dotnet run --project tests/McpResponseLog.Tests` from the repository root.
The test invokes the actual HTTP response methods on a private loopback listener
and checks that image data, arbitrary tool text, JSON-RPC errors and IDs remain
unchanged on the wire while only response byte counts enter the local log. It
does not start GMod or test other logging paths. Use an isolated artifacts
directory, because the test appends to the test executable's `gterm.log`.
