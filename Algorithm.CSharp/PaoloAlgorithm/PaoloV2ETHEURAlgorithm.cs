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
using QuantConnect.Securities;
using System;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm
{
    /// <summary>
    /// PaoloHourETHEURAlgorithm v2 - evidence-driven redesign of v1 for the A/B study (card t_1aad99fd).
    ///
    /// Change set (sources: T4 analysis /opt/data/obsidian/20-Research/backtest-2026-v1-analysis.md,
    /// T3 anatomy /opt/data/obsidian/20-Research/algorithm-anatomy-paolo-hour-eth-eur.md,
    /// T5 KB /opt/data/obsidian/20-Research/kb/kb-index.md):
    ///
    ///  (a) REAL stop-loss [T4 priority #1]: a broker-side StopLimitOrder is placed alongside every
    ///      entry fill. StopMarket is NOT used: CoinbaseBrokerageModel rejects it for Time >=
    ///      2019-03-23 (T3 §4 / T5 t6_flag), while StopLimit is in its supported set.
    ///      Stop distance at entry = max(3%, 2 x ATR(14)), anchored at the fill price
    ///      (kb-stop-at-fill-price). T4 counterfactual: a plain 3% stop alone turned v1's -9.34%
    ///      into +2.29% and cost nothing on the 3/3 closed winners.
    ///
    ///  (b) Regime filter [T4: secondary, done cheaply]: mean-reversion (close < BB lower) entries
    ///      are gated on ADX(19) >= 20 AND +DI > -DI, i.e. dip-buys only in a confirmed
    ///      *upward* trend - kills v1's structural failure mode (momentum-chasing dip-buy at the
    ///      window top into a -54% bear; cf. QC Research 21195 / QuantPedia D1H1 veto).
    ///      The middle-band/MACD pullback entry is kept ungated as the trend-following arm.
    ///
    ///  (c) Volatility-scaled exits, deadlock-free [T4 defect D4]: the fixed +4% sell gate and the
    ///      fixed +4% profit cap are REMOVED - they froze v1's state machine for 253 days because
    ///      exit required price >= 1.04 x entry. Exits are now guaranteed reachable:
    ///        - ATR ratchet trail: close <= peak - 2.5 x ATR(14) -> market exit (unconditional;
    ///          a falling price always crosses the trail, so a flat market cannot deadlock),
    ///        - broker-side stop-limit (floor, survives process loss),
    ///        - 45-day time-stop (belt-and-braces per T4 "time-stop and/or unconditional hard stop").
    ///      RSI/BB confirmation is KEPT for the optional early profit-take (RSI(26) > 70 AND
    ///      close > BB upper) but is no longer a precondition for exiting.
    ///
    ///  (d) Risk-based sizing [card 3d + kb-vol-capped-sizing]: size so (entry - stop) x qty
    ///      <= 2% of equity, capped at 80% of free cash (v1's 80%-of-cash cap retained as ceiling).
    ///      With the default 3% floor-stop this deploys ~67% of equity vs v1's flat 80%.
    ///
    ///  (e) Explicit SetFeeModel(new CoinbaseFeeModel()) matching T3's verified Coinbase Advanced-1
    ///      taker 0.80%/side (gross/net honesty - T5 kb-lean-zero-fee-trap), and SetBenchmark(symbol)
    ///      (was commented out in v1, T3 weakness #7) so alpha-vs-hold is attributed natively.
    ///
    /// A/B fairness notes:
    ///  - Account, currency, cash, symbol, resolution, brokerage model, warm-up and all indicator
    ///    parameters (EMA 12/26/55, MACD 12-55-26, RSI 26 EMA-smoothed, BB 26 x 1.0 sigma) are
    ///    identical to v1. Only the exit path, entry gate, sizing, fee/benchmark wiring change.
    ///  - v1's dimensionally-broken 10-bar OLS slope veto (T3 §5: EUR/bar compared against a
    ///    0.5236 rad threshold - effectively never binding) is replaced by its live intent,
    ///    "recovering momentum" = MACD histogram rising with MACD above signal, rather than
    ///    porting a vet that never actually vetted anything.
    ///  - Entry/exit decisions evaluate on bar CLOSE and fill at the next open, matching v1.
    ///  - Backtest dates come from algorithm parameters "start-date"/"end-date" (yyyy-MM-dd)
    ///    so both windows can be run against ONE compiled binary; defaults reproduce the v1
    ///    2026 window for the primary A/B leg.
    /// </summary>
    public class PaoloV2ETHEURAlgorithm : QCAlgorithm
    {
        private enum Phase { Flat, Armed, Long }

        // --- Symbols / account (identical to v1) ---
        private const string CryptoName = "ETH";
        private const string CurrencyName = "EUR";
        private const string SymbolName = CryptoName + CurrencyName;

        // --- v2 risk parameters (card 3a/3c/3d) ---
        private const decimal MinStopDistance = 0.03m;      // 3% floor stop, T4 counterfactual anchor
        private const decimal AtrStopMultiple = 2.0m;       // stop distance = max(3%, 2*ATR(14)) at entry
        private const decimal AtrTrailMultiple = 2.5m;      // chandelier trail from peak (kb-atr-trailing-stop-risk-model)
        private const decimal RiskPerTrade = 0.02m;         // (entry-stop)*qty <= 2% of equity
        private const decimal MaxCashFraction = 0.8m;       // cap notional at 80% of free cash (v1 ceiling)
        private const decimal StopLimitBuffer = 0.015m;     // limit = stop*(1-1.5%): fill protection on gap-through
        private const int MaxHoldHours = 24 * 45;           // time-stop: no position may live longer than 45 days
        private const decimal RsiProfitTakeLevel = 70m;     // kept RSI confirmation for the *optional* early exit
        private const decimal AdxTrendLevel = 20m;          // regime gate threshold (card 3b)

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
        private decimal? _prevHistogram;                  // track MACD histogram delta manually (no Previous accessor in this fork)
        private bool _armedByLowerBand;                   // which arm rule set the Armed state (regime gate applies to MR arm only)
        private decimal _entryPrice;
        private decimal _peakPrice;
        private int _hoursHeld;
        private int _stopOrderId = -1;

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

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            _symbol = AddCrypto(SymbolName, Resolution.Hour, Market.Coinbase).Symbol;

            // (e) honest net P&L: T3 verified CoinbaseBrokerageModel -> CoinbaseFeeModel with
            // Advanced-1 default taker 0.80%/side; set it explicitly rather than relying on wiring.
            SetFeeModel(new CoinbaseFeeModel());
            // (e) benchmark = the traded instrument, so reported alpha is vs buy-and-hold ETHEUR.
            SetBenchmark(_symbol);

            _veryFastMA = EMA(_symbol, VeryFastPeriod, Resolution.Hour);
            _macd = MACD(_symbol, VeryFastPeriod, SlowPeriod, FastPeriod, MovingAverageType.Exponential, Resolution.Hour);
            _adx = ADX(_symbol, AdxPeriod, Resolution.Hour);
            _rsi = RSI(_symbol, RsiPeriod, MovingAverageType.Exponential);
            _bollingerBands = BB(_symbol, BbPeriod, 1.0m, MovingAverageType.Simple);
            _atr = ATR(_symbol, AtrPeriod, MovingAverageType.Simple, Resolution.Hour);

            // Same warm-up as v1 (7 days = 168 hourly bars; binding requirement MACD = 80 bars,
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

            if (!_macd.IsReady || !_adx.IsReady || !_rsi.IsReady || !_bollingerBands.IsReady || !_atr.IsReady)
            {
                return;
            }

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

        // ---- Entry path (structure inherited from v1: arm then confirm) -------------------------

        private void HandleFlat(decimal close)
        {
            var bb = _bollingerBands;

            // Mean-reversion arm: close below the 1-sigma lower band.
            // (b) REGIME GATE (card 3b): only allowed when ADX(19) >= 20 with +DI > -DI, i.e. a
            // confirmed UP trend. v1's failure mode was exactly this entry fired at the window top
            // into a strong downtrend (T4 §4: entered within 2% of the top, rode -54%).
            if (close < bb.LowerBand && IsConfirmedUpTrend())
            {
                _armedByLowerBand = true;
                _phase = Phase.Armed;
                return;
            }

            // Trend-pullback arm (kept from v1, ungated): below mid-band with bearish MACD posture,
            // expecting MACD recovery to confirm. This is the momentum arm; the ADX gate per T4 is
            // secondary and intentionally applied to the mean-reversion arm only.
            if (close < bb.MiddleBand && _macd < _macd.Signal)
            {
                _armedByLowerBand = false;
                _phase = Phase.Armed;
            }
        }

        private void HandleArmed(decimal close)
        {
            // If a mean-reversion arm aged out of the trend regime, disarm instead of holding a
            // stale signal (cheap re-check of the same gate).
            if (_armedByLowerBand && !IsConfirmedUpTrend())
            {
                _phase = Phase.Flat;
                return;
            }

            if (IsOkToBuy(close))
            {
                var quantity = RiskBasedQuantity(close);
                if (quantity >= 0.01m)   // Coinbase ETHEUR min order size
                {
                    MarketOrder(_symbol, quantity);
                    // State transition happens on the FILLED order event (as in v1), not here.
                }
                _phase = Phase.Flat; // disarm either way; fill event moves us to Long
            }
            // Unarmed stall: an arm is only valid for one confirmation bar, mirroring v1's
            // same-bar arm->confirm cadence without letting signals stack up.
            else
            {
                _phase = Phase.Flat;
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
            var histogram = _macd.Histogram;
            var macdAboveSignal = _macd > _macd.Signal;
            var histogramRising = histogram.IsReady && _prevHistogram.HasValue
                && histogram.Current.Value > _prevHistogram.Value;
            _prevHistogram = histogram.IsReady ? histogram.Current.Value : (decimal?)null;
            var aboveLowerBand = close > _bollingerBands.LowerBand;

            // MACD must be recovering while price has stepped back above the lower band:
            // v1 fired on slope>=0 (never binding as a veto); this is the same intent, evaluable.
            return macdAboveSignal && histogramRising && aboveLowerBand;
        }

        private decimal RiskBasedQuantity(decimal entry)
        {
            // (d) size so (entry - stop) * qty <= 2% equity, capped at 80% of free cash.
            var stopDistance = StopDistance(entry);
            var equity = Portfolio.TotalPortfolioValue;
            var riskQuantity = (RiskPerTrade * equity) / stopDistance;
            var cashQuantity = (Portfolio.CashBook[CurrencyName].Amount * MaxCashFraction) / entry;
            var qty = Math.Min(riskQuantity, cashQuantity);
            qty = Math.Truncate(qty * 1000m) / 1000m;   // 1e-3 ETH, same rounding discipline as v1
            return Math.Max(qty, 0m);
        }

        private decimal StopDistance(decimal entry)
        {
            // (a) entry-anchored stop distance: max(3%, 2 x ATR(14)) evaluated at entry time (kb-fixed-vs-trailing test-arm rationale).
            return Math.Max(MinStopDistance * entry, AtrStopMultiple * _atr.Current.Value);
        }

        // ---- Exit path (guaranteed reachable from any state; fixes T4 defect D4) ----------------

        private void HandleLong(decimal close)
        {
            _hoursHeld++;
            _peakPrice = Math.Max(_peakPrice, close);

            // 1. ATR ratchet trail (2.5xATR below the running peak) - UNCONDITIONAL on breach.
            //    Replaces v1's fixed +4% gate + 1% trail: no profit threshold can deadlock it,
            //    and it lets winners run in the regimes v1 capped away (T3 weakness #3).
            var trailPrice = _peakPrice - AtrTrailMultiple * _atr.Current.Value;
            if (close <= trailPrice)
            {
                CloseLong("ATR-trail");
                return;
            }

            // 2. Optional early profit-take keeping v1's RSI/BB confirmation style (card 3c),
            //    at a stricter level so it is a bonus exit, never the only door.
            if (_rsi > RsiProfitTakeLevel && close > _bollingerBands.UpperBand)
            {
                CloseLong("RSI/BB-profit-take");
                return;
            }

            // 3. Time-stop (T4 D4 hardening): no position lives past 45 days without an exit.
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
            // Phase returns to Flat via the Sell-Filled event (bookkeeping symmetry with v1).
        }

        // ---- Order-event bookkeeping -----------------------------------------------------------

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

                    // (a) broker-side protection immediately behind the entry.
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
                }
            }
            else if (orderEvent.Status == OrderStatus.Invalid || orderEvent.Status == OrderStatus.Canceled)
            {
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
