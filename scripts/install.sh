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
        --force) FORCE=1 ;;
        -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
    shift
done

BUILD_DIR="$REPO_ROOT/$PLUGIN/bin/$CONFIG"
PLUGINS_DIR="$XOM_ROOT/installedPlugins/$PLUGIN"
BACKUP_DIR="$XOM_ROOT/$PLUGIN-upstream-backup"   # deliberately outside installedPlugins, so dalamud does not try to load it
CONFIG_DIR="$XOM_ROOT/pluginConfigs/$PLUGIN"

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

case "$ACTION" in
    status) do_status ;;
    install) do_install ;;
    restore) do_restore ;;
esac
