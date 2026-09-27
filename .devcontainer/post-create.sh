#!/bin/bash
# postCreate for the image-only devcontainer (.devcontainer/devcontainer.json).
# Order matters (R1 policy): the shared-data mount check runs FIRST and hard
# fails before anything publishes or runs. Data comes exclusively from the
# orangenas CIFS share; there is no fetch/re-create fallback.
set -euo pipefail

# R1: canonical data mount must exist before anything else.
bash /Lean/docker/data-mount-check.sh

cd /Lean

# NETSDK1152: DownloaderDataProvider and Optimizer.Launcher both emit an identical
# config.example.json; SDK 10 aborts the publish unless duplicates are tolerated.
dotnet publish Launcher/QuantConnect.Lean.Launcher.csproj -c Release -o /Lean/Bin \
  -p:ErrorOnDuplicatePublishOutputFiles=false -v minimal

# R2: results go to the pre-existing /opt/data/workspace/lean-storage (bind-mounted
# at /lean-storage by devcontainer.json); no mkdir fallbacks here or anywhere.
# R3: run with Launcher/config.json (the canonical config) + CLI path overrides only.
# R4: credentials arrive via containerEnv and are passed as CLI args by docker/run-backtest.sh.
echo "post-create: published to /Lean/Bin; run with:"
echo "  bash /Lean/docker/run-backtest.sh"
