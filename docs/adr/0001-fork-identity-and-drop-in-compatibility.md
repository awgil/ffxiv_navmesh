# 0001. Fork identity and drop-in compatibility

## Status

Accepted

## Context

This fork adds human-like movement behaviour on top of vnavmesh. A large ecosystem of
Dalamud plugins already drives vnavmesh through its IPC surface, and the fork is only
useful if those plugins keep working without modification.

Three separate mechanisms tie a consumer to vnavmesh:

- **IPC names.** `IPCProvider.cs:73` registers every entry point as `"vnavmesh." + name`.
  Dalamud IPC is a global string-keyed registry, so ownership of a name belongs to
  whichever plugin registered it, regardless of which assembly that plugin lives in.
- **Plugin manifest.** `vnavmesh.json` declares `InternalName: "vnavmesh"`. Some
  consumers check `IDalamudPluginInterface.InstalledPlugins` for that string rather than
  probing IPC, so IPC parity alone is not sufficient.
- **Shared data.** `FollowPath.cs:39` publishes a `bool[]` under the tag
  `vnav.PathIsRunning` via `GetOrCreateData`.

There is also a cost consideration: `Navmesh.Version` (`Navmesh.cs:15`) gates the
on-disk mesh cache format. Bumping it forces every user to rebuild every cached zone,
which is minutes of CPU per zone.

## Decision

Preserve the entire identity surface:

- Keep the IPC prefix `vnavmesh.` and every existing method name and signature byte for byte.
- Keep `InternalName: "vnavmesh"` in the manifest.
- Keep the shared data tag `vnav.PathIsRunning`.
- Keep the DTR entry name `vnavmesh` and both `/vnav` and `/vnavmesh` commands.

New behaviour is exposed only through **new** IPC methods and **new** config fields.
No existing signature changes meaning, and no existing default changes value.

Do not bump `Navmesh.Version`. Behavioural work does not alter the mesh format, so
users keep their `meshcache` directory intact across the switch.

## Consequences

- Consumers see an identical API and cannot tell the difference until new features are
  explicitly enabled.
- This fork and upstream vnavmesh **cannot be installed at the same time**. They share
  an `InternalName`, so the plugin installer treats them as the same plugin, and they
  would fight over IPC name registration if both loaded. This is intended: the fork
  replaces upstream rather than sitting beside it.
- Testing the two side by side requires two game installs or two Dalamud config sets,
  not two plugins in one install.
- Distribution needs a self-hosted `repo.json`; the upstream puni.sh repository is not
  usable for this.
- Any upstream change to an IPC signature must be mirrored exactly, and is a merge
  conflict worth resolving carefully rather than mechanically.
