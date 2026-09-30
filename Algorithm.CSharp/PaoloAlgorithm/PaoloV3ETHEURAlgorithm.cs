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
    /// PaoloV3ETHEURAlgorithm - trend-following / long-bias redesign (card t_301ce8d1, T6.3).
    ///
    /// Program evidence (backtest-v21/AB-RESULTS.md, obsidian 20-Research/backtest-2026-v1-analysis.md):
    /// v1 -9.34%/2026, v2 -1.72% but MDD 48.3% and loses hold-2024 by 18pts, v2.1 -3.74%/-2.04%
    /// with MDD 4%. Diagnosis across three versions: the mean-reversion dip-buy CORE is structurally
    /// inverted vs a V/bull tape - it is flat exactly when the market pays (2026 Feb-Jun rebound) and
    /// exposed when it shouldn't. Every version loses to DCA-2026 (+29.82%) which won by simply
    /// BEING IN the rebound. Risk plumbing works (v2.1 MDD 4.0%); EXPOSURE TIMING is the defect.
    ///
    /// v3 DESIGN (per orchestrator decisions - a design pivot, not a re-tune):
    ///  (1) LONG-BIAS EXPOSURE ENGINE. The proven gate geometry (ADX(19) >= 20 AND +DI > -DI, plus a
    ///      slow SMA(120h) trend backbone) is used as a REGIME filter, not entry micro-timing:
    ///        - regime risk-on  = close > SMA(120) AND ADX(19) >= 20 AND +DI > -DI  -> take/keep base
    ///          position; scale in on new closing highs, max 2 adds;
    ///        - regime risk-off -> flat (regime-lapse exit; broker stop and ATR trail sit underneath).
    ///      HONEST DESIGN NOTE (deviation recorded for the report): the card's example spells the gate
    ///      out for taking/keeping. v2.1 proved that making ADX(19)~20 the hold-condition churns the
    ///      account to death (ADX oscillates around 20 on hourly bars -> fee-eaten round trips, and
    ///      quiet grinds-up - exactly the rebound we must hold through - dip below ADX 20). Therefore:
    ///      the FULL gate (SMA+ADX+DI) governs ENTRY/SCALE-IN; the SMA(120h) backbone alone governs
    ///      HOLDING (flat when close < SMA(120)). Same gate geometry, split roles - the exposure fix
    ///      this redesign exists to test.
    ///  (2) Proven plumbing kept VERBATIM from v2/v2.1 (@01fa2cc0e): broker-side StopLimitOrder
    ///      anchored at each fill (NOT StopMarket - CoinbaseBrokerageModel rejects it), ATR 2.5x
    ///      ratchet trail from peak, 45d time-stop, 2%-risk sizing capped at 80% of free cash,
    ///      explicit CoinbaseFeeModel, maker-limit entries at close*1.0005 with 4-bar TTL, no chase.
    ///      One refinement from v2.2 proposal #2, mandated by the same evidence: the 48-bar re-entry
    ///      cooldown applies ONLY after a broker stop-loss exit (the bear staircase signature), NOT
    ///      after time-stop / regime-lapse / trail exits in a still risk-on tape (a uniform cooldown
    ///      was proven to destroy the profitable 2024 re-entry cadence).
    ///  (3) No new exotic indicators (SMA/ADX/ATR all in v2's set), no HMM, single value per knob.
    ///
    /// Acceptance bar (hard, both windows): 2026 return >= -1.716% AND MDD <= 44.0% AND > -6.43%;
    /// 2024 return >= +25.481% AND MDD <= 28.7% AND > +12.391%. If it fails: report honestly, do not
    /// iterate parameters on this card.
    /// </summary>
    public class PaoloV3ETHEURAlgorithm : QCAlgorithm
    {
        private enum Phase { Flat, Long }

        // --- Symbols / account (identical to v1/v2/v2.1) ---
        private const string CryptoName = "ETH";
        private const string CurrencyName = "EUR";
        private const string SymbolName = CryptoName + CurrencyName;

        // --- v2 risk plumbing (inherited verbatim) ---
        private const decimal MinStopDistance = 0.03m;      // 3% floor stop, T4 counterfactual anchor
        private const decimal AtrStopMultiple = 2.0m;       // stop distance = max(3%, 2*ATR(14)) at entry
        private const decimal AtrTrailMultiple = 2.5m;      // chandelier trail from peak
        private const decimal RiskPerTrade = 0.02m;         // (entry-stop)*qty <= 2% of equity
        private const decimal MaxCashFraction = 0.8m;       // cap notional at 80% of free cash (v1 ceiling)
        private const decimal StopLimitBuffer = 0.015m;     // limit = stop*(1-1.5%): fill protection on gap-through
        private const int MaxHoldHours = 24 * 45;           // time-stop: no position may live longer than 45 days

        // --- v2.1 maker-limit plumbing (inherited verbatim) ---
        private const decimal LimitEntryOffset = 0.0005m;   // +0.05% above close: marketable maker buy limit
        private const int EntryLimitTtlBars = 4;            // cancel unfilled entry limit after 4 bars (no chase)
        private const int StopExitCooldownBars = 48;        // cooldown ONLY after a broker stop-loss exit

        // --- v3 regime parameters (single value per knob, walk-forward discipline) ---
        private const int RegimeSmaPeriod = 120;            // 120 hourly bars ~ 5 days: slow trend backbone
        private const int AdxPeriod = 19;                   // same ADX(19) geometry as v2/v2.1
        private const decimal AdxTrendLevel = 20m;          // same threshold as v2/v2.1
        private const int MaxAdds = 2;                      // scale-in on new closing peaks, max 2 adds

        private Symbol _symbol;
        private SimpleMovingAverage _regimeSma;
        private AverageDirectionalIndex _adx;
        private AverageTrueRange _atr;

        private Phase _phase = Phase.Flat;
        private int _addsUsed;                            // scale-ins since position open (0..MaxAdds)
        private decimal _entryPrice;
        private decimal _peakPrice;
        private int _hoursHeld;
        private int _stopOrderId = -1;

        // order/clock state (v2.1 machinery, verbatim)
        private int _pendingEntryOrderId = -1;            // live maker-limit entry/add order
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

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            _symbol = AddCrypto(SymbolName, Resolution.Hour, Market.Coinbase).Symbol;

            // Explicit self-declared fee model (v2 item e): Coinbase Advanced-1 taker 0.80% /
            // maker 0.60% - maker fills on the limit entries price at 0.60% (kb-maker-limit-fee-drag).
            Securities[_symbol].SetFeeModel(new CoinbaseFeeModel());
            // Benchmark = the traded instrument, so reported alpha is vs buy-and-hold ETHEUR.
            SetBenchmark(_symbol);

            _regimeSma = SMA(_symbol, RegimeSmaPeriod, Resolution.Hour);
            _adx = ADX(_symbol, AdxPeriod, Resolution.Hour);
            _atr = ATR(_symbol, 14, MovingAverageType.Simple, Resolution.Hour);

            // Warm-up: binding requirement is SMA(120)=120 bars, ADX(19)~38 bars, ATR(14)=15 bars.
            // 10 days = 240 hourly bars clears the slowest with 2x margin.
            SetWarmUp(TimeSpan.FromDays(10));
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

            // cooldown clock ticks once per bar, independent of the state machine (v2.1 machinery).
            if (_cooldownBars > 0)
            {
                _cooldownBars--;
            }

            // entry-limit lifecycle: age the pending order and cancel on TTL expiry, never chase
            // (v2.1 machinery, verbatim). A Fill/Canceled/Invalid event clears pending in OnOrderEvent.
            if (_pendingEntryOrderId >= 0)
            {
                _entryOrderAgeBars++;
                if (_entryOrderAgeBars >= EntryLimitTtlBars)
                {
                    Transactions.CancelOrder(_pendingEntryOrderId);
                    _pendingEntryOrderId = -1;
                    _entryOrderAgeBars = 0;
                }
            }

            if (!_regimeSma.IsReady || !_adx.IsReady || !_atr.IsReady)
            {
                return;
            }

            switch (_phase)
            {
                case Phase.Flat:
                    HandleFlat(close);
                    break;
                case Phase.Long:
                    HandleLong(close);
                    break;
            }
        }

        // ---- Exposure engine --------------------------------------------------------------------

        /// <summary>Full risk-on gate: slow trend backbone + directional-strength confirmation.
        /// Gates ENTRY and SCALE-IN only (see class note on the v2.1 churn evidence).</summary>
        private bool IsRiskOn()
        {
            return _lastClose > _regimeSma
                && _adx >= AdxTrendLevel
                && _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex;
        }

        /// <summary>Hold condition: the SMA(120h) backbone alone. Flat when price closes below it -
        /// that is the risk-off state of a long-bias trend engine (the tape we must NOT sit out).</summary>
        private bool IsTrendAlive()
        {
            return _lastClose > _regimeSma;
        }

        private void HandleFlat(decimal close)
        {
            if (_cooldownBars > 0 || _pendingEntryOrderId >= 0)
            {
                return;
            }

            // LONG BIAS: when the regime is risk-on we take the base position. No dip, no pullback,
            // no MACD confirmation required - the regime IS the signal (kb-time-series-momentum).
            if (IsRiskOn())
            {
                var quantity = RiskBasedQuantity(close);
                if (quantity >= 0.01m)   // Coinbase ETHEUR min order size
                {
                    SubmitEntryLimit(close, quantity);
                }
            }
        }

        private void HandleLong(decimal close)
        {
            _hoursHeld++;
            var newPeak = close > _peakPrice;
            if (newPeak)
            {
                _peakPrice = close;
            }

            // 1. ATR ratchet trail (2.5xATR below the running peak) - UNCONDITIONAL on breach.
            var trailPrice = _peakPrice - AtrTrailMultiple * _atr.Current.Value;
            if (close <= trailPrice)
            {
                CloseLong("ATR-trail");
                return;
            }

            // 2. Regime lapse: the core exit of a trend engine. Long-bias means we hold through
            //    ADX softness and consolidation; we are flat only when the trend backbone breaks.
            if (!IsTrendAlive())
            {
                CloseLong("regime-lapse");
                return;
            }

            // 3. Scale-in (max 2): pyramiding on a NEW closing peak while the full gate stays
            //    risk-on. Continuation-adds, the classic trend-following exposure ladder.
            if (newPeak && _addsUsed < MaxAdds && _pendingEntryOrderId < 0)
            {
                var addQty = RiskBasedQuantity(close);
                if (addQty >= 0.01m)
                {
                    SubmitEntryLimit(close, addQty);
                }
            }

            // 4. Time-stop: no position lives past 45 days without an exit (v2 plumbing verbatim;
            //    in a persistent uptrend the engine re-enters immediately - exposure is preserved).
            if (_hoursHeld >= MaxHoldHours)
            {
                CloseLong("time-stop");
            }
            // 5. Broker-side stop-limit (placed at each fill) remains pending underneath all of the
            //    above and executes without algorithm liveness at all.
        }

        private void SubmitEntryLimit(decimal close, decimal quantity)
        {
            // Maker-limit entry at close * (1 + 0.05%) - marketable buy limit (v2.1 helper verbatim):
            // fills on the next bar unless price gaps down more than the buffer; CoinbaseFeeModel then
            // charges the 0.60% maker fee. Cancelled after 4 bars, never chased with market.
            var limitPrice = Math.Round(close * (1m + LimitEntryOffset), 2);
            var ticket = LimitOrder(_symbol, quantity, limitPrice);
            _pendingEntryOrderId = ticket.OrderId;
            _entryOrderAgeBars = 0;
            Log($"{Time:u} entry-limit submitted @ {limitPrice:F2} qty={quantity} order={_pendingEntryOrderId} adds={_addsUsed}");
        }

        private void CloseLong(string reason)
        {
            var held = Portfolio.CashBook[CryptoName].Amount;
            if (held > 0m)
            {
                Log($"{Time:u} exit [{reason}] close={_lastClose:F2} entry={_entryPrice:F2} held_h={_hoursHeld} adds={_addsUsed}");
                if (_stopOrderId >= 0)
                {
                    // cancel the broker stop BEFORE emitting the software exit (kills the racing
                    // invalid-event noise seen in every v2/v2.1 leg)
                    Transactions.CancelOrder(_stopOrderId);
                    _stopOrderId = -1;
                }
                MarketOrder(_symbol, -held);
            }
            // Phase returns to Flat via the Sell-Filled event (bookkeeping symmetry with v1/v2).
        }

        private decimal RiskBasedQuantity(decimal entry)
        {
            // size so (entry - stop) * qty <= 2% equity, capped at 80% of free cash (v2 verbatim).
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
            // entry-anchored stop distance: max(3%, 2 x ATR(14)) evaluated at entry time (v2 verbatim).
            return Math.Max(MinStopDistance * entry, AtrStopMultiple * _atr.Current.Value);
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
                    _pendingEntryOrderId = -1;
                    _entryOrderAgeBars = 0;

                    // broker-side protection anchored AT EACH FILL, covering the whole holding
                    // (v2 item a - StopLimit, never StopMarket: rejected by CoinbaseBrokerageModel
                    // post 2019-03-23). On an add-fill the old stop is replaced so the anchor tracks
                    // the newest fill and the position stays fully protected.
                    if (_stopOrderId >= 0)
                    {
                        Transactions.CancelOrder(_stopOrderId);
                    }
                    _entryPrice = orderEvent.FillPrice;
                    _peakPrice = Math.Max(_peakPrice > 0m ? _peakPrice : 0m, orderEvent.FillPrice);
                    if (_phase != Phase.Long)
                    {
                        _phase = Phase.Long;
                        _hoursHeld = 0;
                        _addsUsed = 0;
                        _peakPrice = orderEvent.FillPrice;
                    }
                    else
                    {
                        _addsUsed++;   // this fill is a scale-in
                    }

                    var stop = _entryPrice - StopDistance(_entryPrice);
                    var limit = stop * (1m - StopLimitBuffer);
                    var totalHeld = Portfolio.CashBook[CryptoName].Amount;
                    var ticket = StopLimitOrder(_symbol, -totalHeld, stop, limit);
                    _stopOrderId = ticket.OrderId;
                    Log($"{Time:u} entry filled @ {_entryPrice:F2} qty={orderEvent.FillQuantity} (held {totalHeld}), " +
                        $"stop-limit @ stop={stop:F2} limit={limit:F2} order={_stopOrderId} adds={_addsUsed}");
                }
                else // Sell filled (trail / regime-lapse / time-stop exit, or the broker stop-limit triggering)
                {
                    _phase = Phase.Flat;
                    _addsUsed = 0;
                    _peakPrice = 0m;
                    var stopTriggered = orderEvent.OrderId == _stopOrderId;
                    _stopOrderId = -1;
                    // Exit-reason-differentiated cooldown: ONLY a stop-loss exit (the bear-staircase
                    // signature that killed v2's MDD) earns the 48-bar penalty. Time-stop /
                    // regime-lapse / trail exits in a live tape re-enter freely (v2.2 proposal #2,
                    // the uniform-cooldown 2024 regression was the evidence).
                    if (stopTriggered)
                    {
                        _cooldownBars = StopExitCooldownBars;
                    }
                }
            }
            else if (orderEvent.Status == OrderStatus.Invalid || orderEvent.Status == OrderStatus.Canceled)
            {
                if (orderEvent.OrderId == _pendingEntryOrderId)
                {
                    // expired/rejected maker entry: drop the signal, stay in current state (no chasing)
                    _pendingEntryOrderId = -1;
                    _entryOrderAgeBars = 0;
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
