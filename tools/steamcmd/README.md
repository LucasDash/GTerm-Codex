# Isolated GMod dedicated server via SteamCMD

This Windows helper prepares a separately located GMod dedicated-server
installation (Steam app 4020) for GTerm development. It uses anonymous
SteamCMD login; it does not log into an owner's Steam account, manage Workshop
items, start a server, or install GTerm's binary module.

Supply a signed Valve SteamCMD executable and an empty server directory:

```powershell
./tools/steamcmd/Invoke-GmodServer.ps1 -Action Inspect `
  -SteamCmdPath 'D:\GMod-Agent-Lab\steamcmd\steamcmd.exe' `
  -ServerRoot 'D:\GMod-Agent-Lab\server-x64' -Branch x86-64

./tools/steamcmd/Invoke-GmodServer.ps1 -Action InstallOrUpdate `
  -SteamCmdPath 'D:\GMod-Agent-Lab\steamcmd\steamcmd.exe' `
  -ServerRoot 'D:\GMod-Agent-Lab\server-x64' -Branch x86-64
```

The helper validates that SteamCMD is signed, rejects server paths containing
reparse points and nonempty unmarked destinations, and binds its marker to the
canonical path, app ID and branch. A matching marker permits a later update;
changing branch requires a different empty lab directory. The installer asks
SteamCMD to validate the chosen branch and checks its success message and the
game directory. It does not modify the Steam client installation. Review
SteamCMD's own output if the install fails.

App 4020 is the **dedicated server**; installing its 64-bit branch does not
install or switch the owner's GMod client. The later GTerm server/module
connection, port isolation, startup, and 64-bit client connection each require
their own validation.

For an explicitly managed dedicated server, configure `GmodRoot` as its
absolute installation root and `ModulePort` as the native module's loopback
port in GTerm's Config.json, or use `--gmodroot <path> --moduleport <port>`.
The default module port is 27514; the server module must be configured for
the same port. Set the server process's `GTERM_MODULE_PORT` environment
variable to that decimal port before launching it; an unset variable uses
27514, and an invalid value prevents the module from loading. Set this on the
server process rather than globally when the player client uses the default.
For Windows local IPC, set GTerm's `ModuleTransport` to
`windows_named_pipe` and set `GTERM_MODULE_TRANSPORT=windows_named_pipe` in the
dedicated-server process environment before starting the server. GTerm also
checks that the pipe server PID matches the selected GMod installation. The
pipe DACL grants access to the server process's Windows account and Local
System, and remote pipe clients are rejected. Run GTerm under an account that
can connect to that pipe; processes running as the same account remain within
the trusted boundary. This mode has no TCP fallback. It is not available on
Linux or macOS in this revision.
With an explicit root, GTerm resolves script and relay files
only in that root and disables its automatic module/game-file installer.
Stage the matching native module and server-side loader separately. The
existing Steam client remains on its own installation and default port.

References: [GMod dedicated server documentation](https://wiki.facepunch.com/gmod/Downloading_a_Dedicated_Server)
and [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD).
