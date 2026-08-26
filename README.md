### About this fork

A fork of [vnavmesh](https://github.com/awgil/ffxiv_navmesh) where I am trying to make the
movement look less like a machine drove it. Upstream aims straight at the next waypoint and
turns at whatever rate the character allows, so a route comes out as exact lines joined by
instant corners.

What is here so far:

- a trajectory recorder, which gives you a route to walk and captures the run, so human and
  agent movement on the same route can actually be compared
- a steering layer that follows the path by aiming ahead of itself and giving way to nearby
  geometry, instead of pointing at the next waypoint
- greedy line of sight straightening for flying paths, which upstream leaves as raw voxel
  centres
- a tuned flying pathfind, which finds the same routes 15 to 20 times faster on average
  and cuts the worst case from about thirteen seconds to under one

Everything the fork adds is off by default, and it is a drop-in replacement: same internal
name, same IPC, so other plugins see it as vnavmesh. That also means it cannot be installed
alongside the original.

The reasoning behind each decision is in [docs/adr](docs/adr). What is known to be broken is
in [docs/backlog.md](docs/backlog.md).

### Installing this fork

On a XIV on Mac setup, `./scripts/install.sh` builds and installs over the existing vnavmesh,
backing it up first. `--help` lists the rest, including a dev mode where `dotnet build` is the
whole deploy step. Anywhere else, build the project and copy the output into your plugin
folder yourself.

### Installing upstream

- Run `/xlplugins`
- Click on `Settings`
- Go to the `Experimental` tab
- Scroll down to the `Custom Plugin Repositories` section
- Paste `https://puni.sh/api/repository/veyn` in the empty text box at the bottom
- Press the plus button
- Press the save button (floppy disk icon in the bottom right)
- Update plugins
