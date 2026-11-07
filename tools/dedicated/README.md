# GTerm dedicated development server (Windows)

This uses the existing GTerm application, its server module and SteamCMD; it
does not create another GTerm executable. `Start-GmodDedicated.ps1` prepares
the command line for a **local or private-LAN** x86-64 Sandbox server and can
start it as a separate process. It does not install the GTerm module, start
GTerm, start at login, supervise or restart a crashed server, join a client,
or prove that a requested start became ready. Public hosting, authentication,
firewall rules and unattended supervision require separate setup and testing.

## Requirements

* Windows, PowerShell 7, a Valve-signed `steamcmd.exe` and enough disk space
  for Garry's Mod dedicated server app 4020 and its Workshop content.
* A separate, empty server directory outside the Steam client game directory.
  Install and later update it with the [SteamCMD helper](../steamcmd/README.md).
  **Stop every server that might share files with the target before updating.**
  Distinct directories may have NTFS hard links; updating one may change the
  other. Do not update or validate a server while its files are in use.
* The matching x64 GTerm native module and a server-side loader in that server's
  `garrysmod/lua` tree, installed using the GTerm instructions in the root
  README. An optional Agent Bridge addon and its permissions are separate from
  connecting GTerm. Do not copy credentials or local admin lists into source.
* A separately configured copy of **the same GTerm application** on the same
  host. Its `GmodRoot` must identify this server and its `ModulePort` must
  match the launcher's port. Use distinct game, client, TV, module and MCP
  ports for each server and client GTerm instance. Store the runtime
  `Config.json` outside the repository with a unique `MCPSecret`; do not
  publish it, Steam credentials, GSLTs or server configuration files.

## Install, inspect, start

```powershell
# Use an existing Valve-signed SteamCMD and a new empty directory.
./tools/steamcmd/Invoke-GmodServer.ps1 -Action InstallOrUpdate `
  -SteamCmdPath 'D:\SteamCMD\steamcmd.exe' `
  -ServerRoot 'D:\GModServers\development' -Branch x86-64

# After setting up the GTerm module and loader, inspect without starting.
./tools/dedicated/Start-GmodDedicated.ps1 -Action Inspect `
  -ServerRoot 'D:\GModServers\development'

# Default is loopback only. WhatIf performs checks but starts no server.
./tools/dedicated/Start-GmodDedicated.ps1 -Action Start `
  -ServerRoot 'D:\GModServers\development' -WhatIf

# For a private LAN, use an IPv4 address assigned to this machine.
./tools/dedicated/Start-GmodDedicated.ps1 -Action Start `
  -ServerRoot 'D:\GModServers\development' `
  -BindAddress '192.168.1.10' -GamePort 28115 -ModulePort 27524 `
  -WorkshopCollection '123456789'
```

Use your own local address and collection ID. Omit `WorkshopCollection` to
start without one. The launcher validates inputs, checks common UDP/TCP port
conflicts, refuses an already-running server from the same root and reports a
process ID with `Ready = false`; it cannot rule out other processes racing
the checks. Verify server output, map, game state, GTerm connection and actual
client access separately. A local `sv_lan 1` server is not a public server.

GTerm and its module communicate over loopback on the same host. Configure
the dedicated GTerm process to use this server's root and module port, and
choose a different MCP port from the client's GTerm. GTerm's MCP can execute
server Lua: keep it local, use a unique secret, and grant addon-provided tools
only deliberately through GTerm's package-consent flow. Its logs can include
commands and request bodies, so do not put passwords or other secrets in tool
commands or publish runtime logs.

## Workshop and CI

`+host_workshop_collection <ID>` downloads and mounts a public or unlisted
collection when the server starts; availability and its contents must be
verified. For an explicit item list, GMod 2026.09.22 and later can read
`garrysmod/cfg/srcds_workshop_ids.txt` using its documented KeyValues format:

```text
"my_workshop_addons"
{
    "1" { "wsid" "123456789" }
    "2" { "wsid" "987654321" }
}
```

These are GMod server features; the SteamCMD helper installs or updates app
4020 and does not fetch or execute individual Workshop addons. GTerm's
launcher currently accepts a collection ID; keep an explicit ID file in the
server's `cfg` directory when a collection is not the right fit. Workshop
updates may change test inputs. Set `host_workshop_autoupdate 0` for a pinned
development run, then update and review dependencies deliberately. Avoid
mounting both a published Workshop addon and a loose development copy of the
same addon.

### Generate a dependency bundle

Copy `workshop-manifest.example.json` to a working location and edit it. Keep
IDs as decimal **strings**: `server_collection` is one collection ID or null,
`server_items` lists individual server addons, and `client_content` lists only
individual items whose custom assets clients need. The same item may appear in
both lists. ULX, for example, requires ULib; declare both as server items if
using them outside a collection. Do not add Lua-only addons to client content.
The separate `workshop-manifest.admin-dev.example.json` names EEL, ULib and
ULX as an optional development profile. Review and copy it when needed; it is
never enabled automatically.

```powershell
pwsh -NoProfile -File ./tools/dedicated/New-GmodWorkshopBundle.ps1 `
  -ManifestPath 'D:\GModServers\development-dependencies.json' `
  -OutputDirectory 'D:\GModServers\reviewed-workshop-bundle'
