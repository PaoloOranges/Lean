#!/bin/bash
# R3/R4 run path for the Lean devcontainer.
#
# R3: single-source config. Launcher/config.json IS the per-run config - we do
#     NOT keep a divergent copy. Only path settings are overridden on the CLI
#     (all supported by Configuration/LeanArgumentParser.cs):
#       --data-folder                  -> canonical data on the shared mount (R1)
#       --results-destination-folder   -> /lean-storage/results (R2)
#       --algorithm-location           -> published algorithm dll
#       --close-automatically          -> headless run
#     NOTE: object-store-root has no CLI option; Launcher/config.json's
#     "../../../lean-storage" resolves to /lean-storage when run from /Lean.
# R4: QC API credentials come from the container environment (devcontainer.json
#     containerEnv <- host env) and are passed as CLI args. Values are never
#     echoed, logged, or committed.
set -euo pipefail

DATA_MOUNT="${LEAN_DATA_ROOT:-/data/QuantConnect/Data}"

# R1: data must be on the shared mount; no fallbacks.
bash "$(dirname "$0")/data-mount-check.sh"

if [ -z "${QUANTCONNECT_API_USERNAME:-}" ] || [ -z "${QUANTCONNECT_API_TOKEN:-}" ]; then
    echo "run-backtest: QUANTCONNECT_API_USERNAME / QUANTCONNECT_API_TOKEN not set in the environment" >&2
    exit 1
fi

cd /Lean
exec dotnet /Lean/Bin/QuantConnect.Lean.Launcher.dll \
    --config /Lean/Launcher/config.json \
    --data-folder "${DATA_MOUNT}" \
    --results-destination-folder /lean-storage/results \
    --algorithm-location /Lean/Bin/QuantConnect.Algorithm.CSharp.dll \
    --close-automatically=true \
    --job-user-id="${QUANTCONNECT_API_USERNAME}" \
    --api-access-token="${QUANTCONNECT_API_TOKEN}"
