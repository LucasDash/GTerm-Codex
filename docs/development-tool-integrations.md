# Development tool integration notes

These tools are optional. A source tree or subscribed Workshop item is not
proof that the dedicated server has mounted and initialized it.

## EEL, ULX and ULib

[EEL](https://github.com/Nak2/Eel) supplies developer commands, visual entity
inspection and Lua execution. Its documented permission interface is CAMI;
its execution commands default to superadmin. The local EEL source includes
CAMI. It can be used alongside GTerm as a separate development tool, but
GTerm and Veejay do not call an EEL API or require it to run.

[ULX](https://github.com/TeamUlysses/ulx) is an admin addon and requires
[ULib](https://github.com/TeamUlysses/ulib). Their upstream Workshop IDs are
`557962280` and `557962238`, respectively. Declare both as server items or
use a collection containing both. EEL's Workshop ID is `3318985488`. These
IDs are examples, not automatically enabled dependencies. Availability,
version, server mounting, CAMI permissions and actual interaction with GTerm
must be checked on the intended server after startup.

ULib includes a [CAMI bridge](https://github.com/TeamUlysses/ulib/blob/master/lua/ulib/shared/cami_ulib.lua),
which is the integration point EEL expects. Dash Reality currently gates its
server controls through host/admin/superadmin checks rather than registering
individual CAMI privileges. Whether a particular ULX group satisfies those
checks must be verified in the running server.

GTerm's server tools use their own connection and package-consent boundaries;
they do not create ULX privileges or automatically inherit a player's CAMI
permissions. A future player-facing integration should use CAMI's supported
callbacks and keep those permissions separate from GTerm's transport access.

## Other local references

The supplied DevTools snapshot provides targeted entity measurements and
clipboard helpers. Dash Reality already has an Entity Inspector, and GTerm has
bounded entity, player and hook inspection; extend those when a specific
consumer needs a missing measurement.

The supplied Perfopus snapshot times Lua hooks, timers and entity methods; it
also depends on CONV and broadly instruments runtime callbacks. GLUAWATCH
times callbacks too, but its hook wrapper returns only one value and stopping
its entity watcher does not restore previously wrapped methods. Treat both as
optional diagnostic references. Prefer bounded measurements in code that owns
the relevant callback, preserving arguments, return values and lifecycle.
Dash Reality already has an optional Mod Workbench frame-delivery bridge.

The next practical checks are a real Workshop server/client startup using an
explicit manifest, a live GMod screenshot after the Windows image change, and
controlled profiling of a named Veejay or Dash Reality workload. Offline
fixtures alone cannot establish addon compatibility or whole-game FPS.
