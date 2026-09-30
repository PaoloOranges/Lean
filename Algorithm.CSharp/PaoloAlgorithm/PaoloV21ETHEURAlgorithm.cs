/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using System;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm
{
    /// <summary>
    /// PaoloHourETHEURAlgorithm v2.1 - evidence-driven iteration on v2 (card t_cebe7f5b, parent T6).
    ///
    /// Starting evidence (T6 A/B, /opt/data/workspace/lean-storage/results/backtest-v2/AB-RESULTS.md):
    /// v2 beat v1 and buy-and-hold on the 2026 in-sample return (-1.716% vs -9.337% / -6.43%) and
    /// Sharpe (-0.091 -> +0.206), but FAILED the hard MDD bar: 48.3% > v1's 44.0%. Root cause:
    /// the ungated trend-pullback entry arm re-bought the Feb/Jun 2026 downtrend 29 times (win rate
    /// 15%, avg loss -2.35%, round-trip fees EUR 117.94 = 11.8% of capital): a -2..4%/trade staircase.
    ///
    /// v2.1 change set (decisions made by the orchestrator parent; defaults encoded exactly):
    ///  (1) PRIMARY FIX: IsConfirmedUpTrend() - ADX(19) >= 20 AND +DI > -DI - now gates BOTH entry
    ///      arms (v2 gated only the lower-band mean-reversion arm). Kills the bear-market re-entry
    ///      staircase at the source (T5 top-5 idea #1; QC Research 21195 / QuantPedia D1H1 veto).
    ///      Consequence: the Armed-state regime re-check also applies to both arms (any armed signal
    ///      disarms the moment the up-trend confirmation lapses).
    ///
    ///  (2) Fee-aware re-entry cooldown: MinBarsBetweenTrades = 48 hourly bars (2 days) after ANY
    ///      position close (software exit or broker stop-limit trigger), to blunt the 1.6%/round-trip
    ///      fee drag vs the 2% risk budget (kb-cost-aware-trading).
    ///
    ///  (3) Maker-limit entries: entry orders are LimitOrder at bar close * (1 + 0.05%) so the buy
    ///      limit sits just ABOVE the market (marketable buy limit): it fills on the next bar unless
    ///      price gaps down more than the buffer. CoinbaseFeeModel charges the 0.60% maker fee for
    ///      limit fills (vs 0.80% taker; T5 kb-maker-limit-fee-drag ~25% fee cut). If unfilled after
    ///      EntryLimitTtlBars = 4 bars the order is CANCELLED and the signal is dropped - by decision,
    ///      we do NOT chase with market. Deviation note: the card flags a "MarketOrder fallback behind
    ///      a parameter flag default false"; we interpret "fallback" as the chase-on-expiry behaviour
    ///      and expose it as parameter ChaseWithMarket (default false, i.e. cancel-only per the
    ///      sentence above). Flipping ChaseWithMarket=true re-enters via MarketOrder at expiry,
    ///      letting A/B toggle the fallback without recompiling the strategy default.
    ///
    /// Unchanged from v2 (card item 5): broker-side StopLimitOrder behind every entry fill (NOT
    /// StopMarket - CoinbaseBrokerageModel rejects it post 2019-03-23), ATR 2.5x ratchet trail,
    /// 45-day time-stop, 2%-risk sizing capped at 80% of free cash, explicit CoinbaseFeeModel,
    /// SetBenchmark(symbol), parameterised start-date/end-date window, identical indicator set and
    /// warm-up. v1 (PaoloHourETHEURAlgorithm) and v2 (PaoloV2ETHEURAlgorithm) are kept intact on
    /// this branch for the A/B/C comparison.
    /// </summary>
    public class PaoloV21ETHEURAlgorithm : QCAlgorithm
    {
        private enum Phase { Flat, Armed, Long }

        // --- Symbols / account (identical to v1/v2) ---
        private const string CryptoName = "ETH";
        private const string CurrencyName = "EUR";
        private const string SymbolName = CryptoName + CurrencyName;

        // --- v2 risk parameters (inherited unchanged from v2) ---
        private const decimal MinStopDistance = 0.03m;      // 3% floor stop, T4 counterfactual anchor
        private const decimal AtrStopMultiple = 2.0m;       // stop distance = max(3%, 2*ATR(14)) at entry
        private const decimal AtrTrailMultiple = 2.5m;      // chandelier trail from peak
        private const decimal RiskPerTrade = 0.02m;         // (entry-stop)*qty <= 2% of equity
        private const decimal MaxCashFraction = 0.8m;       // cap notional at 80% of free cash (v1 ceiling)
        private const decimal StopLimitBuffer = 0.015m;     // limit = stop*(1-1.5%): fill protection on gap-through
        private const int MaxHoldHours = 24 * 45;           // time-stop: no position may live longer than 45 days
        private const decimal RsiProfitTakeLevel = 70m;     // optional early-exit RSI level
        private const decimal AdxTrendLevel = 20m;          // regime gate threshold

        // --- v2.1 parameters (card items 2-4) ---
        private const decimal LimitEntryOffset = 0.0005m;   // +0.05% above close: marketable maker buy limit
        private const int EntryLimitTtlBars = 4;            // cancel unfilled entry limit after 4 bars (no chase)
        private const int DefaultMinBarsBetweenTrades = 48; // fee-aware re-entry cooldown after any close

        private int _minBarsBetweenTrades;                  // from parameter "min-bars-between-trades"
        private bool _chaseWithMarket;                      // from parameter "chase-with-market" (default false)

        private const int AtrPeriod = 14;
        private const int AdxPeriod = 19;
        private const int RsiPeriod = 26;
        private const int BbPeriod = 26;
        private const int VeryFastPeriod = 12;
        private const int FastPeriod = 26;
        private const int SlowPeriod = 55;

        private Symbol _symbol;
        private ExponentialMovingAverage _veryFastMA;
        private MovingAverageConvergenceDivergence _macd;
        private AverageDirectionalIndex _adx;
        private RelativeStrengthIndex _rsi;
        private BollingerBands _bollingerBands;
        private AverageTrueRange _atr;

        private Phase _phase = Phase.Flat;
        private decimal? _prevHistogram;                  // MACD histogram delta tracked manually
        private bool _histRising;
        private bool _armedByLowerBand;                   // which arm rule set the Armed state
        private decimal _entryPrice;
        private decimal _peakPrice;
        private int _hoursHeld;
        private int _stopOrderId = -1;

        // v2.1 order/clock state
        private int _pendingEntryOrderId = -1;            // live maker-limit entry order
        private int _entryOrderAgeBars;                   // bars since the entry limit was submitted
        private int _cooldownBars;                        // bars remaining before a new entry is allowed

        private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.Coinbase, AccountType.Cash);
            SetTimeZone(NodaTime.DateTimeZone.Utc);

            // Parameterised window: identical binary for the 2026 in-sample and 2024 OOS legs.
            var startDate = ParseDateParameter("start-date", new DateTime(2026, 1, 1));
            var endDate = ParseDateParameter("end-date", new DateTime(2026, 9, 27));
            SetStartDate(startDate);
            SetEndDate(endDate);

            // v2.1 (2)/(3): cooldown length and market-chase fallback, A/B-toggleable via config params.
            _minBarsBetweenTrades = Math.Max(0, GetParameter("min-bars-between-trades", DefaultMinBarsBetweenTrades));
            _chaseWithMarket = string.Equals(GetParameter("chase-with-market", "false"), "true",
                StringComparison.OrdinalIgnoreCase);

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            _symbol = AddCrypto(SymbolName, Resolution.Hour, Market.Coinbase).Symbol;

            // Explicit self-declared fee model (v2 item e): Coinbase Advanced-1 taker 0.80% /
            // maker 0.60% - maker fills on the v2.1 limit entries price at 0.60% (T5
            // kb-maker-limit-fee-drag). CoinbaseBrokerageModel supports OrderType.Limit (verified
            // Common/Brokerages/CoinbaseBrokerageModel.cs SupportedOrderTypes).
            Securities[_symbol].SetFeeModel(new CoinbaseFeeModel());
            // Benchmark = the traded instrument, so reported alpha is vs buy-and-hold ETHEUR.
            SetBenchmark(_symbol);

            _veryFastMA = EMA(_symbol, VeryFastPeriod, Resolution.Hour);
            _macd = MACD(_symbol, VeryFastPeriod, SlowPeriod, FastPeriod, MovingAverageType.Exponential, Resolution.Hour);
            _adx = ADX(_symbol, AdxPeriod, Resolution.Hour);
            _rsi = RSI(_symbol, RsiPeriod, MovingAverageType.Exponential);
            _bollingerBands = BB(_symbol, BbPeriod, 1.0m, MovingAverageType.Simple);
            _atr = ATR(_symbol, AtrPeriod, MovingAverageType.Simple, Resolution.Hour);

            // Same warm-up as v1/v2 (7 days = 168 hourly bars; binding requirement MACD = 80 bars,
            // ATR(14) = 15, ADX(19) ~ 38 - all clear with margin, see T3 §6).
            SetWarmUp(TimeSpan.FromDays(7));
        }

        private DateTime ParseDateParameter(string name, DateTime defaultValue)
        {
            var raw = GetParameter(name, null);
            if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, _culture, DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
            return defaultValue;
        }

        public override void OnData(Slice data)
        {
            if (IsWarmingUp || !data.Bars.ContainsKey(_symbol))
            {
                return;
            }

            var bar = data.Bars[_symbol];
            var close = bar.Close;
            _lastClose = close;

            // v2.1 (2): cooldown clock ticks once per bar, independent of the state machine.
            if (_cooldownBars > 0)
            {
                _cooldownBars--;
            }

            // v2.1 (3): entry-limit lifecycle: age the pending order and cancel on TTL expiry.
            // A Fill/Canceled/Invalid event clears the pending state in OnOrderEvent.
            if (_pendingEntryOrderId >= 0)
            {
                _entryOrderAgeBars++;
                if (_entryOrderAgeBars >= EntryLimitTtlBars)
                {
                    Transactions.CancelOrder(_pendingEntryOrderId);
                    _pendingEntryOrderId = -1;
                    _entryOrderAgeBars = 0;
                    if (_chaseWithMarket)
                    {
                        // A/B toggle (default false): chase the expired signal with a market order.
                        ChaseWithMarketOrder();
                    }
                }
            }

            if (!_macd.IsReady || !_adx.IsReady || !_rsi.IsReady || !_bollingerBands.IsReady || !_atr.IsReady)
            {
                return;
            }

            // track MACD histogram delta every bar (independent of state machine)
            var hist = _macd.Histogram.Current.Value;
            _histRising = _prevHistogram.HasValue && hist > _prevHistogram.Value;
            _prevHistogram = hist;

            switch (_phase)
            {
                case Phase.Flat:
                    HandleFlat(close);
                    break;
                case Phase.Armed:
                    HandleArmed(close);
                    break;
                case Phase.Long:
                    HandleLong(close);
                    break;
            }
        }

        // ---- Entry path (structure inherited from v1/v2: arm then confirm) ----------------------

        private void HandleFlat(decimal close)
        {
            // v2.1 (2): fee-aware re-entry cooldown - no new signals for N bars after any close.
            if (_cooldownBars > 0)
            {
                return;
            }
            // Never stack entry signals while one is live.
            if (_pendingEntryOrderId >= 0)
            {
                return;
            }

            // v2.1 (1) PRIMARY FIX: the up-trend regime gate applies to BOTH arms below (v2 gated
            // only the mean-reversion arm; the ungated pullback arm produced the 48.3% MDD staircase).
            var upTrend = IsConfirmedUpTrend();
            if (!upTrend)
            {
                return;
            }

            var bb = _bollingerBands;

            // Mean-reversion arm: close below the 1-sigma lower band, in a confirmed up trend (as v2).
            if (close < bb.LowerBand)
            {
                _armedByLowerBand = true;
                _phase = Phase.Armed;
                return;
            }

            // Trend-pullback arm: below mid-band with bearish MACD posture, expecting MACD recovery
            // to confirm. NOW GATED by the same regime check (v2.1 change vs v2).
            if (close < bb.MiddleBand && _macd < _macd.Signal)
            {
                _armedByLowerBand = false;
                _phase = Phase.Armed;
            }
        }

        private void HandleArmed(decimal close)
        {
            // v2.1: regime re-check in Armed applies to BOTH arms (mirrors (1) being on both entry
            // arms) - a stale arm disarms the moment up-trend confirmation lapses. Mirrors v1's
            // persistent ReadyToBuy state otherwise: the arm survives across bars until confirmed.
            if (!IsConfirmedUpTrend())
            {
                _phase = Phase.Flat;
                return;
            }

            if (IsOkToBuy(close))
            {
                var quantity = RiskBasedQuantity(close);
                if (quantity >= 0.01m)   // Coinbase ETHEUR min order size
                {
                    SubmitEntryLimit(close, quantity);
                    // State transition happens on the FILLED order event (as in v1/v2), not here.
                }
                _phase = Phase.Flat; // disarm either way; fill event moves us to Long
            }
        }

        private void SubmitEntryLimit(decimal close, decimal quantity)
        {
            // v2.1 (3): maker-limit entry at close * (1 + 0.05%) - a marketable buy limit: it sits
            // just above the last price, fills at (better than) the ask on the next bar, and is only
            // missed on a >0.05% gap-down, exactly the case where we DO NOT want to chase.
            var limitPrice = Math.Round(close * (1m + LimitEntryOffset), 2);
            var ticket = LimitOrder(_symbol, quantity, limitPrice);
            _pendingEntryOrderId = ticket.OrderId;
            _entryOrderAgeBars = 0;
            Log($"{Time:u} entry-limit submitted @ {limitPrice:F2} qty={quantity} order={_pendingEntryOrderId}");
        }

        private void ChaseWithMarketOrder()
        {
            // Only reachable with chase-with-market=true (A/B toggle; default false = cancel-only).
            if (_phase != Phase.Flat || _cooldownBars > 0)
            {
                return;
            }
            var quantity = RiskBasedQuantity(_lastClose);
            if (quantity >= 0.01m)
            {
                MarketOrder(_symbol, quantity);
            }
        }

        private bool IsConfirmedUpTrend()
        {
            return _adx >= AdxTrendLevel
                && _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex;
        }

        private bool IsOkToBuy(decimal close)
        {
            // "Recovering momentum" (replacement for v1's OLS-slope veto, see class comment):
            var macdAboveSignal = _macd > _macd.Signal;
            var histogramRising = _histRising;
            var aboveLowerBand = close > _bollingerBands.LowerBand;

            // MACD must be recovering while price has stepped back above the lower band:
            // v1 fired on slope>=0 (never binding as a veto); this is the same intent, evaluable.
            return macdAboveSignal && histogramRising && aboveLowerBand;
        }

        private decimal RiskBasedQuantity(decimal entry)
        {
            // size so (entry - stop) * qty <= 2% equity, capped at 80% of free cash.
            var stopDistance = StopDistance(entry);
            var equity = Portfolio.TotalPortfolioValue;
            var riskQuantity = (RiskPerTrade * equity) / stopDistance;
            var cashQuantity = (Portfolio.CashBook[CurrencyName].Amount * MaxCashFraction) / entry;
            var qty = Math.Min(riskQuantity, cashQuantity);
            qty = Math.Truncate(qty * 1000m) / 1000m;   // 1e-3 ETH, same rounding discipline as v1/v2
            return Math.Max(qty, 0m);
        }

        private decimal StopDistance(decimal entry)
        {
            // entry-anchored stop distance: max(3%, 2 x ATR(14)) evaluated at entry time.
            return Math.Max(MinStopDistance * entry, AtrStopMultiple * _atr.Current.Value);
        }

        // ---- Exit path (unchanged from v2; guaranteed reachable from any state) -----------------

        private void HandleLong(decimal close)
        {
            _hoursHeld++;
            _peakPrice = Math.Max(_peakPrice, close);

            // 1. ATR ratchet trail (2.5xATR below the running peak) - UNCONDITIONAL on breach.
            var trailPrice = _peakPrice - AtrTrailMultiple * _atr.Current.Value;
            if (close <= trailPrice)
            {
                CloseLong("ATR-trail");
                return;
            }

            // 2. Optional early profit-take keeping v1's RSI/BB confirmation style, at a strict
            //    level so it is a bonus exit, never the only door.
            if (_rsi > RsiProfitTakeLevel && close > _bollingerBands.UpperBand)
            {
                CloseLong("RSI/BB-profit-take");
                return;
            }

            // 3. Time-stop: no position lives past 45 days without an exit.
            if (_hoursHeld >= MaxHoldHours)
            {
                CloseLong("time-stop");
            }
            // 4. Broker-side stop-limit (placed at fill) remains pending underneath all of the
            //    above and executes without algorithm liveness at all.
        }

        private void CloseLong(string reason)
        {
            var held = Portfolio.CashBook[CryptoName].Amount;
            if (held > 0m)
            {
                Log($"{Time:u} exit [{reason}] close={_lastClose:F2} entry={_entryPrice:F2} held_h={_hoursHeld}");
                MarketOrder(_symbol, -held);
            }
            if (_stopOrderId >= 0)
            {
                // do not leave an orphan stop after a software exit
                Transactions.CancelOrder(_stopOrderId);
                _stopOrderId = -1;
            }
            // Phase returns to Flat via the Sell-Filled event (bookkeeping symmetry with v1/v2);
            // the cooldown is armed there, on the CLOSE of a position, per v2.1 (2).
        }

        // ---- Order-event bookkeeping -------------------------------------------------------------

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null)
            {
                return;
            }

            if (orderEvent.Status == OrderStatus.Filled)
            {
                if (orderEvent.Direction == OrderDirection.Buy)
                {
                    _phase = Phase.Long;
                    _entryPrice = orderEvent.FillPrice;
                    _peakPrice = orderEvent.FillPrice;
                    _hoursHeld = 0;
                    _pendingEntryOrderId = -1;
                    _entryOrderAgeBars = 0;

                    // broker-side protection immediately behind the entry (v2 item a - StopLimit,
                    // never StopMarket: rejected by CoinbaseBrokerageModel post 2019-03-23).
                    var stop = _entryPrice - StopDistance(_entryPrice);
                    var limit = stop * (1m - StopLimitBuffer);
                    var ticket = StopLimitOrder(_symbol, -orderEvent.FillQuantity, stop, limit);
                    _stopOrderId = ticket.OrderId;
                    Log($"{Time:u} entry filled @ {_entryPrice:F2} qty={orderEvent.FillQuantity}, " +
                        $"stop-limit @ stop={stop:F2} limit={limit:F2} order={_stopOrderId}");
                }
                else // Sell filled (trail/RSI/time-stop exit, or the broker stop-limit triggering)
                {
                    _phase = Phase.Flat;
                    _stopOrderId = -1;
                    // v2.1 (2): fee-aware re-entry cooldown after ANY position close.
                    _cooldownBars = _minBarsBetweenTrades;
                }
            }
            else if (orderEvent.Status == OrderStatus.Invalid || orderEvent.Status == OrderStatus.Canceled)
            {
                if (orderEvent.OrderId == _pendingEntryOrderId)
                {
                    // expired/rejected maker entry: drop the signal, stay flat (no chasing by default)
                    _pendingEntryOrderId = -1;
                    _entryOrderAgeBars = 0;
                }
                if (orderEvent.Direction == OrderDirection.Buy && _phase == Phase.Flat)
                {
                    // rejected/cancelled entry: stay flat (v1's Invalid handler flipped state erratically; T3 notes)
                    _phase = Phase.Flat;
                }
                if (orderEvent.OrderId == _stopOrderId && _phase == Phase.Flat)
                {
                    _stopOrderId = -1;
                }
            }
        }

        private decimal _lastClose;
        public override void OnEndOfAlgorithm()
        {
            Log($"{Time} - TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            Log($"{Time} - CashBook: {Portfolio.CashBook}");
        }
    }
}
