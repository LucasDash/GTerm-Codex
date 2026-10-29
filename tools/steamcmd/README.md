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

References: [GMod dedicated server documentation](https://wiki.facepunch.com/gmod/Downloading_a_Dedicated_Server)
and [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD).
