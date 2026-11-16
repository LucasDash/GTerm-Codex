# Dedicated target checks

Build GTerm, then run this no-dependency test executable with the absolute
directory containing the built `GTerm.dll`:

```powershell
dotnet build GTerm.csproj -c Release
dotnet run --project tests/DedicatedTarget.Tests/DedicatedTarget.Tests.csproj -c Release -- `
  (Resolve-Path ./bin/Release/net10.0).Path
```

The test exercises multiple simultaneous CLI flags, case-preserving values,
explicit target validation and no-fallback behavior, and framed TCP commands on
an ephemeral loopback port. It creates a unique temporary fake server layout;
it does not start GTerm, Garry's Mod, or a dedicated server.
