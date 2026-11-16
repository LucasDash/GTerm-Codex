# Windows named-pipe module integration test

This harness loads the candidate x64 module in a disposable process with a
no-op Tier0 test stub, then exercises the actual named-pipe worker. It does
not load GMod binaries, inject into a running game, or restart a server.

Build from a Visual Studio Developer PowerShell or with MSBuild configured for
the installed Visual C++ toolset:

```powershell
MSBuild.exe .\ModuleTransport.Tests.vcxproj /t:Build /p:Configuration=Release /p:Platform=x64 /m
MSBuild.exe .\Tier0Stub.vcxproj /t:Build /p:Configuration=Release /p:Platform=x64 /m
```

Run with this test directory as the runtime root and the candidate module's
output directory:

```powershell
& .\bin\x64\Release\ModuleTransport.Tests.exe `
  (Get-Location).Path `
  'D:\path\to\gterm_module\module\projects\windows\vs2022\x86_64\Release'
```

The harness checks that the current account can connect, that a restricted
same-user token is denied by the pipe DACL, that the pipe server PID is the
module host, that zero-length frames and idle connections remain connected,
that partial and oversized frames disconnect cleanly, that clients can
reconnect, and that module shutdown/reopen completes in the same process.
It also checks actual framed output from the stub logging callback, a client
that stops reading, and concurrent logging while clients disconnect.

It deliberately does not submit a console command: the test stub has no
initialized GMod engine interface, so `IVEngineServer::ServerCommand`
behavior must be tested in an isolated GMod server. It also does not exercise
the real Tier0 logging exports or engine command execution. Run the managed
listener test as well to cover C# server-PID rejection and bidirectional
framing:

```powershell
dotnet run --project .\ModuleTransport.Managed.Tests.csproj -c Release
```

Neither test alone establishes full gameplay/runtime compatibility.