```

The output directory must be new and its parent must exist. The command checks
the schema, ID syntax, duplicates and size before writing. It generates
`workshop-plan.json`, a native server ID file when `server_items` is nonempty,
and a separate server autorun file for `client_content` when that list is
nonempty. Review the bundle and reconcile it with any existing server files
before copying it to the server root. Empty lists produce no corresponding
file and do not remove previously installed declarations. Pass the optional
collection ID from the plan to the launcher's `-WorkshopCollection` option.
Generating a bundle does not resolve dependencies, download, install, mount or
verify any item. Include required dependencies explicitly.

Server mounting and client content delivery are separate. `resource.AddWorkshop`
asks joining clients to download a single addon's custom content; it does not
install or mount that addon on the server, does not accept a collection ID,
and does nothing useful for Lua-only addons. The cited Addon Share and
Workshop Downloader examples help build client resource lists from mounted or
selected addons; they do not provide server-side Workshop installation. Their
scanning can inform a review, but broad automatic selection can send unrelated
content. Prefer a small, explicit server collection or ID manifest, then
separately list only the client assets players need. See [Workshop for
Dedicated Servers](https://wiki.facepunch.com/gmod/Workshop_for_Dedicated_Servers),
[`resource.AddWorkshop`](https://wiki.facepunch.com/gmod/resource.AddWorkshop),
and [Serving Content](https://wiki.facepunch.com/gmod/Serving_Content).

The optional third-party [Setup SteamCMD GitHub Action](https://github.com/marketplace/actions/setup-steamcmd)
installs SteamCMD on a GitHub Actions runner; it does not itself install app
4020 or provide a persistent server. A manually triggered CI job can use the
Action and then the anonymous SteamCMD command, with the runner's own disposable
directory. Review and pin third-party action versions as appropriate. Do not
run multi-gigabyte server downloads on every push or upload server files,
Workshop content, runtime configuration or secrets as public build artifacts.

## Image processing

Screenshot capture and region processing use ImageSharp. `ScreenshotFrame`
validates JPEG relay size and dimensions before decoding; `ScreenshotCapturer`
handles capture, cropping and enlargement. Keep that validation before decode.
Offline tests do not verify the game-rendered capture path, so check it with a
live client after changing screenshot processing.
## Tests and limitations

Run `pwsh -NoProfile -File ./tools/dedicated/DedicatedServer.Tests.ps1` and
`pwsh -NoProfile -File ./tools/dedicated/WorkshopBundle.Tests.ps1` from the
repository root. Tests use fake process/socket functions and disposable
temporary directories; they do not launch SteamCMD, GTerm or a real server.
A real server/client join, Workshop download, module connection, multiple
players, external networking and unattended recovery remain separate tests.

Official references: [Downloading and Hosting a Dedicated Server](https://wiki.facepunch.com/gmod/Downloading_a_Dedicated_Server)
and [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD).
