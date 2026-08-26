# 0009. Pathfind progress is echoed to chat

## Status

Accepted.

## Context

A pathfind runs off the game thread and takes anywhere from a few milliseconds to
several seconds (ADR 0008 has the numbers). While it runs the character stands still, so
"is it thinking or is it stuck" is a real question, and upstream answers it poorly:

- the DTR bar says `Mesh: Pathfinding` while a query is in flight, with no destination
  and no timing, and it says nothing at all once the query ends
- the plugin log has both, but reading it means keeping `/xllog` open next to the game
- a query started over IPC by another plugin shows up in neither place in any useful
  form, and that is exactly the case where nothing else on screen explains the pause

Timings are also the thing this fork keeps needing. Tuning the volume search was done
against measurements taken outside the game, and there was no way to see, while playing,
whether a route in a real zone was fast or was one of the slow ones.

## Decision

Echo two lines into the chat log per query, at seam A, `NavmeshManager.QueryPath`:

```
[vnavmesh] searching for flight path to 123.4, 5.6, -78.9
[vnavmesh] done in 0.412 seconds
```

The second line is `cancelled` when the query's token was signalled (a zone change, a
mesh reload, an external cancel) and `failed` otherwise, with the reason left to the log.

The lines are printed through `IChatGui.Print`, which writes into the local chat log
only. Nothing is sent to the server, and nobody else sees them.

The first line goes out when the search actually starts, not when it was queued. Queries
are serialised behind a single worker, so time spent queued is not pathfinding cost, and
starting the pair late keeps the two lines of one query next to each other rather than
interleaved with another query's.

Off by default, as `Humanizer.PathfindChatEnabled`, per ADR 0002.

The message text lives in `PathfindChat`, which is pure and is tested outside the game;
`PathfindAnnouncer` holds the Dalamud side and decides when each line goes out. Numbers
are formatted with the invariant culture: on a locale with a comma decimal separator, a
coordinate triple would otherwise read as six comma-separated numbers.

## Consequences

- `NavmeshManager` gains two lines, and the announcer's disposal is what reports a query
  that ended without a result, so no path out of the query is silent.
- Pathfinds started over IPC announce themselves too. That is the point, and it is also
  the risk: a plugin that re-paths every few seconds will fill the chat log. Off by
  default covers it, and the switch is one checkbox in the config tab.
- `failed` in chat carries no reason. The exception is in the log, and a failed move
  request already prints its own message through `Plugin.DuoLog`.
- The timing is the search only. A query that waited behind a mesh build reports the
  search, and the wait is invisible, which is the honest number for "was this route
  slow" but not for "why did the character stand there".
