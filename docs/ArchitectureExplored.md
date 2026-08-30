# QuantConnect LEAN Algorithmic Trading Engine -- Architecture Map

## Overview

LEAN is an **event-driven, open-source algorithmic trading engine** written in C# (.NET 9) with Python support. It powers backtesting, paper trading, and live trading across equities, options, futures, forex, crypto, CFDs, and indices. Every component is pluggable via MEF (MEP/Composer) dependency injection.

| Metric | Value |
|---|---|
| **Nodes** | 74,142 |
| **Edges** | 220,496 |
| **Source Files** | 4,766 (86% C#, 11% Python) |
| **Classes** | 5,493 |
| **Methods** | 21,183 |

---

## Module Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          LAUNCHER (Entry Point)                          │
│  Launcher/Program.Main() → config.json → Initializer.Start()            │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │
                    ┌───────────┬────────────┐
                    │     CONFIGURATION       │
                    │  Reads config.json      │
                    │  Merges environments    │
                    │  Resolves handler types  │
                    └───────────┬────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                  ▼
     ┌──────────────┐  ┌──────────────┐   ┌──────────────┐
     │ QUEUES       │  │   ENGINE      │   │    API       │
     │ JobQueue     │  │ Engine.Run()  │   │ QuantConnect │
     │ Pulls jobs   │  │ Orchestrator  │   │ ApiClient    │
     └──────────────┘  └───────┬──────┘   └──────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                  ▼
     ┌──────────────┐  ┌──────────────┐   ┌──────────────┐
     │ SETUP        │  │ ALGORITHM     │   │ REAL-TIME    │
     │ SetupHandler │  │ Manager       │   │ Handler      │
     │ Creates algo │  │ TimeSlice     │   │ Live/Backtest│
     │ + brokerage  │  │ Loop          │   └──────────────┘
     └──────────────┘  └───────┬──────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                  ▼
     ┌──────────────┐  ┌──────────────┐   ┌──────────────┐
     │ DATA FEED    │  │ TRANSACTION  │   │ RESULTS      │
     │ Synchronizer │  │ Handler      │   │ Handler      │
     │ StreamData() │  │ Order fills  │   │ HTML/JSON    │
     └───────┬──────┘  └──────────────┘   └──────────────┘
             │
             ▼
     ┌──────────────────┐
     │  DATA PROVIDERS   │
     │ FileSystem/API/   │
     │ Brokerage         │
     └──────────────────┘
```

---

## Core Module Dependencies (19 Projects)

### Tier 1 -- Foundation (no internal dependencies)

| Project | Path | Nodes | Role |
|---------|------|-------|------|
| **Common** | `Common/` | 3,450 | Domain models, securities, symbols, cash management, scheduling, timekeeping, interfaces, parsing utilities. The backbone everything depends on. |
| **Compression** | `Compression/` | — | ZIP file I/O for Lean's flat-file data storage format. |
| **Logging** | `Logging/` | — | Multi-target logging with pluggable `ILogHandler` (console, file, composite, queue). |
| **Messaging** | `Messaging/` | — | `IMessagingHandler` abstraction for sending packets between algorithm and external systems. |
| **Api** | `Api/` | 206 | REST API client for QuantConnect cloud platform communication. |

### Tier 2 -- Domain Models

| Project | Path | Nodes | Role |
|---------|------|-------|------|
| **Data** | `Data/` | 4,303 | Market data models, consolidators (ticks→bars), feed abstractions, subscriptions, aggregators. Largest module. |
| **Securities** | `Common/Securities/` | 1,339 | Security definitions: Equity, Option, Future, Forex, Crypto. Includes margin models, settlement models, buying power, position management. |
| **Indicators** | `Indicators/` | 896 | 170+ technical indicators (SMA, EMA, RSI, MACD, Bollinger Bands, ATR, Hull MA). Unified `Indicator<T>` base with chaining and auto-warmup. |
| **Orders** | `Common/Orders/` | 430 | Order types, order events, fill models. |

### Tier 3 -- Algorithm & Framework

| Project | Path | Nodes | Role |
|---------|------|-------|------|
| **Algorithm** | `Algorithm/` | 1,175 | **QCAlgorithm base class** — the user-facing API. Partial classes for trading, universes, indicators, history, plotting, framework support, Python integration. |
| **Algorithm.Framework** | `Algorithm.Framework/` | 438 | **LEAF** (Lean Algorithmic Engine Framework): Alpha models, Universe Selection, Portfolio Construction, Risk Management, Execution models. |

### Tier 4 -- Engine & Brokerage

| Project | Path | Nodes | Role |
|---------|------|-------|------|
| **Engine** | `Engine/` | 1,141 | **Central orchestrator.** Wires all handlers together, runs main event loop via `AlgorithmManager`, manages lifecycle. |
| **Brokerages** | `Brokerages/` | 534 | Pluggable brokerage layer: base `Brokerage` class, paper brokerage, backtesting brokerage, WebSocket infrastructure, order books. (Exchange-specific brokers like Alpaca/IB/Binance are in separate NuGet packages.) |

### Tier 5 -- Data Infrastructure

| Project | Path | Nodes | Role |
|---------|------|-------|------|
| **DataFeeds** | `Engine/DataFeeds/` | 615 | Data subscription management, consolidation pipeline, synchronizer (bridges data sources to algorithm), `TimeSlice` generation. |
| **Queues** | `Queues/` | — | Job queue (`JobQueue`). Local: loads DLL from disk. Cloud: pulls `AlgorithmNodePacket` from QuantConnect server. |

### Tier 6 -- Entry Points & Tooling

| Project | Path | Role |
|---------|------|------|
| **Launcher** | `Launcher/` | Primary entry point. Reads config, initializes engine, runs jobs. Contains `config.json` with 30+ environment definitions. |
| **Optimizer.Launcher** | `Optimizer.Launcher/` | Parameter optimization entry point. Runs multiple algorithm instances in parallel. |
| **Optimizer** | `Optimizer/` | Core optimization engine: parameter grids, parallel strategies, objectives. |
| **DownloaderDataProvider** | `DownloaderDataProvider/` | Standalone data download tool. |
| **ToolBox** | `ToolBox/` | CLI utility: data converters (AlgoSeek, Kaiko), generators (RandomDataGenerator), universe generators, exchange info updaters. |

### Tier 7 — Analysis & Research

| Project | Path | Role |
|---------|------|------|
| **Report** | `Report/` | Backtest result analysis: HTML reports, equity curves, performance metrics (Sharpe, Sortino, Calmar), rolling returns. |
| **Research** | `Research/` | Jupyter notebook integration via `QuantBook` for interactive research. |

### Tier 8 — Tests & Samples

| Project | Path | Role |
|---------|------|------|
| **Tests** | `Tests/` | Comprehensive NUnit test suite + regression harness that runs hundreds of algorithms against golden files. |
| **Algorithm.CSharp** | `Algorithm.CSharp/` | ~800 C# algorithm implementations (examples + regression tests). |
| **Algorithm.Python** | `Algorithm.Python/` | ~436 Python algorithm implementations (mirrors C# set). |
| **AlgorithmFactory** | `AlgorithmFactory/` | Algorithm loading/compilation: compiles C# from source, loads Python algorithms. |

---

## Leiden Clusters (Detected Architectural Modules)

The graph analysis identified 12 functionally cohesive clusters:

| # | Cluster | Members | Cohesion | Top Nodes | Interpretation |
|---|---------|---------|----------|-----------|----------------|
| 1 | **Algorithm** | 391 | 0.80 | `RegressionTestException`, `Buy`, `GetSubscriptionDataConfigs` | Core algorithm orchestration, order methods, regression test infrastructure |
| 2 | **Common** | 267 | 1.00 | `ConvertPeriod`, `GetPeriodValue` | Period conversion utilities (perfect cohesion) |
| 3 | **Common** | 264 | 0.67 | `Trace`, `Error`, `Debug`, `Run` | Cross-cutting logging/tracing infrastructure |
| 4 | **Tests** | 217 | 0.81 | `GetDefault`, `AlwaysOpen`, `SetMarketPrice` | Brokerage model testing: default params, market price simulation |
| 5 | **Tests** | 199 | 0.65 | `AddDays`, `IsOpen`, `CreateForexSecurityExchangeHours` | Security exchange hours testing for forex/futures |
| 6 | **Tests** | 196 | 0.73 | `Strikes`, `OptionUniverse`, `CreateOptionSecurity` | Options universe selection testing |
| 7 | **Tests** | 190 | 0.87 | `Update`, `TestIndicator`, `Reset`, `SimpleMovingAverage` | Indicator regression testing framework |
| 8 | **Common** | 161 | 0.78 | `TryGetValue`, `Generate`, `CreateSecurity` | Security creation factory methods and property parsing |
| 9 | **Algorithm** | 154 | 0.91 | `log`, `get`, `date`, `pop` | Python algorithm support / interop layer |
| 10 | **Tests** | 133 | 0.99 | `AssertCode`, `Tinygrad`, `Tigramite`, `Tsfel` | ML/AI library integration testing |
| 11 | **Tests** | 126 | 0.79 | `GetSlice`, `PortfolioTargetCollection`, `GenerateEquity` | Backtesting simulation: data slice generation, fake algorithm |
| 12 | **Tests** | 122 | 0.79 | `Combine`, `Exists`, `CreateDirectory`, `GetMarketHoursDatabase` | File system and market hours database utilities |

---

## Key Interfaces (Plugin Architecture)

All handlers are resolved from `config.json` via MEF Composer:

```
config.json → Initializer.GetSystemHandlers() → Composer.Resolve<T>()
              Initializer.GetAlgorithmHandlers() → Composer.Resolve<T>()
```

| Interface | Assembly | Purpose |
|-----------|----------|---------|
| `IAlgorithm` | Common | Core algorithm contract (TimeKeeper, Securities, Portfolio, Orders) |
| `IBrokerage` | Brokerages | Exchange communication (Connect, PlaceOrder, UpdateOrder, Disconnect) |
| `IDataFeed` | Engine/DataFeeds | Data source abstraction (Initialize, CreateSubscription, Exit) |
| `ISetupHandler` | Engine/Setup | Algorithm + brokerage creation (CreateAlgorithmInstance, CreateBrokerage) |
| `IResultHandler` | Engine/Results | Output generation (HTML reports, JSON, live status) |
| `ITransactionHandler` | Engine/Transactions | Order processing (ProcessSynchronousEvents, AddOpenOrder) |
| `IRealTimeHandler` | Engine/RealTime | Live trading control (Setup, SetTime, ScanPastEvents) |
| `ILeanManager` | Engine/Server | Lifecycle callbacks (SetAlgorithm, OnAlgorithmStart/End, Update) |
| `IApi` | Api | Cloud platform communication (SetAlgorithmStatus, SetAuthentication) |
| `IMessagingHandler` | Messaging | Packet delivery (Send, SendNotification) |
| `IJobQueueHandler` | Queues | Job management |

---

## LEAF Framework (Lean Algorithmic Engine Framework)

Structured four-pillar approach in `Algorithm.Framework/`:

```
┌─────────────────────────────────────────────────────────────┐
│                        AlphaModel                           │
│  Generates Insights (buy/sell signals) from market data    │
└──────────────────────┬──────────────────────────────────────┘
                       │ insights
          ┌────────────▼────────────┐
          │  PortfolioConstruction   │
          │  Model                  │
          │  Creates Portfolio      │
          │  Targets from insights  │
          └────────────┬────────────┘
                       │ targets
          ┌────────────▼────────────┐     ┌──────────────────┐
          │  RiskManagement         │────►│  Execution       │
          │  Model                  │     │  Model           │
          │  Enforces limits        │     │  Order routing   │
          └─────────────────────────┘     └──────────────────┘
```

| Pillar | Key Interface | Role |
|--------|---------------|------|
| **Alpha** | `AlphaModel` | Signal generation (`Update`, `OnSecuritiesChanged`) |
| **Universe Selection** | `UniverseSelectionModel` | Asset filtering (`CreateUniverses`, `GetNextRefreshTimeUtc`) |
| **Portfolio Construction** | `PortfolioConstructionModel` | Weight allocation (`CreateTargets`, `RefreshRebalance`) |
| **Risk Management** | `RiskManagementModel` | Position limits (`ManageRisk`, `OnSecuritiesChanged`) |
| **Execution** | `ExecutionModel` | Order routing (`Execute`, `OnOrderEvent`) |

---

## Algorithm Lifecycle Flow

```
Launcher.Program.Main()
  │
  ├─► Configuration.Parse()        // Load config.json, merge environments
  ├─► Initializer.Start()          // Setup logging, MEF Composer, resolve handlers
  ├─► JobQueue.NextJob()           // Get AlgorithmNodePacket
  │
  ▼
Engine.Run()
  │
  ├─► BrokerageSetupHandler.CreateAlgorithmInstance()  // Load DLL / Python
  ├─► BrokerageSetupHandler.CreateBrokerage()          // Paper/Backtesting/Live
  ├─► SecurityService + DataManager + SubscriptionManager init
  ├─► Synchronizer.StreamData()                        // Bridge data → algorithm
  │
  ▼
AlgorithmManager.Run()        // Main event loop
  │
  ├─► LeanManager.OnAlgorithmStart()
  ├─► synchronizer.StreamData() → foreach TimeSlice:
  │     │
  │     ├─► realTimeHandler.ScanPastEvents()
  │     ├─► subscriptionManager.ScanPastConsolidators()  // ticks→bars
  │     ├─► algorithm.Time = slice.Time
  │     ├─► algorithm.OnData(slice)          // ← USER TRADING LOGIC
  │     ├─► algorithm.OnFrameworkData()      // LEAF pipeline
  │     ├─► transactionHandler.ProcessSynchronousEvents()  // Fill orders
  │     ├─► HandleSplits() / HandleDividends()
  │     └─► algorithm.OnEndOfTimeStep()
  │
  ├─► LeanManager.OnAlgorithmEnd()
  ├─► Liquidate() (live mode only)
  └─► brokerage.Disconnect()
```

---

## Data Flow Path

```
Data Source (file / API / brokerage WebSocket)
    │
    ▼
IDataProvider.Resolve()
    │
    ▼
SubscriptionDataSourceReader → AggregationManager (IDataAggregator)
    │                                  // Ticks → Minutes → Hours
    ▼
Synchronizer.StreamData()
    │
    ▼
AlgorithmManager.Run() loop
    │
    ▼
algorithm.OnData(Slice)     // User's OnData() method
```

---

## Order Flow Path

```
algorithm.MarketOrder(symbol, quantity)
    │
    ▼
SecurityTransactionManager.SubmitOrder()
    │
    ▼
ITransactionHandler.ProcessSynchronousEvents()
    │
    ├── Backtesting → fills immediately from slice data
    ├── Paper → simulates fills
    └── Live → IBrokerage.PlaceOrder() → WebSocket/REST → Exchange matching engine
```

---

## Cross-Package Call Relationships (Top Dependencies)

| From | To | Calls | Interpretation |
|------|-----|-------|----------------|
| ToolBox | RandomDataGenerator | 113 | Synthetic data generation for testing |
| ToolBox | Securities | 68 | Security generators/manipulators |
| ToolBox | CSharp (Algorithm) | 52 | C# algorithm test scaffolding |
| Research | CSharp | 39 | Research notebooks calling C# APIs |
| Report | Util | 23 | Report generation using shared utilities |

---

## Environment Configuration Pattern

`config.json` defines named environments, each specifying different handler implementations:

```json
"backtesting": {
  "setup-handler": "BacktestingSetupHandler",
  "result-handler": "BacktestingResultHandler",
  "data-feed-handler": "FileSystemDataFeed",
  "real-time-handler": "BacktestingRealTimeHandler",
  "transaction-handler": "BacktestingTransactionHandler"
}
"live-alpaca": {
  "live-mode-brokerage": "AlpacaBrokerage",
  "result-handler": "LiveTradingResultHandler",
  "data-feed-handler": "LiveTradingDataFeed",
  "transaction-handler": "BrokerageTransactionHandler"
}
```

The same engine code runs all modes — only the handler implementations change.

---

## Hotspot Analysis (Most Referenced Methods)

| Method | Fan-in | Role |
|--------|--------|------|
| `RegressionTestException.AreEqual` | 3,166 | Regression test assertion — most referenced method in entire codebase |
| `Common.Parse.DateTime` | 1,723 | Date parsing — critical shared utility |
| `CompositeTimeProvider.GetUtcNow` | 1,663 | Global time provider for backtesting + live trading |
| `QCAlgorithm.SetStartDate` | 655 | Algorithm entry-point method |
| `QCAlgorithm.SetEndDate` | 642 | Algorithm entry-point method |
| `BaseSymbolGenerator.Create` | 491 | Synthetic symbol generation for testing |

---

## Summary of Architectural Properties

1. **Event-driven core** — `AlgorithmManager.Run()` processes `TimeSlice` objects in a tight loop; every subsystem hooks into this loop.

2. **Plugin architecture via MEF** — Every major component (logging, messaging, data feed, setup, results, transactions, real-time) is an interface with swappable implementations resolved from config.

3. **Dual-mode operation** — Same engine code runs backtesting and live trading; `LiveMode` flag + environment-specific handlers handle divergence.

4. **Multi-language support** — C# algorithms compile to DLLs loaded via reflection; Python algorithms run through pythonnet. Both inherit from `QCAlgorithm`.

5. **LEAF framework abstraction** — Structured four-pillar model (Alpha → Portfolio → Risk → Execution) for professional-grade strategy development.

6. **Flat-file data storage** — CSV/JSON files compressed in ZIP archives, organized by asset type, market, resolution, and ticker.

7. **Pervasive regression testing** — 7 of 12 detected clusters are test-related; 800+ C# + 436+ Python algorithms serve as both documentation and automated regression tests with golden-output validation.
