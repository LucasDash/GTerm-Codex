# gmsv_gterm
A Garry's Mod module that provides an interface for external consoles (used by [GTerm](https://github.com/Earu/GTerm)).

The compatibility default exposes the server console over localhost TCP. The
default port is 27514; set `GTERM_MODULE_PORT` to a decimal port from 1 to
65535 and configure GTerm's `ModulePort` to match. GTerm connects to the
module as a client. TCP mode does not authenticate either peer.

On Windows, matching builds can instead use a named pipe. Set the game
process's `GTERM_MODULE_TRANSPORT` to `windows_named_pipe` and GTerm's
`ModuleTransport` to the same value. The pipe is named
`GTerm.Module.<port>`, has a DACL for the game process's account and Local
System, rejects remote clients, and uses `FILE_FLAG_FIRST_PIPE_INSTANCE` to
refuse a pre-existing pipe. GTerm checks that the pipe server PID belongs to a
supported executable in the selected GMod installation before connecting.
There is no fallback to TCP. The OS account running GMod and GTerm is trusted;
other processes running as that same account and local administrators remain
inside the trust boundary. Linux and macOS continue to use TCP in this
revision.

The module rejects command frames over 1 MiB; GTerm rejects incoming log
frames over 8 MiB. An oversized frame closes the connection so the client can
reconnect. In named-pipe mode, log frames are dropped when the 1 MiB pending
queue is full so a client that stops reading does not block engine logging.
These bounds do not authenticate TCP mode.
**This fork provides both x86 and x64 compatible versions.**

## Compiling
### For the x86_64 branch:
#### Building the project for linux/macos
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive --branch=x86-64-support-sourcesdk`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Run `premake5 gmake` in your local copy of **this** repo
4) Navigate to the makefile directory (`cd /projects/linux/gmake` or `cd /projects/macosx/gmake`)
5) Run `make config=releasewithsymbols_x86_64`

#### Building the project on windows
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive --branch=x86-64-support-sourcesdk`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Run `premake5 vs2019` in your local copy of **this** repo
4) Navigate to the project directory `cd /projects/windows/vs2019`
5) Open the .sln in Visual Studio 2019+
6) Select Release, and either x64 or x86
7) Build


### For the *current (x86)* main branch:
#### Building the project for linux/macos
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Edit premake5.lua and change `PROJECT_GENERATOR_VERSION` to `2`
4) Run `premake5 gmake` in your local copy of **this** repo
5) Navigate to the makefile directory (`cd /projects/linux/gmake` or `cd /projects/macosx/gmake`)
6) Run `make`

#### Building the project on windows
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Run `premake5 vs2019` in your local copy of **this** repo
4) Edit premake5.lua and change `PROJECT_GENERATOR_VERSION` to `2`
5) Navigate to the project directory `cd /projects/windows/vs2019`
6) Open the .sln in Visual Studio 2019+
7) Select Release, and either x64 or x86
8) Build
