# Server operations tests

Run from the repository root with the .NET 10 SDK and Python with `lupa` installed:

```powershell
dotnet run -c Release --project tests/ServerOperations.Tests -- --emit "$env:TEMP/gterm-serverops-generated.json"
python tests/ServerOperations.Tests/run_lua.py "$env:TEMP/gterm-serverops-generated.json"
```

The .NET fixture checks input handling and generates Lua for the Lua 5.1 and
LuaJIT mock environments. These checks do not replace a live dedicated server,
connected-player, consent, or client-rendering test. The generated file is a
temporary test artifact and should not be committed.
