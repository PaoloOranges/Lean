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
using System.Collections.Generic;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm
{
    /// <summary>
    /// T6.3 cap-only control leg (C2) engine - card t_3a66114e, AB-PLAN-T6.3.md rev 2 §2.2.
    ///
    /// Base line: v2 @ 00686124b policy, built on the v2.1 mechanics (01fa2cc0e) that T6.2-
    /// CONFIRMED-SCOPE.md pins as the locked parameter set (both arms gated, tagged cooldowns,
    /// maker-limit entries), plus the §1a G-B cadence policy the owner went on 2026-09-30:
    ///   - MAX_ENTRIES_PER_MONTH = 4, hard code counter, SHARED across all arms (cap per program,
    ///     not per arm, so arm-hopping cannot evade it). Unfilled TTL cancels consume no slot.
    ///   - MIN_HOLD_FLOOR_BARS = 24: no NON-STOP software exit before 24 bars held. The broker
    ///     stop-limit is EXEMPT (§4 of the proposal: the floor protects turnover, not capital).
    /// Fee model: CoinbaseProFeeModel (t_3409ddf9 fix, owner-confirmed Pro base 0.60% taker /
    /// 0.40% maker) - NOT the Advanced-1 default. Rebased onto paolo/eth-eur-algo-v2-fee-fix
    /// @ bd916f817 per operator instruction; the fee model is reused, not reimplemented.
    ///
    /// EXECUTION DEVIATION (operator directive, 2026-09-30 mid-run, recorded in AB report):
    /// the proposal drafts entries at close*(1+0.05%) as v2.1 did - that is a *marketable* limit
    /// and LEAN's CoinbaseFeeModel prices marketable limits as TAKER (0.60%), so the drafted
    /// form never delivers the 0.40% maker assumption behind P4/K4 fee accounting (root cause
    /// t_3409ddf9: "maker entries were maker in intent, not in execution"). v2.3 entries are
    /// therefore NON-MARKETABLE POST-ONLY buy limits at close*(1 - 0.05%): below the touch,
    /// PostOnly=true (fee model forces maker classification; a crossing fill is rejected on the
    /// real venue), TTL 4 bars, no chase (TTL cancel disarms; cadence slot consumed only on fill).
    /// The 0.05% magnitude is preserved; the sign flipped to satisfy the maker requirement.
    ///
    /// Hysteresis gate (T6.2 locked, now a real state machine - v2.1 evaluated ADX>=20 per bar
    /// which silently re-armed; the confirmed scope defines hysteresis, proposal §3 restates it):
    ///   open:  ADX(19) >= 20 AND +DI(19) > -DI(19)   (single-bar open event, fresh-open required)
    ///   hold:  ADX >= 15 (+DI>-DI re-verified at every ENTRY instant - all three arms)
    ///   close: ADX < 15; re-entry requires a full disarm + fresh open event (invariant §3 K3).
    ///
    /// Tagged cooldowns (T6.2 §2 table, deterministic from the exit tag, never inferred ex-post):
    ///   broker stop / any loss exit -> 48 | profitable trail exit, gate OPEN -> 0 |
    ///   profitable trail, gate CLOSED -> 12 | regime-lapse -> 12 | time-stop -> 12 | other -> 12.
    /// Cancel-before-exit: the broker stop is cancelled BEFORE the software-exit market order is
    /// emitted (v3-proven: 0 invalid order events).
    ///
    /// Diagnostics (first-class per AB-PLAN §5): every bar logs
    ///   EQUI|&lt;utc&gt;|&lt;equity&gt;|&lt;posEth&gt;|&lt;phase&gt;|&lt;gate&gt;|&lt;entriesThisMonth&gt;
    /// every fill logs FILL|side|price|qty|fee|impliedRate|tag; guard counters are summed in
    /// OnEndOfAlgorithm (DIAG line). K1 replay, TIM, sub-window returns and the fee-implied-rate
    /// proof all parse from these lines.
    /// </summary>
    public class PaoloV23CapETHEURAlgorithm : QCAlgorithm
    {
        protected enum Phase { Flat, Armed, Long }
        protected enum Arm { None, LowerBand, Pullback, Breakout }

        // --- Symbols / account (identical to v1/v2/v2.1) ---
        private const string CryptoName = "ETH";
        private const string CurrencyName = "EUR";
        private const string SymbolName = CryptoName + CurrencyName;

        // --- locked risk parameters, verbatim v2/v2.1 (proposal §7 inheritance table) ---
        private const decimal MinStopDistance = 0.03m;      // stop = entry - max(3%, 2*ATR(14))
        private const decimal AtrStopMultiple = 2.0m;
        private const decimal AtrTrailMultiple = 2.5m;      // chandelier trail, floor-gated at 24 bars
        private const decimal RiskPerTrade = 0.02m;         // (entry-stop)*qty <= 2% equity
        private const decimal MaxCashFraction = 0.8m;       // notional <= 80% free cash
        private const decimal StopLimitBuffer = 0.015m;     // stop-limit limit = stop*(1-1.5%)
        private const int MaxHoldHours = 24 * 45;           // 45-day time-stop
        private const decimal RsiProfitTakeLevel = 70m;

        // --- gate constants (locked 20/15 hysteresis) ---
        private const decimal GateOpenAdx = 20m;
        private const decimal GateHoldAdx = 15m;

        // --- cooldowns (locked 48/0/12 mapping) ---
        private const int CooldownStop = 48;
        private const int CooldownTrailGateOpen = 0;
        private const int CooldownOther = 12;

        // --- entry execution (post-only maker; see EXECUTION DEVIATION in class header) ---
        private const decimal LimitEntryDiscount = 0.0005m; // BELOW close: non-marketable by construction
        private const int EntryLimitTtlBars = 4;            // no chase; TTL cancel consumes no slot

        // --- NEW constants, exactly the §1a G-B policy pair + the arm's one knob ---
        protected const int MaxEntriesPerMonth = 4;         // hard counter, all arms combined
        protected const int MinHoldFloorBars = 24;          // non-stop exits only (broker stop exempt)
        protected const int BreakoutLookbackBars = 24;      // 1 day of hourly bars; pinned, no tuning (OQ2)

        private const int AtrPeriod = 14;
        private const int AdxPeriod = 19;
        private const int RsiPeriod = 26;
        private const int BbPeriod = 26;
        private const int VeryFastPeriod = 12;
        private const int FastPeriod = 26;
        private const int SlowPeriod = 55;

        private Symbol _symbol;
        private MovingAverageConvergenceDivergence _macd;
        private AverageDirectionalIndex _adx;
        private RelativeStrengthIndex _rsi;
        private BollingerBands _bollingerBands;
        private AverageTrueRange _atr;

        // --- state machine ---
        private Phase _phase = Phase.Flat;
        private Arm _armedBy = Arm.None;
        private Arm _openedBy = Arm.None;
        private int _armedAgeBars;
        private bool _gateOpen;                              // hysteresis state (single instance, all arms)
        private decimal? _prevHistogram;
        private bool _histRising;
        private decimal _entryPrice;
        private decimal _peakPrice;
        private int _hoursHeld;
        private int _stopOrderId = -1;
        private int _pendingEntryOrderId = -1;               // live maker entry (single in-flight entry, no stacking)
        private Arm _pendingEntryTag = Arm.None;
        private int _entryOrderAgeBars;
        private int _cooldownBars;
        private int _pendingExitOrderId = -1;
        private int _pendingExitCooldown = -1;
        private decimal _lastClose;

        // --- cadence counter (§1a G-B / K2) ---
        private int _monthKey;                               // year*100+month of the current counter
        private int _entriesThisMonth;
        private int _maxEntriesInAnyMonth;
        private int _capBoundBars;                           // bars blocked by the cap and nothing else
        private readonly Queue<decimal> _closingHigh = new Queue<decimal>(); // rolling 24-bar close high

        // --- diagnostics / invariant counters (all must obey K2/K3) ---
        private int _ttlCancels;
        private int _regimeLapseExits;
        private int _guardEvents;                            // bear-reentry-guard / K3 violations (must be 0)
        private int _k2Events;                               // blocked/attempted over-cap fills (must be 0)
        private int _takerMisclassifiedEntries;              // entry fills with implied fee >= 0.005 (must be 0)
        private decimal _minEntryImpliedRate = decimal.MaxValue;
        private decimal _maxEntryImpliedRate;

        private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

        /// <summary>Treatment hook: PaoloV23ETHEURAlgorithm flips this to true. False = C2 leg.</summary>
        protected virtual bool TrendArmEnabled { get { return false; } }

        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.Coinbase, AccountType.Cash);
            SetTimeZone(NodaTime.DateTimeZone.Utc);

            var startDate = ParseDateParameter("start-date", new DateTime(2026, 1, 1));
            var endDate = ParseDateParameter("end-date", new DateTime(2026, 9, 27));
            SetStartDate(startDate);
            SetEndDate(endDate);

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            _symbol = AddCrypto(SymbolName, Resolution.Hour, Market.Coinbase).Symbol;

            // CORRECTED fee tier (t_3409ddf9): Pro base maker 0.40% / taker 0.60%. Declared on
            // the security directly (fee models are consulted at fill time).
            Securities[_symbol].SetFeeModel(new CoinbaseProFeeModel());
            SetBenchmark(_symbol);

            _macd = MACD(_symbol, VeryFastPeriod, SlowPeriod, FastPeriod, MovingAverageType.Exponential, Resolution.Hour);
            _adx = ADX(_symbol, AdxPeriod, Resolution.Hour);
            _rsi = RSI(_symbol, RsiPeriod, MovingAverageType.Exponential);
            _bollingerBands = BB(_symbol, BbPeriod, 1.0m, MovingAverageType.Simple);
            _atr = ATR(_symbol, AtrPeriod, MovingAverageType.Simple, Resolution.Hour);

            SetWarmUp(TimeSpan.FromDays(7));

            Debug($"t_3a661149 v2.3 engine: trendArm={TrendArmEnabled} cap={MaxEntriesPerMonth}/mo floor={MinHoldFloorBars} bars " +
                  $"fees=Pro(0.004/0.006) entries=POST-ONLY limit close*(1-0.0005) TTL {EntryLimitTtlBars} no-chase");
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

            // per-bar equity ledger (K1 replay / TIM / sub-window returns parse this line)
            Log($"EQUI|{Time:u}|{Portfolio.TotalPortfolioValue:F4}|{Portfolio.CashBook[CryptoName].Amount}|{(int)_phase}|{(_gateOpen ? 1 : 0)}|{_entriesThisMonth}");

            if (_cooldownBars > 0)
            {
                _cooldownBars--;
            }

            // entry-limit lifecycle: age + TTL cancel (no chase). TTL cancel consumes no cadence slot.
            if (_pendingEntryOrderId >= 0)
            {
                _entryOrderAgeBars++;
                if (_entryOrderAgeBars >= EntryLimitTtlBars)
                {
                    Transactions.CancelOrder(_pendingEntryOrderId);
                    // state cleared on the Canceled event; counted here for the report row
                    _ttlCancels++;
                }
            }

            UpdateHysteresisGate();
            RollMonth();

            if (!_macd.IsReady || !_adx.IsReady || !_rsi.IsReady || !_bollingerBands.IsReady || !_atr.IsReady)
            {
                return;
            }

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

            // queue updated AFTER the state machine so the breakout predicate
            // close > max(close[t-24..t-1]) excludes the CURRENT bar (proposal §2);
            // enqueuing before evaluation would make the strict > unsatisfiable.
            RollClosingHigh(close);
        }

        // ---- gate (shared singleton state machine; arms are consumers, proposal §3) ------------

        private void UpdateHysteresisGate()
        {
            if (!_adx.IsReady)
            {
                return;
            }
            if (!_gateOpen)
            {
                // fresh open event: ADX >= 20 AND +DI > -DI on the same bar
                if (_adx >= GateOpenAdx && PositiveDiLeads())
                {
                    _gateOpen = true;
                    Log($"GATE|{Time:u}|open|adx={_adx.Current.Value:F2}");
                }
            }
            else if (_adx < GateHoldAdx)
            {
                // close is single-bar on ADX < 15; re-entry needs a full disarm + fresh open (K3)
                _gateOpen = false;
                Log($"GATE|{Time:u}|close|adx={_adx.Current.Value:F2}");
            }
        }

        private bool PositiveDiLeads()
        {
            return _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex;
        }

        /// <summary>Gate-open AND entry-time +DI>-DI (all arms; memo §5.2, not waivable).</summary>
        private bool EntryRegimeOk()
        {
            return _gateOpen && PositiveDiLeads();
        }

        // ---- cadence (K2): hard monthly counter, all arms combined -------------------------------

        private void RollMonth()
        {
            var key = Time.Year * 100 + Time.Month;
            if (key != _monthKey)
            {
                _monthKey = key;
                _entriesThisMonth = 0;
            }
        }

        private bool CadenceOk()
        {
            return _entriesThisMonth < MaxEntriesPerMonth;
        }

        // ---- rolling 24-bar close high (breakout predicate; current bar EXCLUDED) ----------------

        private void RollClosingHigh(decimal close)
        {
            _closingHigh.Enqueue(close);
            while (_closingHigh.Count > BreakoutLookbackBars)
            {
                _closingHigh.Dequeue();
            }
        }

        private bool HasFullHighWindow()
        {
            return _closingHigh.Count == BreakoutLookbackBars;
        }

        private decimal MaxRecentClose()
        {
            var max = decimal.MinValue;
            foreach (var c in _closingHigh)
            {
                if (c > max) max = c;
            }
            return max;
        }

        // ---- entry path --------------------------------------------------------------------------

        private void HandleFlat(decimal close)
        {
            if (_cooldownBars > 0 || _pendingEntryOrderId >= 0)
            {
                return;
            }
            if (!EntryRegimeOk())
            {
                return;
            }
            if (!CadenceOk())
            {
                // cap-bound diagnostic (proposal §2a: if the cap binds, that itself is a finding)
                _capBoundBars++;
                return;
            }

            var bb = _bollingerBands;

            // CONTINUATION ARM (T leg only) - predicate-DISJOINT from both dip arms:
            //   breakout:  close > BB(26,1,SMA).MiddleBand  AND  close > max(close[t-24..t-1])
            //   dip A:     close < BB.LowerBand              (LowerBand < MiddleBand always)
            //   dip B:     close < BB.MiddleBand
            // close > MiddleBand contradicts close < LowerBand and close < MiddleBand on the SAME
            // bar (LowerBand < MiddleBand is an identity of the bands) => trigger sets share no bar.
            // Additionally: fresh +DI>-DI at this instant (EntryRegimeOk above), shared cooldown,
            // cadence headroom. Arm fires on the breakout bar ITSELF (the bar IS the confirmation;
            // proposal §2 - a confirmation lag would buy the 2nd bar of every breakout).
            if (TrendArmEnabled && HasFullHighWindow() && close > bb.MiddleBand && close > MaxRecentClose())
            {
                if (_phase == Phase.Armed && _armedAgeBars < EntryLimitTtlBars)
                {
                    // one armed tag per bar (proposal §5 rule 2): a fresh dip arm (< TTL old) is
                    // protected; the breakout simply waits for the next qualifying bar.
                    return;
                }
                _armedBy = Arm.Breakout;
                _armedAgeBars = 0;
                _phase = Phase.Armed;
                // same-bar entry attempt: fall through to HandleArmed immediately below
                TryEnterArmed(close);
                return;
            }

            // Dip arm A: mean-reversion below the lower band (gated, as v2.1).
            if (close < bb.LowerBand)
            {
                _armedBy = Arm.LowerBand;
                _armedAgeBars = 0;
                _phase = Phase.Armed;
                return;
            }
            // Dip arm B: trend-pullback below mid-band with bearish MACD posture (as v2.1).
            if (close < bb.MiddleBand && _macd < _macd.Signal)
            {
                _armedBy = Arm.Pullback;
                _armedAgeBars = 0;
                _phase = Phase.Armed;
            }
        }

        private void HandleArmed(decimal close)
        {
            _armedAgeBars++;
            // a stale arm disarms the moment the (entry-time) regime lapses - mirrors v2.1
            if (!EntryRegimeOk())
            {
                _phase = Phase.Flat;
                _armedBy = Arm.None;
                return;
            }
            // Breakout arms and enters on the same bar (already handled in HandleFlat); only the
            // dip arms need the 1-bar MACD-recovery confirmation, as v2.1.
            if (_armedBy != Arm.Breakout && IsOkToBuy(close))
            {
                TryEnterArmed(close);
            }
        }

        private void TryEnterArmed(decimal close)
        {
            var arm = _armedBy;
            _phase = Phase.Flat;      // disarm either way; the FILLED event moves us to Long
            _armedBy = Arm.None;

            // K3 runtime guard (invariant §3): this submit point is only reachable through
            // EntryRegimeOk() this bar; a violation here would mean a code path bypassed the gate.
            if (!EntryRegimeOk() || !CadenceOk())
            {
                _guardEvents++;
                Log($"GUARD|{Time:u}|bear-reentry-guard|arm={arm}|gate={_gateOpen}|di={PositiveDiLeads()}|entriesThisMonth={_entriesThisMonth}");
                return;
            }

            var quantity = RiskBasedQuantity(close);
            if (quantity < 0.01m)   // Coinbase ETHEUR min order size
            {
                return;
            }

            // POST-ONLY non-marketable maker entry (see EXECUTION DEVIATION, class header).
            // Below the touch => !IsMarketable; PostOnly=true => CoinbaseFeeModel maker branch
            // unconditionally (a crossing fill would be rejected on the real venue).
            var limitPrice = Math.Round(close * (1m - LimitEntryDiscount), 2);
            var ticket = LimitOrder(_symbol, quantity, limitPrice,
                orderProperties: new CoinbaseOrderProperties { PostOnly = true });
            _pendingEntryOrderId = ticket.OrderId;
            _pendingEntryTag = arm;
            _entryOrderAgeBars = 0;
            Log($"ENTRY|{Time:u}|arm={arm}|limit={limitPrice:F2}|qty={quantity}|ttl={EntryLimitTtlBars}");
        }

        private bool IsOkToBuy(decimal close)
        {
            return _macd > _macd.Signal && _histRising && close > _bollingerBands.LowerBand;
        }

        private decimal RiskBasedQuantity(decimal entry)
        {
            var stopDistance = StopDistance(entry);
            var equity = Portfolio.TotalPortfolioValue;
            var riskQuantity = (RiskPerTrade * equity) / stopDistance;
            var cashQuantity = (Portfolio.CashBook[CurrencyName].Amount * MaxCashFraction) / entry;
            var qty = Math.Min(riskQuantity, cashQuantity);
            qty = Math.Truncate(qty * 1000m) / 1000m;   // 1e-3 ETH
            return Math.Max(qty, 0m);
        }

        private decimal StopDistance(decimal entry)
        {
            return Math.Max(MinStopDistance * entry, AtrStopMultiple * _atr.Current.Value);
        }

        // ---- exit path (identical for all arms - the arm cannot smuggle looser stops) ------------

        private void HandleLong(decimal close)
        {
            _hoursHeld++;
            _peakPrice = Math.Max(_peakPrice, close);

            // MIN_HOLD_FLOOR_BARS = 24 guards every NON-STOP exit below. The broker stop-limit
            // anchored at entry is exempt by construction (it lives at the brokerage, proposal §4).
            var floorOk = _hoursHeld >= MinHoldFloorBars;

            // 1. Chandelier trail (2.5xATR below the running peak), floor-gated.
            if (floorOk)
            {
                var trailPrice = _peakPrice - AtrTrailMultiple * _atr.Current.Value;
                if (close <= trailPrice)
                {
                    CloseLong("ATR-trail");
                    return;
                }

                // 2. RSI/BB bonus profit-take (inherited), floor-gated.
                if (_rsi > RsiProfitTakeLevel && close > _bollingerBands.UpperBand)
                {
                    CloseLong("RSI/BB-profit-take");
                    return;
                }

                // 3. REGIME-LAPSE EXIT (T leg only, breakout-opened positions): the breakout thesis
                //    IS the directional regime, so a gate close (ADX<15) falsifies it directly.
                //    Tag regime_lapse -> 12-bar cooldown (proposal §4).
                if (TrendArmEnabled && _openedBy == Arm.Breakout && !_gateOpen)
                {
                    _regimeLapseExits++;
                    CloseLong("regime-lapse");
                    return;
                }
            }

            // 4. Time-stop (1080 bars >> floor; floor never conflicts).
            if (_hoursHeld >= MaxHoldHours)
            {
                CloseLong("time-stop");
            }
        }

        private void CloseLong(string reason)
        {
            // deterministic cooldown from the exit TAG (T6.2 §2; never inferred from PnL after the fact)
            var exitPnl = _lastClose - _entryPrice;
            int cooldown;
            if (reason == "ATR-trail")
            {
                cooldown = exitPnl > 0m
                    ? (_gateOpen ? CooldownTrailGateOpen : CooldownOther)
                    : CooldownStop;                      // "any loss exit" branch
            }
            else if (reason == "RSI/BB-profit-take")
            {
                cooldown = exitPnl > 0m ? CooldownOther : CooldownStop;
            }
            else
            {
                cooldown = CooldownOther;                // regime-lapse / time-stop / other
            }
            _pendingExitCooldown = cooldown;

            // CANCEL-BEFORE-EXIT (v3-proven, 0 invalid events): cancel the broker stop FIRST,
            // same time step, then emit the software-exit market order.
            if (_stopOrderId >= 0)
            {
                Transactions.CancelOrder(_stopOrderId);
                _stopOrderId = -1;
            }

            var held = Portfolio.CashBook[CryptoName].Amount;
            if (held > 0m)
            {
                var ticket = MarketOrder(_symbol, -held);
                _pendingExitOrderId = ticket.OrderId;
            }
            Log($"EXIT|{Time:u}|reason={reason}|arm={_openedBy}|close={_lastClose:F2}|entry={_entryPrice:F2}|held_h={_hoursHeld}|cd={cooldown}");
        }

        // ---- order-event bookkeeping --------------------------------------------------------------

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null)
            {
                return;
            }

            if (orderEvent.Status == OrderStatus.Filled)
            {
                var fee = orderEvent.OrderFee != null ? orderEvent.OrderFee.Value.Amount : 0m;
                var notional = orderEvent.FillPrice * orderEvent.AbsoluteFillQuantity;
                var implied = notional > 0m ? fee / notional : 0m;

                if (orderEvent.Direction == OrderDirection.Buy)
                {
                    // --- K2 (cap) runtime verification at the fill, not just at submit ---
                    RollMonth();
                    if (_entriesThisMonth >= MaxEntriesPerMonth)
                    {
                        _k2Events++;
                        // count the fill in the month ledger (it IS a fill; hiding it would
                        // corrupt the trades/month report row) then flatten to neutral.
                        _entriesThisMonth++;
                        if (_entriesThisMonth > _maxEntriesInAnyMonth)
                        {
                            _maxEntriesInAnyMonth = _entriesThisMonth;
                        }
                        Log($"K2|{Time:u}|over-cap fill flattened-to-neutral|month={_monthKey}|count={_entriesThisMonth}");
                        FlatNow();
                        return;
                    }
                    // --- K3 (bear-no-re-entry) runtime verification at the fill ---
                    if (!EntryRegimeOk())
                    {
                        _guardEvents++;
                        Log($"K3|{Time:u}|bear-reentry-guard at fill|gate={_gateOpen}|di={PositiveDiLeads()}");
                        FlatNow();
                        return;
                    }
                    // --- disjointness runtime proof: a breakout fill must NOT be a dip fill ---
                    if (_pendingEntryTag == Arm.Breakout &&
                        (_lastClose < _bollingerBands.MiddleBand || _lastClose < _bollingerBands.LowerBand))
                    {
                        _guardEvents++;
                        Log($"K3|{Time:u}|disjointness-violation breakout fill below mid band");
                    }

                    _entriesThisMonth++;
                    if (_entriesThisMonth > _maxEntriesInAnyMonth)
                    {
                        _maxEntriesInAnyMonth = _entriesThisMonth;
                    }
                    _minEntryImpliedRate = Math.Min(_minEntryImpliedRate, implied);
                    _maxEntryImpliedRate = Math.Max(_maxEntryImpliedRate, implied);
                    if (implied >= 0.005m)
                    {
                        _takerMisclassifiedEntries++;   // must be 0: entries owe the maker rate 0.0040
                    }

                    _phase = Phase.Long;
                    _openedBy = _pendingEntryTag;
                    _entryPrice = orderEvent.FillPrice;
                    _peakPrice = orderEvent.FillPrice;
                    _hoursHeld = 0;
                    _pendingEntryOrderId = -1;
                    _pendingEntryTag = Arm.None;
                    _entryOrderAgeBars = 0;

                    var stop = _entryPrice - StopDistance(_entryPrice);
                    var limit = stop * (1m - StopLimitBuffer);
                    var ticket = StopLimitOrder(_symbol, -orderEvent.FillQuantity, stop, limit);
                    _stopOrderId = ticket.OrderId;
                    Log($"FILL|{Time:u}|buy|price={orderEvent.FillPrice:F2}|qty={orderEvent.FillQuantity}|fee={fee:F4}|implied={implied:F5}|arm={_openedBy}|stop={stop:F2}");
                }
                else // Sell filled: software exit, broker-stop trigger, or an emergency flatten
                {
                    int cd;
                    if (_pendingExitOrderId >= 0 && orderEvent.OrderId == _pendingExitOrderId)
                    {
                        cd = _pendingExitCooldown;               // tagged software exit
                    }
                    else if (orderEvent.OrderId == _stopOrderId)
                    {
                        cd = CooldownStop;                       // broker stop-limit triggered
                    }
                    else
                    {
                        cd = CooldownStop;                       // unknown sell -> conservative 48
                    }
                    Log($"FILL|{Time:u}|sell|price={orderEvent.FillPrice:F2}|qty={orderEvent.FillQuantity}|fee={fee:F4}|implied={implied:F5}|arm={_openedBy}|cd={cd}");
                    _phase = Phase.Flat;
                    _openedBy = Arm.None;
                    _stopOrderId = -1;
                    _pendingExitOrderId = -1;
                    _pendingExitCooldown = -1;
                    _cooldownBars = cd;
                }
            }
            else if (orderEvent.Status == OrderStatus.Invalid || orderEvent.Status == OrderStatus.Canceled)
            {
                if (orderEvent.OrderId == _pendingEntryOrderId)
                {
                    // TTL/no-chase: disarm fully; NO cadence slot consumed (only fills count)
                    _pendingEntryOrderId = -1;
                    _pendingEntryTag = Arm.None;
                    _entryOrderAgeBars = 0;
                }
                if (orderEvent.OrderId == _stopOrderId && _phase != Phase.Long)
                {
                    _stopOrderId = -1;
                }
            }
        }

        private void FlatNow()
        {
            var held = Portfolio.CashBook[CryptoName].Amount;
            if (held > 0m)
            {
                if (_stopOrderId >= 0)
                {
                    Transactions.CancelOrder(_stopOrderId);
                    _stopOrderId = -1;
                }
                MarketOrder(_symbol, -held);
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"DIAG|trendArm={TrendArmEnabled}|maxEntriesPerMonth={_maxEntriesInAnyMonth}|capBoundBars={_capBoundBars}" +
                $"|ttlCancels={_ttlCancels}|regimeLapseExits={_regimeLapseExits}|guardEvents={_guardEvents}|k2Events={_k2Events}" +
                $"|takerMisclassifiedEntries={_takerMisclassifiedEntries}|entryImpliedRate=[{_minEntryImpliedRate},{_maxEntryImpliedRate}]" +
                $"|endEquity={Portfolio.TotalPortfolioValue:F4}");
        }
    }

    /// <summary>
    /// T6.3 TREATMENT leg (T) - card t_3a66114e, proposal PROPOSAL-T6.3-TREND-ARM.md rev 2 §2.
    ///
    /// Identical to <see cref="PaoloV23CapETHEURAlgorithm"/> (C2) in EVERY mechanic - dip arms,
    /// hysteresis gate, tagged cooldowns, post-only maker entries, monthly cap, hold floor,
    /// stop geometry, sizing, Pro fee tier - and differs by exactly one thing: the continuation
    /// arm is enabled (TrendArmEnabled=true), which adds
    ///   (a) the predicate-disjoint breakout entry (new 24-bar closing high above the mid band,
    ///       entry-time +DI>-DI, same shared gate/cooldown/cadence budget - see HandleFlat),
    ///   (b) the regime-lapse exit for breakout-opened positions (12-bar cooldown).
    /// Single position, NO scale-ins, NO pyramiding (v3 postmortem: adds × chop was a named loss
    /// mechanism). Cap + floor are inherited from C2, so T - C2 is the arm effect with one change
    /// surface (AB-PLAN rev 2 §2.2 attribution doctrine).
    /// </summary>
    public class PaoloV23ETHEURAlgorithm : PaoloV23CapETHEURAlgorithm
    {
        protected override bool TrendArmEnabled { get { return true; } }
    }

    /// <summary>
    /// OBSERVATION-ONLY probe for the C control legs (AB-PLAN §6.1 K1 needs the control's
    /// point-in-time drawdown curve). Wraps the pinned PaoloV2RepricedETHEURAlgorithm
    /// (t_3409ddf9, unmodified v2 logic at Pro fees) and adds the EQUI ledger line after the
    /// base OnData. It submits no orders and mutates no state, so the equity path is identical
    /// to the C runs in results/backtest-v2-repriced/ - this class exists so all ten legs emit
    /// the same per-bar K1/TIM evidence.
    /// </summary>
    public class PaoloV23CProbeETHEURAlgorithm : PaoloV2RepricedETHEURAlgorithm
    {
        public override void OnData(Slice data)
        {
            base.OnData(data);
            if (IsWarmingUp || !data.Bars.ContainsKey(EthEurSymbol))
            {
                return;
            }
            Log($"EQUI|{Time:u}|{Portfolio.TotalPortfolioValue:F4}|{Portfolio.CashBook["ETH"].Amount}|probe|1|0");
        }

        private Symbol EthEurSymbol
        {
            get
            {
                foreach (var s in Securities.Values)
                {
                    if (s.Symbol.Value == "ETHEUR") return s.Symbol;
                }
                return default;
            }
        }
    }
}
