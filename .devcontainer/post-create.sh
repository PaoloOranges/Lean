#!/bin/bash
# postCreate for the image-only devcontainer (.devcontainer.json).
# Publishes launcher + algorithm into /Lean/Bin so the devcontainer run command works out of the box.
# Runs inside the container after creation; requires nothing on the host.
set -euo pipefail
cd /Lean

# NETSDK1152: DownloaderDataProvider and Optimizer.Launcher both emit an identical
# config.example.json; SDK 10 aborts the publish unless duplicates are tolerated.
dotnet publish Launcher/QuantConnect.Lean.Launcher.csproj -c Release -o /Lean/Bin \
  -p:ErrorOnDuplicatePublishOutputFiles=false -v minimal

# Make sure backtest results land in the mounted volume (bind-mounted at /lean-storage).
mkdir -p /lean-storage/results
echo "post-create: published to /Lean/Bin; run with:"
echo "  dotnet /Lean/Bin/QuantConnect.Lean.Launcher.dll --config /Lean/config.devcontainer.json"
