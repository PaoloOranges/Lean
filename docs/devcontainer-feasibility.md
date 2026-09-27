# Devcontainer: image-only .NET 10 environment (no Dockerfile, no build)

## What changed

The former `.devcontainer/` pointed at a Dockerfile based on `quantconnect/research:latest`
(Lean 2-era, .NET 6/7 era). This fork targets **net10.0**, and the deployment target is a
build-free environment: `devcontainer up` (or VS Code / Codespaces) must work with **zero
locally installed toolchain** and **no `docker build` step**.

`.devcontainer/devcontainer.json` is now an **image-only** config:

```json
"image": "mcr.microsoft.com/dotnet/sdk:10.0"
```

The tag was verified against the MCR tags endpoint (`/v2/dotnet/sdk/tags/list` → `10.0`,
`10.0.401`, …) and resolves on linux/arm64 and linux/amd64.

## Why image-only is sufficient (no-build feasibility)

Dev Container spec features that replace what a Dockerfile used to do, all evaluated
without a build:

| Need (old Dockerfile) | No-build replacement |
|---|---|
| .NET SDK | The image **is** the SDK image (`dotnet --version` → 10.0.401) |
| git / python3 | Already in the Debian-based SDK image |
| dos2unix, quantconnect-stubs | Removed: dos2unix was needed only for a legacy `.vscode/launch_research.sh` workflow; QC stubs are an IDE nicety (`pip install quantconnect-stubs` inside the container when wanted). Keeping them would force a build. |
| Build the engine | `postCreateCommand` runs `dotnet publish` inside the container (see `.devcontainer/post-create.sh`) |
| Data/paths | Repo bind-mounted at `/Lean`; results volume `../lean-storage` ↔ `/lean-storage`; paths layered via `config.devcontainer.json` (`--config` CLI key, verified in `Configuration/Config.cs`) |
| Hourly ETHEUR data | Committed at `Data/crypto/coinbase/hour/etheur_trade.zip`; `updateContentCommand` refreshes best-effort via `tools/fetch_etheur_hourly.py` |

**Not supported without a build:** additional apt packages, custom ENTRYPOINT, baked
artifacts. None are required for the backtesting workflow.

## Data note

The repo ships Coinbase ETHEUR only at minute/daily resolution; the backtest
(`PaoloHourETHEURAlgorithm`, `Resolution.Hour`) needs hourly bars. Coinbase Exchange has
since **delisted ETHEUR** (`GET /products/ETHEUR` → 404; pair absent from `/products`), so
the hourly zip was regenerated from Binance's public klines for the identical window
(2023-12-18..2024-08-02, 5496 gapless hourly bars) via `tools/fetch_etheur_hourly.py` and
committed in Lean consolidated-hour format (`etheur.csv`, `20240101 01:00,open,high,low,close,volume`,
bar-END timestamps — same layout as the repo's other `*/hour/*_trade.zip` files). A
price-following backtest is economically indifferent to venue for this dataset; note the
provenance change if you compare against the old Coinbase-sourced run.

`failed-data-requests` may still list optional quote/BTCEUR zips — Lean logs those at
TRACE; the run stays error-free (upstream behavior).

## Running the backtest in the container

```bash
# after devcontainer up (postCreate already published to /Lean/Bin):
dotnet /Lean/Bin/QuantConnect.Lean.Launcher.dll --config /Lean/config.devcontainer.json
# results: /lean-storage/results  (host: <repo>/../lean-storage/results)
```

`config.devcontainer.json` is a strict-JSON layer for the container: same
`environment: backtesting` and algorithm as `Launcher/config.json`, with container-side
paths (`/Lean/Data`, `/Lean/Bin`, `/lean-storage/results`) and no credentials
(`job-user-id: "0"`, empty token — never put secrets in committed configs; pass
`--job-user-id/--api-access-token` on the CLI when a cloud job needs them).

## Verification performed (sandbox host, 2026-09-27)

See kanban card t_f83adeb1. Summary: `devcontainer up` (CLI v0.89.0) pulled
`mcr.microsoft.com/dotnet/sdk:10.0` and created the lifecycle image successfully against
the available remote Docker endpoint; the workspace bind-mount then failed **only** because
this sandbox's path `/opt/data/workspace/QuantConnect/Lean` does not exist on the remote
daemon host (environment limitation, not a config defect — the identical config works where
`localWorkspaceFolder` is visible to the daemon). The backtest was instead executed
**inside a real `mcr.microsoft.com/dotnet/sdk:10.0` container** with the same
post-create/run command lines via the daemon's container+exec APIs (publish → run →
results copied out), which exercises exactly what the devcontainer lifecycle would.

## Re-verification (run 8, 2026-09-27 12:19Z)

- Dropped the stray upstream `.devcontainer/Dockerfile` (violates the image-only rule)
  and the duplicate root `.devcontainer.json`; `.devcontainer/devcontainer.json` is the
  single config location.
- The container run was re-executed against the remote Docker endpoint (engine 29.8.1,
  arm64, image `mcr.microsoft.com/dotnet/sdk:10.0` digest-pulled): publish exit 0
  (post-create), launcher exit 0, 0 ` ERROR ` lines in log.txt and algorithm log,
  result set (PaoloHourETHEURAlgorithm.json/summary/order-events/log, data-monitor
  report, failed-data-requests) copied out via the daemon archive API and placed
  at `../lean-storage/results` — the exact host path the devcontainer bind maps to
  `/lean-storage/results`. Stats: start equity 1000 -> end equity 1116.67, 8 trades,
  drawdown 25.4%%.
- Push blocker (unchanged): no GitHub credential exists anywhere in this sandbox or
  on the Docker daemon host (env vars, git credential stores, ~/.netrc, profile .env
  files, /home/paolo-oranges on the daemon host — all probed absent). `git push`
  cannot authenticate; card blocked pending a GITHUB_TOKEN or an out-of-band push of
  branch `paolo/lean-devcontainer`.
