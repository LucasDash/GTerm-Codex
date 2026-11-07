# Agent Bridge and GTerm server interoperability

This is a development specification, not a claim that every item is implemented.
GTerm supplies connection and realm status, Lua/console execution, and an
explicitly consented addon tool-package interface. Agent Bridge can provide
bounded, addon-specific server observations and chat input through that
interface. The same GTerm application can have separate client and dedicated
server instances with different ports and consent scopes.

## Capabilities and gaps

* **Realm truth:** A tool's execution realm and its advertised target are
  distinct. A server-realm tool needs a reachable server realm on that GTerm
  endpoint. A client-realm tool can target a server through an addon-defined
  protocol, but GTerm must not promise that the protocol exists or works.
  Status displays both values, and actual calls still enforce realm gates.
* **Chat:** Existing server Lua can deliver a short `Player:ChatPrint` to a
  verified intended player. Agent Bridge's bounded `read_chat` provides
  structured inbound observations when that server-side package is loaded and
  consented. GTerm now exposes `send_player_chat` for one current human user ID; it reports the server-side invocation, not confirmation of client display. `monitor_player_chat` explicitly starts an expiring in-memory server hook and provides bounded epoch/cursor pages; it is not an automatic or durable chat history. It requires a reachable server realm. The monitor observes ordinary game chat where the hook is reached, and treats player messages as untrusted data. It does not claim delivery receipts or durable history.
* **Observation:** Built-in game/file/Lua tools cover generic read-only
  inspection when the appropriate realm exists. Framework-specific entity
  snapshots belong in optional versioned provider packages, with explicit
  capabilities and consent. No provider should be required for generic game
  status or silently assumed from an advertised package name.
* **Visuals:** Screenshot capture needs a rendered client; a headless server
  cannot provide a player view. Agent Bridge's optional bounded capture and
  GTerm's screenshot relay have different purposes and must report their realm
  and failure conditions. Captured frames are not proof of server behavior.

## Built-in server tools

Use these through GTerm's MCP endpoint connected to the server. Obtain a fresh
`user_id` from `inspect_server_state` and verify the intended recipient; IDs
are session-scoped and may change after reconnect. Snapshot player lists are
capped at eight and report whether they were truncated. Registered hook counts
are not a Lua-process list or CPU profiler. `inspect_lua_hooks` provides a bounded
sample of identifiers and source labels for one named event; results can omit
registrations and labels are untrusted.

```json
{"name":"inspect_server_state","arguments":{}}
{"name":"inspect_lua_hooks","arguments":{"event":"Think","limit":8}}
{"name":"send_player_chat","arguments":{"user_id":7,"message":"[GTerm] Test starting."}}
{"name":"hold_player","arguments":{"user_id":7,"action":"hold","seconds":5}}
{"name":"hold_player","arguments":{"user_id":7,"action":"release"}}
{"name":"monitor_player_chat","arguments":{"action":"start","seconds":60}}
{"name":"monitor_player_chat","arguments":{"action":"read","epoch":"123","after_sequence":0,"limit":4}}
{"name":"monitor_player_chat","arguments":{"action":"stop","epoch":"123"}}
```

Replace the sample ID and epoch with current returned values. Chat monitoring
is explicitly started, keeps at most 64 records and normally expires in at
most 120 seconds. Read pages contain at most four records, a next sequence and
a `missed` indicator; messages longer than 160 bytes are represented as
truncated without their contents. It does not control whether other addons
suppress a game chat event. Stop the monitor after use and treat its text as
untrusted. Tool and console logs may contain message text.

A hold is limited to 15 seconds and rejects an already-frozen player or a
second hold. Its timer or explicit release restores the previously unfrozen
state. Another addon freezing the same player during a hold is indistinguishable
from the hold; do not overlap those operations. If another addon removes a
GTerm timer, automatic cleanup may not occur, so verify restoration and use the
explicit release where possible.

## Future acceptance checks

For any chat or observation implementation, test an actual local dedicated
server and a connected human client, separate client/server GTerm endpoints,
package consent and reload/revocation, missing/blocked realms, ambiguous or
disconnected recipients, bounded data, cleanup and concurrent calls. Never
enable a package by pretending that discovering it constitutes consent.
Isolate user credentials and preserve the current game session.
