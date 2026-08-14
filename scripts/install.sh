#!/usr/bin/env bash
#
# Install this fork over the vnavmesh plugin in a local XIV on Mac setup.
#
# This fork is a drop-in replacement (ADR 0001): it keeps the InternalName
# "vnavmesh", so it has to take over the existing install rather than sit next
# to it. Upstream is backed up once, before the first overwrite, and can be put
# back with --restore.
#
#   ./scripts/install.sh              build Debug and install
#   ./scripts/install.sh --release    build Release and install
#   ./scripts/install.sh --no-build   install whatever is already built
#   ./scripts/install.sh --dry-run    print what would happen, change nothing
#   ./scripts/install.sh --status     show what is currently installed
#   ./scripts/install.sh --restore    put the backed up upstream build back
#
# Dev mode installs to devPlugins instead and registers it with dalamud, which
# gives hot reloading: a rebuild alone makes dalamud reload the plugin, no game
# restart. The normal install is moved aside while dev mode is active, because two
# copies would both try to register the vnavmesh IPC names.
#
#   ./scripts/install.sh --dev        build and install as a dev plugin
#   ./scripts/install.sh --dev-remove unregister and put the normal install back
#
# Override the setup location with XOM_ROOT=/some/path.

set -euo pipefail

PLUGIN="vnavmesh"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
XOM_ROOT="${XOM_ROOT:-$HOME/Library/Application Support/XIV on Mac}"

CONFIG="Debug"
ACTION="install"
DRY=0
BUILD=1
FORCE=0

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }
run() {
    if [ "$DRY" -eq 1 ]; then
        printf '  would: %s\n' "$*"
    else
        "$@"
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        --release) CONFIG="Release" ;;
        --debug) CONFIG="Debug" ;;
        --no-build) BUILD=0 ;;
        --dry-run) DRY=1 ;;
        --status) ACTION="status" ;;
        --restore) ACTION="restore" ;;
        --dev) ACTION="dev" ;;
        --dev-remove) ACTION="dev-remove" ;;
        --force) FORCE=1 ;;
        -h|--help) sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
    shift
done

BUILD_DIR="$REPO_ROOT/$PLUGIN/bin/$CONFIG"
PLUGINS_DIR="$XOM_ROOT/installedPlugins/$PLUGIN"
BACKUP_DIR="$XOM_ROOT/$PLUGIN-upstream-backup"   # deliberately outside installedPlugins, so dalamud does not try to load it
CONFIG_DIR="$XOM_ROOT/pluginConfigs/$PLUGIN"
DEV_DIR="$XOM_ROOT/devPlugins/$PLUGIN"
DISABLED_DIR="$XOM_ROOT/$PLUGIN-installed-disabled"   # normal install parked here while dev mode is on
DALAMUD_CFG="$XOM_ROOT/dalamudConfig.json"

# wine maps the mac filesystem onto Z:, and dalamud stores windows-shaped paths
win_path() { printf 'Z:%s' "$(printf '%s' "$1" | tr '/' '\\')"; }

# parse rather than grep: the config stores the path json-escaped, with doubled backslashes
dev_registered() {
    [ -f "$DALAMUD_CFG" ] || return 1
    python3 - "$DALAMUD_CFG" "$(win_path "$DEV_DIR/$PLUGIN.dll")" <<'DEVCHECK'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(1)
locs = (d.get("DevPluginLoadLocations") or {}).get("$values") or []
hit = next((v for v in locs if v.get("Path") == sys.argv[2] and v.get("IsEnabled")), None)
reload_on = (d.get("DevPluginSettings") or {}).get(sys.argv[2], {}).get("AutomaticReloading")
sys.exit(0 if hit and reload_on else 1)
DEVCHECK
}

[ -d "$XOM_ROOT" ] || die "XIV on Mac setup not found at: $XOM_ROOT (set XOM_ROOT to override)"

# the game holds the plugin dlls open, so swapping files under it corrupts the install
assert_game_stopped() {
    if pgrep -f "ffxiv_dx11" >/dev/null 2>&1; then
        [ "$FORCE" -eq 1 ] || die "FFXIV looks like it is running - quit the game first (or pass --force)"
        info "warning: FFXIV appears to be running, continuing because of --force"
    fi
}

manifest_version() {
    local manifest="$1"
    [ -f "$manifest" ] || die "no manifest at $manifest"
    sed -n 's/.*"AssemblyVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -1
}

installed_versions() {
    [ -d "$PLUGINS_DIR" ] || return 0
    find "$PLUGINS_DIR" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; | sort
}

