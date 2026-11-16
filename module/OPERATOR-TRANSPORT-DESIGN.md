# Operator transport: proposed security boundary

Status: design only, 2026-11-16. The current TCP protocol does **not**
authenticate either peer and grants a connected local process access to the
game console. Do not treat an MCP secret, a single GTerm instance or the
loopback address as protection for this module. No implementation or change to
the live server is included in this document.

The source baseline is GTerm-Codex `2449c028429e71d85e281d70224d67a2aa938d04`;
the most recent commit affecting `module/source/main.cpp` on that baseline is
`4cc91fa`. The server accepts one TCP client, publishes its socket to the log
listener, and executes incoming framed strings as commands. The client declares
itself connected as soon as TCP connects. On that baseline, both sides allocate
from a peer's 32-bit frame length without an upper bound. The local worktree
candidate adds incoming frame limits but does not authenticate the connection.
A peer that connects and remains
idle can also occupy the only accepted connection.

## Desired properties

- A configured secure transport must reject unauthenticated peers, authenticate
  the server to the client and the client to the server before forwarding logs
  or commands, and never fall back to legacy TCP after a failure.
- Do not expose credentials in the command line, normal logs, source control,
  or world-readable files. Protect persisted configuration using operating
  system permissions, and document the account and administrator trust model.
- Use vetted operating system or library primitives for authentication, random
  values, integrity and encryption. Do not implement cryptographic primitives
  locally. For a network design, defend the whole session against active relay,
  injection, replay and truncation, not just the initial exchange.
- Bound handshake time and message sizes on both peers before allocation. Close
  incomplete, oversized or invalid messages and keep reconnect and shutdown
  responsive. A peer must not monopolize the accept loop indefinitely.
- Keep the current TCP behavior for existing unconfigured installations until
  the project explicitly adopts a new default. Display its unauthenticated
  status and never describe the legacy connection as secure.

## Candidate approaches

An operating-system-protected local transport is the first candidate: Windows
named pipes with an explicit DACL and appropriate first-instance protections;
Unix-domain sockets in an owner-controlled directory, with peer credentials
checked where the platform supports them. The server and client must verify
the peer identity before exposing the transport. Windows and Unix have distinct
ownership and peer-verification APIs; test each supported platform independently.
Processes running as the permitted OS identity and administrators remain trusted.
Endpoint squatting, permissions on parent directories, symlinks, stale endpoints,
reconnects and a client connecting to a fake server all need explicit handling.

An alternative is a mutually authenticated transport built with a maintained
TLS library and protected per-installation credentials. Both endpoints must
verify identity and enforce protocol versions. Key provisioning, rotation and
recovery must be designed and tested. A plaintext password sent on loopback,
an unauthenticated server challenge, or one proof followed by unprotected frames
does not meet the active-local-attacker boundary.

The Windows-first lab can evaluate the first option in an isolated server, but
shipping a cross-platform GTerm build requires working equivalents or a clearly
advertised unsupported state on the other platforms. Silently switching to
legacy TCP would defeat the requested protection.

## Required validation before deployment

1. Compile matching module and GTerm binaries using their documented
   dependencies, in addition to unit and cross-language protocol tests.
2. Exercise allowed and denied OS identities, fake servers, malformed and
   partial requests, oversized frames, an idle peer, multiple simultaneous
   connections, abrupt disconnects, shutdown, restart and reconnect.
3. On an isolated GMod server, show a permitted GTerm receives logs and runs a
   harmless command; show an unpermitted process cannot read logs or execute
   a harmless command. Check both endpoint ownership and access from a
   separate OS identity. Keep secrets and logs out of the repository.
4. Verify legacy mode separately and confirm all secure-mode mismatches fail
   closed. Only then consider the live server with backups and matching module
   and client versions.

The present shell has .NET and a GCC executable, but `premake5`, MSVC `cl` and
`GARRYSMOD_COMMON` were not available on PATH/in the environment during this
review. That observation is not an exhaustive search for installed toolchains.
It is not evidence that a native module was compiled or tested.
