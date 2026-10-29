# Dedicated package discovery tests

Run `dotnet run --project tests/DedicatedPackages.Tests` from the repository root.
The harness exercises the built GTerm assembly through reflection without launching
GMod or reading or writing package consent configuration. Its cases cover local
dedicated versus client/remote discovery, consent scope and MCP discovery realm selection, package name
sanitization, realm filtering for mixed and client-only packages, retention of
the full definition hash, and the console probe length.

A passing run does not establish mounted addon discovery, relay behavior, actual
consent interaction, invocation, or hash mismatch handling in a running server.
Those require a dedicated runtime test.