do_status() {
    info "setup:      $XOM_ROOT"
    info "build dir:  $BUILD_DIR"
    if [ -f "$BUILD_DIR/$PLUGIN.dll" ]; then
        info "  built:    $(manifest_version "$BUILD_DIR/$PLUGIN.json") ($(date -r "$BUILD_DIR/$PLUGIN.dll" '+%Y-%m-%d %H:%M'))"
    else
        info "  built:    (nothing built yet)"
    fi

    local versions
    versions="$(installed_versions)"
    if [ -z "$versions" ]; then
        info "installed:  (none)"
    else
        info "installed:  $PLUGINS_DIR"
        local v
        for v in $versions; do
            info "  $v      $(date -r "$PLUGINS_DIR/$v/$PLUGIN.dll" '+%Y-%m-%d %H:%M' 2>/dev/null || echo 'no dll')"
        done
    fi

    if [ -d "$BACKUP_DIR" ]; then
        info "backup:     $BACKUP_DIR ($(installed_backup_versions | tr '\n' ' '))"
    else
        info "backup:     (none yet - first install will make one)"
    fi

    if [ -d "$DEV_DIR" ]; then
        info "dev:        $DEV_DIR ($(date -r "$DEV_DIR/$PLUGIN.dll" '+%Y-%m-%d %H:%M' 2>/dev/null || echo 'no dll'))"
        if dev_registered; then
            info "  registered with dalamud, hot reload on"
        else
            info "  NOT registered with dalamud"
        fi
    else
        info "dev:        (not installed)"
    fi
    [ -d "$DISABLED_DIR" ] && info "parked:     $DISABLED_DIR"

    info "config:     $CONFIG_DIR"
    [ -d "$CONFIG_DIR" ] && info "  contents: $(ls "$CONFIG_DIR" | tr '\n' ' ')"
}

installed_backup_versions() {
    [ -d "$BACKUP_DIR" ] || return 0
    find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; | sort
}

do_install() {
    assert_game_stopped

    if [ "$BUILD" -eq 1 ]; then
        info "building $CONFIG..."
        run dotnet build "$REPO_ROOT/$PLUGIN/$PLUGIN.csproj" -c "$CONFIG" -v q --nologo
    fi

    [ -f "$BUILD_DIR/$PLUGIN.dll" ] || die "no build output at $BUILD_DIR/$PLUGIN.dll"
    [ -f "$BUILD_DIR/$PLUGIN.json" ] || die "no manifest at $BUILD_DIR/$PLUGIN.json"

    local version target
    version="$(manifest_version "$BUILD_DIR/$PLUGIN.json")"
    [ -n "$version" ] || die "could not read AssemblyVersion from the built manifest"
    target="$PLUGINS_DIR/$version"

    local existing
    existing="$(installed_versions)"
    local count
    count="$(printf '%s' "$existing" | grep -c . || true)"
    if [ "$count" -gt 1 ]; then
        info "warning: several installed versions ($(echo $existing)), dalamud may not load the one being written"
    fi

    # back up pristine upstream exactly once, so --restore always has something real to go back to
    if [ ! -d "$BACKUP_DIR" ] && [ -d "$PLUGINS_DIR" ]; then
        info "backing up upstream to $BACKUP_DIR"
        run mkdir -p "$BACKUP_DIR"
        local v
        for v in $existing; do
            run cp -R "$PLUGINS_DIR/$v" "$BACKUP_DIR/$v"
        done
    elif [ -d "$BACKUP_DIR" ]; then
        info "backup already exists, leaving it alone ($BACKUP_DIR)"
    fi

    info "installing $version -> $target"
    run mkdir -p "$target"
    # clear first so files dropped between builds do not linger
    if [ "$DRY" -eq 1 ]; then
        printf '  would: clear %s and copy %s files\n' "$target" "$(find "$BUILD_DIR" -maxdepth 1 -type f | wc -l | tr -d ' ')"
    else
        find "$target" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        find "$BUILD_DIR" -maxdepth 1 -type f -exec cp {} "$target/" \;
    fi

    info ""
    info "done. start the game and check /vnav."
    info "captures and routes will land in: $CONFIG_DIR"
    info "note: a manual 'Update plugins' in dalamud will pull upstream back over this."
}

do_restore() {
    assert_game_stopped
    [ -d "$BACKUP_DIR" ] || die "no backup at $BACKUP_DIR, nothing to restore"

    local v
    for v in $(installed_backup_versions); do
        info "restoring $v"
        run mkdir -p "$PLUGINS_DIR/$v"
        if [ "$DRY" -eq 1 ]; then
            printf '  would: clear %s and copy the backup back\n' "$PLUGINS_DIR/$v"
        else
            find "$PLUGINS_DIR/$v" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
            find "$BACKUP_DIR/$v" -maxdepth 1 -type f -exec cp {} "$PLUGINS_DIR/$v/" \;
        fi
    done
    info "done, upstream is back in place."
}

