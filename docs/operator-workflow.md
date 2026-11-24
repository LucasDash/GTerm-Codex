# GTerm operator workflow

This guide describes the roles and current limits of a portable development
environment. It does not provision a remote host or establish remote GTerm/MCP
access. Keep deployment-specific settings and credentials out of the checkout.

## Components

- **GTerm-Codex** supplies an operator console, a native GMod module and an
  optional MCP interface. The module connects a specific running GMod realm to
  GTerm. Review package prompts in the GTerm console for each scope.
- [**Agent Toolkit for Garry's Mod**](https://github.com/LucasDash/agent-toolkit-gmod)
  supplies the `agent_bridge` addon, its optional GTerm package and a Docker
  dedicated-server deployment. Its server guide owns the deployment procedure.
- A **GMod client** is needed to join, render and test client-side behavior.
  A dedicated server runs independently and cannot provide a player's screen.

## Preparing a machine

For source development, install Git, a .NET 10 SDK and access to these
repositories. Build GTerm with `dotnet build GTerm.sln` or use a matching,
trusted build of the fork. Keep the build's `Config.json`, logs and package
consent local; each executable directory has its own settings. The SDK is not
needed to run a suitable self-contained build.

To play, install Steam and Garry's Mod with a valid account and select the
appropriate game branch. To host the included Docker server, install a working
Docker Engine with Compose and enough storage for SteamCMD, the server, addons
and backups. The current container targets the GMod x86-64 server and installs
its own dedicated-server files through anonymous SteamCMD; the host does not
need a Steam client for that role. Docker Desktop on Windows also needs its
supported virtualization backend. A machine may fill one or several roles.

Follow the Toolkit server guide for environment settings and startup. A local
container stays on its host: it stops if that host shuts down. An independent
server requires another powered machine or a remote host, its own persistent
storage and a reachable game port. Automatic restarts and backups improve
recovery; they do not preserve a live session during a host failure.

## Connecting and validating

For a local GMod installation, use the installation and transport instructions
in the [main README](../README.md) and [module guide](../module/readme.md).
The explicit `GmodRoot` option is for a locally accessible dedicated-server
installation and disables GTerm's automatic game-file and module installation.
Use matching native modules and ports. A TCP connection alone is not proof that
the selected game root or all MCP tools are available.

For the Toolkit Docker deployment, the native module runs in the server
container and the relay is published on the Docker host's loopback interface.
The module TCP connection is unauthenticated. Protect access to the host and
other containers on its Docker network; do not expose the relay or Docker API
to untrusted networks. A local SSH forward can carry the console connection
from a remote host, but it does not supply the server's filesystem to GTerm.
Several MCP operations and package flows use files under the selected GMod root
as a relay. Full remote operation requires a separately designed, validated
way to access the matching files and has not yet been demonstrated.

Before relying on a setup, check the selected target and port, the module
connection, the current game session, and the realms and tools actually
available. Enable `agent_bridge` only after reviewing its GTerm prompt. Package
consent is scoped and can require renewed approval after a package or scope
change. See [package consent in the README](../README.md#mcp-tool-packages-opt-in).

The current Docker game connection has been checked with one local human client
through the host's LAN address. Remote hosting, internet connectivity, multiple
players and recovery on a different host are separate checks.
