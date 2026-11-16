# Operator transport: Windows local authorization boundary

Status: implemented prototype on `codex/module-auth-socket`, based on `4a47d26`.
The Windows x64 build, transport harness,
managed listener tests, and an isolated GMod and GTerm UI check passed on
2026-11-16. The production server and client were not changed.

## Current behavior

TCP remains the compatibility default and does not authenticate either peer.
It exposes the game console to any process that can connect to the local port.
Do not treat loopback, an MCP secret, or a single GTerm instance as protection
for TCP mode.

Windows can opt into a named pipe, `GTerm.Module.<port>`. The module creates it
with an explicit DACL for the server process account and Local System, rejects
remote clients, and uses `FILE_FLAG_FIRST_PIPE_INSTANCE` to reject a pre-existing
endpoint. GTerm verifies that the pipe server PID belongs to a supported
executable in the selected GMod installation before sending commands. With
automatic selection it compares against the detected GMod executable path.
Multiple processes may share that executable; the check does not distinguish
them. There is no fallback
to TCP if named-pipe mode is selected. GTerm sets `GTERM_MODULE_TRANSPORT` and
`GTERM_MODULE_PORT` when it launches GMod; an externally launched server must
inherit matching environment variables. GTerm can also select the transport
with `ModuleTransport` in `Config.json` or `--moduletransport`.

This is operating-system authentication, not a cryptographic handshake. It
protects the local endpoint from other OS accounts and fake pipe servers, but
other processes running as the permitted account and local administrators
remain trusted. The current implementation is Windows-only. Linux and macOS
remain on unauthenticated TCP. Command frames are limited to 1 MiB and log
frames to 8 MiB; the managed client serializes concurrent command writes and
rejects oversized commands before allocating a frame. Named-pipe log writes
use a bounded queue and a separate writer; complete log frames are dropped
when that queue is full, so an operator must not treat logs as lossless.

## Verification evidence

- `dotnet build GTerm.csproj -c Release --no-restore` passed with .NET SDK
  10.0.404. NuGet reported six existing ImageSharp 3.1.12 moderate/high
  advisory warnings; this transport change does not update that dependency.
- `tests/DedicatedTarget.Tests` passed in Release, including config parsing,
  named-pipe selection, no-fallback behavior,
  port validation, framing, and reconnect checks. After the PID selection
  fix, Windows process fixtures confirmed that two processes using the selected
  `bin/win64/gmod.exe` are accepted, while same-named executables in a
  different root or an unexpected nested directory are rejected. These are
  disposable test processes, not a running GMod client.
- `tests/ConfigPreservation.Tests.ps1` passed all eight checks.
- `tests/ModuleTransport.Tests` managed checks passed for wrong-server-PID
  rejection, matching-server-PID acceptance, framed log and command handling,
  oversized-command rejection before writing, and concurrent frame alignment.
- The Windows x64 native module, test harness, and Tier0 stub built with MSVC
  toolset v145. The native harness passed restricted-token DACL denial,
  same-account connection, pipe-server PID identity, idle/zero/partial/oversized
  frames, reconnect, shutdown, and same-process reopen checks. It also checks
  framed log output and a client that stops reading. The harness uses
  a Tier0 test stub, so it does not establish real GMod logging behavior.
- An isolated x64 GMod server with no addons ran on loopback port 28135 using
  named-pipe mode on module port 27587. The pipe reported server PID 19324,
  matching the staged GMod process. A framed `echo GTERM_AUTH_PROBE_OK` command
  sent through that pipe appeared in the server console. A subsequent test
  used the GTerm UI and the final candidate DLL (SHA-256
  `53A86FAC93703BF167987278BFC0E69954D8C4E8FF8424BE8F41F257B9F17AA1`):
  an echo command executed and its timestamped engine log appeared in the UI
  without another command. Earlier synchronous I/O delayed logs until further
  input; the final candidate uses independent overlapped read and write calls.
- The known-good server on `192.168.1.102:28115` remained untouched. TCP mode
  remains covered by the existing loopback listener tests, not a fresh live
  GMod server test in this pass.
- The PID selection fix was exercised by the process fixtures and managed pipe
  listener checks. The isolated GMod UI test above preceded that fix; repeat it
  with a real `bin/win64/gmod.exe` client for end-to-end coverage of the fix.

## Trust and deployment limits

Use named-pipe mode only when GTerm and GMod are on the same Windows host and
run under the intended OS account. A second process under that account is
inside the trust boundary. The implementation does not provide remote access,
per-user credentials, cryptographic confidentiality, or a defense against a
local administrator. Keep TCP mode described as unauthenticated.

Before adopting this as the default or claiming full production validation:

1. Repeat access denial with a separate Windows account, in addition to the
   restricted-token harness.
2. Run the compatibility TCP mode against a real GMod runtime and confirm
   reconnect and shutdown on each supported platform.
3. Review the exact branch diff, including the unrelated ImageSharp advisory
   status, before deciding whether to commit or merge.

For a future remote or cross-platform operator boundary, design a separate
mutually authenticated transport with maintained cryptographic primitives,
protected credential provisioning and rotation, protocol versioning, and
platform-specific peer verification. Do not expand this local pipe into a
network security claim.