# edits dalamudConfig.json in place, preserving the $type annotations newtonsoft needs
edit_dalamud_config() {
    local mode="$1" dll="$2"
    python3 - "$DALAMUD_CFG" "$mode" "$dll" <<'PY'
import json, sys, shutil, uuid, os
cfg, mode, dll = sys.argv[1], sys.argv[2], sys.argv[3]
bak = cfg + ".bak"
if not os.path.exists(bak):
    shutil.copy(cfg, bak)
d = json.load(open(cfg))

locs = d.setdefault("DevPluginLoadLocations", {
    "$type": "System.Collections.Generic.List`1[[Dalamud.Configuration.DevPluginLocationSettings, Dalamud]], System.Private.CoreLib",
    "$values": []})
vals = locs.setdefault("$values", [])
settings = d.setdefault("DevPluginSettings", {
    "$type": "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Dalamud.Configuration.Internal.DevPluginSettings, Dalamud]], System.Private.CoreLib"})

existing = next((v for v in vals if v.get("Path") == dll), None)

if mode == "add":
    if existing is None:
        vals.append({
            "$type": "Dalamud.Configuration.DevPluginLocationSettings, Dalamud",
            "Path": dll, "IsEnabled": True, "Nickname": None})
    else:
        existing["IsEnabled"] = True
    prev = settings.get(dll, {})
    settings[dll] = {
        "$type": "Dalamud.Configuration.Internal.DevPluginSettings, Dalamud",
        "StartOnBoot": True,
        "NotifyForErrors": True,
        "AutomaticReloading": True,
        # keep the id dalamud already assigned, so it does not treat this as a new plugin
        "WorkingPluginId": prev.get("WorkingPluginId") or str(uuid.uuid4()),
        "DismissedValidationProblems": {
            "$type": "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib",
            "$values": []},
    }
    d["DevMode"] = True
    print("registered dev plugin location")
else:
    locs["$values"] = [v for v in vals if v.get("Path") != dll]
    settings.pop(dll, None)
    print("unregistered dev plugin location")

json.dump(d, open(cfg, "w"), indent=2)
PY
}

do_dev_install() {
    assert_game_stopped

    if [ "$BUILD" -eq 1 ]; then
        info "building $CONFIG..."
        run dotnet build "$REPO_ROOT/$PLUGIN/$PLUGIN.csproj" -c "$CONFIG" -v q --nologo
    fi
    [ -f "$BUILD_DIR/$PLUGIN.dll" ] || die "no build output at $BUILD_DIR/$PLUGIN.dll"

    # two copies would both register the vnavmesh IPC names, so park the normal install
    if [ -d "$PLUGINS_DIR" ]; then
        if [ -d "$DISABLED_DIR" ]; then
            info "normal install already parked at $DISABLED_DIR, removing the live copy"
            run rm -rf "$PLUGINS_DIR"
        else
            info "parking normal install at $DISABLED_DIR"
            run mv "$PLUGINS_DIR" "$DISABLED_DIR"
        fi
    fi

    info "installing dev plugin -> $DEV_DIR"
    run mkdir -p "$DEV_DIR"
    if [ "$DRY" -eq 1 ]; then
        printf '  would: clear %s and copy %s files
' "$DEV_DIR" "$(find "$BUILD_DIR" -maxdepth 1 -type f | wc -l | tr -d ' ')"
    else
        find "$DEV_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        find "$BUILD_DIR" -maxdepth 1 -type f -exec cp {} "$DEV_DIR/" \;
    fi

    local dll
    dll="$(win_path "$DEV_DIR/$PLUGIN.dll")"
    info "dalamud path: $dll"
    if [ "$DRY" -eq 1 ]; then
        printf '  would: register %s in dalamudConfig.json with AutomaticReloading
' "$dll"
    else
        edit_dalamud_config add "$dll"
    fi

    info ""
    info "done. start the game; the plugin loads from devPlugins with hot reload on."
    info "after this, a plain 'dotnet build' is enough - dalamud picks the rebuild up."
    info "config backed up once at $DALAMUD_CFG.bak"
}

do_dev_remove() {
    assert_game_stopped
    local dll
    dll="$(win_path "$DEV_DIR/$PLUGIN.dll")"

    if [ "$DRY" -eq 1 ]; then
        printf '  would: unregister %s and delete %s
' "$dll" "$DEV_DIR"
    else
        [ -f "$DALAMUD_CFG" ] && edit_dalamud_config remove "$dll"
        rm -rf "$DEV_DIR"
    fi

    if [ -d "$DISABLED_DIR" ] && [ ! -d "$PLUGINS_DIR" ]; then
        info "putting the normal install back"
        run mv "$DISABLED_DIR" "$PLUGINS_DIR"
    fi
    info "done."
}

case "$ACTION" in
    status) do_status ;;
    install) do_install ;;
    restore) do_restore ;;
    dev) do_dev_install ;;
    dev-remove) do_dev_remove ;;
esac
