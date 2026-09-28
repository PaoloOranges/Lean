# Devcontainer: image-only .NET 10 environment (no Dockerfile, no build)

## What changed

The former `.devcontainer/` pointed at a Dockerfile based on `quantconnect/research:latest`
(Lean 2-era, .NET 6/7 era). This fork targets **net10.0**, and the deployment target is a
build-free environment: `devcontainer up` (or VS Code / Codespaces) must work with **zero
locally installed toolchain** and **no `docker build` step**.

`.devcontainer/devcontainer.json` is an **image-only** config:

```json
"image": "mcr.microsoft.com/dotnet/sdk:10.0"
```

The tag was verified against the MCR tags endpoint (`/v2/dotnet/sdk/tags/list` → `10.0`,
`10.0.401`, …) and resolves on linux/arm64 and linux/amd64.

## Review revision (R1-R4)

This revision addresses the PR #1 review:

- **R1 — canonical data = shared mount only.** Market data lives on the orangenas CIFS
  share (`//orangenas/DockerShared`, vers=3.1.1), bind-mounted on the host at
  `/opt/data/docker-shared` and in the container at `/data` (read-only bind). The repo no
  longer carries dataset copies: `tools/fetch_etheur_hourly.py` and the committed
  `Data/crypto/coinbase/hour/etheur_trade.zip` were deleted; the hourly ETHEUR backtest
  runs off the share's Coinbase **minute** data
  (`/opt/data/docker-shared/QuantConnect/Data/crypto/coinbase/minute/etheur/`, 2022-01-01 →
  present). NOTE: no hourly ETHEUR dataset exists on the share, and this engine
  (LEAN v2.5) does NOT consolidate minute data up to hourly Resolution - see
  "R1 residual gap" below.
  `docker/data-mount-check.sh` enforces the mount (exit 1 + clear message when missing /
  empty / not a mountpoint) and runs **first** in `postCreateCommand` /
  `updateContentCommand` and again in the run wrapper. Policy: if the check fails, mount
  the share — no fallback fetching, no synthetic data, no repo-baked substitutes.
- **R2 — results in the pre-existing `lean-storage`.** `devcontainer.json` bind-mounts
  host `/opt/data/workspace/lean-storage` (absolute; same semantics as
  `results-destination-folder` in `Launcher/config.json`) at `/lean-storage`, and the run
  sets `--results-destination-folder /lean-storage/results`. No `mkdir` fallbacks anywhere
  (the host directory pre-exists; the bind mount fails loudly if it does not).
- **R3 — single-source config.** `config.devcontainer.json` is deleted. The per-run config
  IS `Launcher/config.json` (canonical, credential-masked: `api-access-token` =
  `__REDACTED__`), passed via `--config /Lean/Launcher/config.json`. Only path overrides
  go on the CLI (all supported by `Configuration/LeanArgumentParser.cs`):
  `--data-folder /data/QuantConnect/Data`, `--results-destination-folder
  /lean-storage/results`, `--algorithm-location /Lean/Bin/QuantConnect.Algorithm.CSharp.dll`,
  `--close-automatically=true`. Note: `object-store-root` has no CLI override; the canonical
  `"../../../lean-storage"` resolves to `/lean-storage` when the launcher runs with cwd
  `/Lean` (the run script cds there), i.e. the mounted volume.
- **R4 — credentials from the environment.** `devcontainer.json` `containerEnv` forwards
  `QUANTCONNECT_API_USERNAME` / `QUANTCONNECT_API_TOKEN` from the host env
  (`${localEnv:...}`); `docker/run-backtest.sh` passes them to the launcher as
  `--job-user-id` / `--api-access-token` (CLI merge in `Config` overrides the masked file
  values). Values are never echoed, logged, or committed.

## Running the backtest in the container

```bash
# after devcontainer up (postCreate already mounted-checked + published to /Lean/Bin):
bash /Lean/docker/run-backtest.sh
# results: /lean-storage/results  (host: /opt/data/workspace/lean-storage/results)
```

## Verification performed (sandbox host, 2026-09-27)

See kanban card t_4bf7813b. The lifecycle was exercised for real against the remote Docker
endpoint (daemon host does not see this sandbox's paths, so the verification image bakes the
repo at `/Lean` and a data slice at `/data` via build-context COPY; everything else — mount
check, post-create publish, run-backtest CLI args, results under `/lean-storage/results` —
is the shipped code path). `devcontainer up` itself completes on any host where the two
absolute mount sources exist (`/opt/data/docker-shared`,
`/opt/data/workspace/lean-storage`).

`failed-data-requests` may still list optional quote/BTCEUR zips — Lean logs those at
TRACE; the run stays error-free (upstream behavior).

## R1 residual gap (found during verification, 2026-09-27 run 12)

The board-owner review deleted the repo-committed hourly zip on the premise that
"coinbase hourly data lives on the share". Verified on the share: the only Coinbase
ETHEUR data is **minute** resolution
(`/opt/data/docker-shared/QuantConnect/Data/crypto/coinbase/minute/etheur/`,
1729 day-files, 2022-01-01 → 2026-09-25). There is no
`crypto/coinbase/hour/etheur*` anywhere on the share.

This fork's engine (Globals v2.5.0.0) resolves an hourly subscription by reading
hour files with a daily fallback (`DataMonitor` recorded exactly that:
`/crypto/coinbase/hour/etheur_trade.zip` + `/crypto/coinbase/daily/etheur_trade.zip`
failed, 17 failed requests) — it does **not** build hourly bars from the minute
subscription the way current upstream Lean does. Consequence: the end-to-end run is
technically clean (exit 0, 0 ERROR lines, results written to
`/opt/data/workspace/lean-storage/results`) but data-starved (1 data point, no trades).

To get a trading backtest again, one of these is needed (board owner's call, both
out of scope for the no-fetch/no-recreate policy):
  a) seed `crypto/coinbase/hour/etheur_trade.zip` (consolidated window
     2023-12-18..2024-08-02) onto the share once, or
  b) change `PaoloHourETHEURAlgorithm` to `Resolution.Minute` (the minute data IS on
     the share and fully covers SetStartDate(2024,1,1)..SetEndDate(2024,8,1)).
