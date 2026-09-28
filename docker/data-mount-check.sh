#!/bin/bash
# R1: shared-data mount policy for the Lean devcontainer.
#
# Canonical data source (board owner): the CIFS share from orangenas
# (//orangenas/DockerShared, vers=3.1.1), bind-mounted read-only:
#   host:       /opt/data/docker-shared          (Data root: .../QuantConnect/Data)
#   container:  /data                            (Data root: /data/QuantConnect/Data)
#
# Policy: data comes from this mount ONLY - never fetch, never re-create,
# never bake substitutes into the repo. If this check fails: mount the share
# and retry. No fallback fetching, no synthetic data.
#
# Verification order per candidate data root:
#   1. listed in the mount table (/proc/self/mountinfo) or mountpoint(1) -> OK
#   2. otherwise, if present: the CIFS/bind share always arrives READ-ONLY;
#      a writable directory means someone baked or copied data in place -> FAIL
#
# Exits 1 with a clear message when no valid mount is found.
set -u

CANDIDATES=("/opt/data/docker-shared/QuantConnect/Data" "/data/QuantConnect/Data")
if [ -n "${LEAN_DATA_ROOT:-}" ]; then
    CANDIDATES=("$LEAN_DATA_ROOT")
fi

EXPECTED="//orangenas/DockerShared (vers=3.1.1) bind-mounted read-only at /opt/data/docker-shared (host) or /data (container)"

is_mount_entry() {
    # true if $1's directory (or an ancestor bind landing exactly on it) is in the mount table
    local path="$1"
    command -v mountpoint >/dev/null 2>&1 && mountpoint -q "$path" && return 0
    grep -Fq " $path " /proc/self/mountinfo 2>/dev/null && return 0
    return 1
}

is_readonly() {
    # the share mounts ro; a writable data dir means a baked/copy substitute
    local dir="$1" probe="${dir}/.mount-check-probe.$$"
    if touch "$probe" 2>/dev/null; then
        rm -f "$probe" 2>/dev/null
        return 1   # writable -> not the (read-only) shared mount
    fi
    return 0       # not writable -> consistent with ro bind/CIFS mount
}

for DATA_ROOT in "${CANDIDATES[@]}"; do
    [ -d "$DATA_ROOT" ] || continue
    # non-empty (first entry only; cheap over CIFS)
    [ -n "$(find "$DATA_ROOT" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ] || continue
    [ -f "$DATA_ROOT/market-hours/market-hours-database.json" ] || continue
    [ -f "$DATA_ROOT/symbol-properties/symbol-properties-database.csv" ] || continue

    if is_mount_entry "$DATA_ROOT" || is_mount_entry "$(dirname "$(dirname "$DATA_ROOT")")"; then
        echo "data-mount-check: OK - canonical data mount present at ${DATA_ROOT}"
        exit 0
    fi

    if is_readonly "$DATA_ROOT"; then
        echo "data-mount-check: OK - ${DATA_ROOT} is a read-only bind of the shared dataset (mount table not visible from this namespace)"
        exit 0
    fi

    echo "data-mount-check: FAIL - ${DATA_ROOT} exists but is WRITABLE: that is not the shared mount (baked/copied substitute)" >&2
    echo "data-mount-check: expected ${EXPECTED}" >&2
    echo "data-mount-check: policy: do NOT work around this - mount the share and retry." >&2
    exit 1
done

echo "data-mount-check: FAIL - canonical data mount not found (checked: ${CANDIDATES[*]})" >&2
echo "data-mount-check: expected ${EXPECTED}" >&2
echo "data-mount-check: policy: do NOT work around this - mount the share and retry. No fallback fetching, no synthetic data." >&2
exit 1
