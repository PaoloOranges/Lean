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


//#define LIVE_NO_TRADE
#define PLOT_CHART
//#define LOG_INDICATORS

using Appccelerate.StateMachine;
using Appccelerate.StateMachine.AsyncMachine;
using CircularBuffer;
using MathNet.Numerics;
using QuantConnect.Algorithm.CSharp.PaoloAlgorithm.Utilities;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Type alias for a tuple of two doubles representing a line (intercept, slope)
using Line = System.ValueTuple<double, double>;

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class PaoloHourETHEURAlgorithm : QCAlgorithm
    {
        enum PurchaseState
        {
            Init,
            PrepareToBuy,  
            ReadyToBuy,
            PrepareToSell,
            ReadyToSellGain,
            ReadyToSellLoss
        };

        private enum PurchaseStateEvents
        {
            InitComplete,
            ReadyToBuy,
            Buy,            
            ReadyToSellGain,
            ReadyToSellLoss,
            Sell,

        };


        enum ReadyForBuyState
        {
            None,
            UpTrending,
            LowTrending,
        }

        private ExponentialMovingAverage _slowMA;
        private ExponentialMovingAverage _fastMA;
        private ExponentialMovingAverage _veryFastMA;

        private MovingAverageConvergenceDivergence _macd;
        //private ParabolicStopAndReverse _psar;
        private AverageDirectionalIndex _adx;
        private RelativeStrengthIndex _rsi;
        private BollingerBands _bollingerBands;

        private const decimal _stop_loss_percentage = 0.97m;
        private const decimal _trailing_stop_gain_percentage = 0.01m; // used to maximize gain

        private PurchaseState _tradingPhase = PurchaseState.Init;
        private decimal _boughtPrice = 0;
        private decimal _max_price_after_buy = decimal.MinValue;

        private ReadyForBuyState ready_for_buy_state = ReadyForBuyState.None;

        private const string CryptoName = "ETH";
        private const string CurrencyName = "EUR";
        private const string SymbolName = CryptoName + CurrencyName;

#if DEBUG && PLOT_CHART
        private const string ChartSymbolPrefix = "BARS_";

        private const string OpenSeriesName = "O";
        private const string HighSeriesName = "H";
        private const string LowSeriesName = "L";
        private const string CloseSeriesName = "C";
        private const string VolumeSeriesName = "VOL";
#endif
        // Objsect store to save last buy and sell price for different deploy
        private const string LastBoughtObjectStoreKey = SymbolName + "-last-buy";
        private const string LastSoldObjectStoreKey = SymbolName + "-last-sell";

        private Symbol _symbol = null;

        private decimal _sold_price = 0m;

        private const decimal _amount_to_buy = 0.8m;
        private const decimal _percentagePriceGain = 0.04m;

        private const int WarmUpTime = 440;
        private const double ResolutionInSeconds = 3600.0;
        private const string EmailAddress = "paolo.oranges@gmail.com";

        readonly private CultureInfo _culture_info = CultureInfo.CreateSpecificCulture("en-GB");

        // State counters
        const int CircularBufferLength = 10;
        static readonly private double[] FixedArray = Enumerable.Range(0, CircularBufferLength).Select(x => Convert.ToDouble(x)).ToArray();

        CircularBuffer<IndicatorsFrame> _indicatorsCircularBuffer = new CircularBuffer<IndicatorsFrame>(CircularBufferLength, new IndicatorsFrame[CircularBufferLength]);
               
        Line _fastMALine;
        Line _veryFastMALine;
        Line _slowMALine;
        Line _macdLine;
        Line _macdSignalLine;
        Line _posADXLine;
        Line _negADXLine;
        Line _bbLowLine;
        Line _bbMidLine;
        Line _bbUpLine;

        private int _volumeCounter = 0; //how many consecutive + or - volume
        private decimal _lastVolume = 0;
        private decimal _lastPrice = 0;

        private CrossStateHandler _veryFastMACrossState = new CrossStateHandler();

        private readonly PassiveStateMachine<PurchaseState, PurchaseStateEvents> purchaseStateMachine;

        private Action<Slice, decimal> _processDataAction;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.Coinbase, AccountType.Cash);
            SetTimeZone(NodaTime.DateTimeZone.Utc);

            Resolution resolution = Resolution.Hour;

            SetStartDate(2024, 1, 1); // Set Start Date
            SetEndDate(2024, 8, 1); // Set End Date

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            _symbol = AddCrypto(SymbolName, resolution, Market.Coinbase).Symbol;
            //SetBenchmark(_symbol);

            const int FastPeriod = 26;//55;
            const int SlowPeriod = 55;//110;
            const int VeryFastPeriod = 12;// 15;

            _slowMA = EMA(_symbol, SlowPeriod, resolution);
            _fastMA = EMA(_symbol, FastPeriod, resolution);
            _veryFastMA = EMA(_symbol, VeryFastPeriod, resolution);

            _macd = MACD(_symbol, VeryFastPeriod, SlowPeriod, FastPeriod, MovingAverageType.Exponential, resolution);
            //_psar = PSAR(_symbol, 0.02m, 0.005m, 1m, resolution);

            var adxPeriod = (FastPeriod + VeryFastPeriod) / 2;
            _adx = ADX(_symbol, adxPeriod, resolution);
            _rsi = RSI(_symbol, FastPeriod, MovingAverageType.Exponential);
            _bollingerBands = BB(_symbol, FastPeriod, 1m, MovingAverageType.Simple);

            SetWarmUp(TimeSpan.FromDays(7));

#if DEBUG && PLOT_CHART
            var candleChart = new Chart(ChartSymbolPrefix + SymbolName);
            AddChart(candleChart);
            candleChart.AddSeries(new Series(OpenSeriesName, SeriesType.Line, "€"));
            candleChart.AddSeries(new Series(HighSeriesName, SeriesType.Line, "€"));
            candleChart.AddSeries(new Series(LowSeriesName, SeriesType.Line, "€"));
            candleChart.AddSeries(new Series(CloseSeriesName, SeriesType.Line, "€"));
            candleChart.AddSeries(new Series(VolumeSeriesName, SeriesType.Bar, ""));
            //candleChart.AddSeries(new Series("Time", SeriesType.Line, "date"));
            IndicatorBase[] indicatorsToPlot = { /*_slowMA, _fastMA, _veryFastMA ,*/ _bollingerBands.MiddleBand, _bollingerBands.UpperBand, _bollingerBands.LowerBand};
            PlotIndicator("Indicators", indicatorsToPlot);
            //PlotIndicator("Indicators", _psar);
            //PlotIndicator("Indicators", _maximum_price);
            IndicatorBase[] oscillatorsToPlot = { /*_adx,*//* _adx.PositiveDirectionalIndex, _adx.NegativeDirectionalIndex,*/ _macd, _macd.Signal, _rsi};
            PlotIndicator("Oscillators", oscillatorsToPlot);
#endif

            var builder = new StateMachineDefinitionBuilder<PurchaseState, PurchaseStateEvents>();

            //builder.In(PurchaseState.Init)
            //       .On(PurchaseStateEvents.InitComplete)
            //       .Goto(PurchaseState.PrepareToBuy)
            //       .Execute(PrepareToBuy);

            //builder.In(PurchaseState.PrepareToBuy)
            //       .On(PurchaseStateEvents.ReadyToBuy)
            //       .Goto(PurchaseState.ReadyToBuy)
            //       .Execute(ReadyToBuy);

        }

        public override void PostInitialize()
        {

            var cryptoCashBook = Portfolio.CashBook[CryptoName];
            if (cryptoCashBook.Amount > 0)
            {
                Log(CryptoName + " amount in Portfolio: " + cryptoCashBook.Amount + " - Initialized to Bought");
                _tradingPhase = PurchaseState.PrepareToSell;
                if (HasBoughtPriceFromPreviousSession)
                {
                    string bought_price = ObjectStore.Read(LastBoughtObjectStoreKey);
                    Log("Previous Purchase found: " + bought_price);
                    _boughtPrice = Convert.ToDecimal(bought_price, _culture_info);
                }
            }
            else
            {
                Log(CurrencyName + " amount in Portfolio: " + Portfolio.CashBook[CurrencyName].Amount + " - Initialized to Sold");
                _tradingPhase = PurchaseState.PrepareToBuy;
                ready_for_buy_state = ReadyForBuyState.None;
                if (HasSoldPriceFromPreviousSession)
                {
                    string sold_price = ObjectStore.Read(LastSoldObjectStoreKey);
                    Log("Previous Sell found: " + sold_price);
                }
            }

            base.PostInitialize();

        }

        private bool HasBoughtPriceFromPreviousSession
        {
            get
            {
                return ObjectStore.ContainsKey(LastBoughtObjectStoreKey) && LiveMode;
            }
        }

        private bool HasSoldPriceFromPreviousSession
        {
            get
            {
                return ObjectStore.ContainsKey(LastSoldObjectStoreKey) && LiveMode;
            }
        }

        private DateTime UtcTimeLast;
        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public override void OnData(Slice data)
        {
            UpdateInternalVariables(data);

            if (IsWarmingUp)
            {
                if (_tradingPhase == PurchaseState.PrepareToSell && !HasBoughtPriceFromPreviousSession)
                {
                    _boughtPrice = Math.Max(data[SymbolName].Value, _boughtPrice);
                }
                return;
            }

            OnProcessData(data);

            DateTime UtcTimeNow = UtcTime;

            if (Math.Floor((UtcTimeNow - UtcTimeLast).TotalSeconds) > ResolutionInSeconds)
            {
                Log("WrongTime! Last: " + UtcTimeLast.ToString(_culture_info) + " Now: " + UtcTimeNow.ToString(_culture_info));
            }
            UtcTimeLast = UtcTimeNow;

#if DEBUG && PLOT_CHART
            //Plot(SymbolName, "Price", data[SymbolName].Value);
            Plot(ChartSymbolPrefix + SymbolName, OpenSeriesName, data[SymbolName].Open);
            Plot(ChartSymbolPrefix + SymbolName, HighSeriesName, data[SymbolName].High);
            Plot(ChartSymbolPrefix + SymbolName, LowSeriesName, data[SymbolName].Low);
            Plot(ChartSymbolPrefix + SymbolName, CloseSeriesName, data[SymbolName].Close);
            Plot(ChartSymbolPrefix + SymbolName, VolumeSeriesName, data[SymbolName].Volume);
            //Plot(SymbolName, "Time", (decimal)data[SymbolName].Time.ToBinary());
#endif
        }

        private void UpdateInternalVariables(Slice data)
        {
            IndicatorsFrame frame = new()
            {
                veryFastMA = _veryFastMA,
                fastMA = _fastMA,
                slowMA = _slowMA,
                MACD = _macd,
                MACDSignal = _macd.Signal,
                MACDHistogram = _macd.Histogram,
                ADXPlus = _adx.PositiveDirectionalIndex,
                ADXMinus = _adx.NegativeDirectionalIndex,
                BBLow = _bollingerBands.LowerBand,
                BBMid = _bollingerBands.MiddleBand,
                BBUp = _bollingerBands.UpperBand,
            };

            _indicatorsCircularBuffer.PushBack(frame);

            var frameArray = _indicatorsCircularBuffer.ToArray();

            _fastMALine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.fastMA)));
            _veryFastMALine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.veryFastMA)));
            _slowMALine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.slowMA)));
            _macdLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.MACD)));
            _macdSignalLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.MACDSignal)));
            _posADXLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.ADXPlus)));
            _negADXLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.ADXMinus)));
            _bbLowLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.BBLow)));
            _bbMidLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.BBMid)));
            _bbUpLine = Fit.Line(FixedArray, ConvertToDoubleArray(frameArray, selector: f => Decimal.ToDouble(f.BBUp)));

            decimal current_price = data[SymbolName].Value;
            switch (_tradingPhase)
            {
                case PurchaseState.PrepareToSell:
                case PurchaseState.ReadyToSellGain:
                case PurchaseState.ReadyToSellLoss:
                    {
                        _max_price_after_buy = Math.Max(_max_price_after_buy, current_price);
                    }
                    break;                
                case PurchaseState.PrepareToBuy:
                    {

                    }
                    break;               
                
            }


            decimal volume = data[SymbolName].Volume;
            _lastVolume = volume;

            if (current_price > _lastPrice) 
            {
                if(_volumeCounter > 0)
                {
                    _volumeCounter++;
                }
                else
                { 
                    _volumeCounter = 1; 
                }
            }
            else
            {
                if (_volumeCounter < 0)
                {
                    _volumeCounter--;
                }
                else
                {
                    _volumeCounter = -1;
                }
            }

            _lastPrice = current_price;

            _veryFastMACrossState.OnData(_veryFastMA, _slowMA);
        }

        private void OnProcessData(Slice data)
        {
#if LOG_INDICATORS
            Log("INDICATORS. VeryFastEMA: " + _very_fast_ema + " - FastEMA: " + _fast_ema + " - SlowEMA: " + _slow_ema + " - MACD: " + _macd.Histogram.Current.Value);
#endif

            decimal currentPrice = data[SymbolName].Value; // Value == Close
            //_processDataAction?.Invoke(data, currentPrice);

            switch (_tradingPhase)
            {
                case PurchaseState.Init:
                    break;
                case PurchaseState.ReadyToBuy:
                    ReadyToBuyProcessAction(data, currentPrice);
                    break;
                case PurchaseState.PrepareToSell:
                    PrepareToSellProcessAction(data, currentPrice);
                    break;
                case PurchaseState.ReadyToSellGain:
                    ReadyToSellGainProcessAction(data, currentPrice);
                    break;
                case PurchaseState.ReadyToSellLoss:
                    ReadyToSellLossProcessAction(data, currentPrice);
                    break;
                case PurchaseState.PrepareToBuy:
                    PrepareToBuyProcessAction(data, currentPrice);
                    break;
                default:
                    Error("Invalid Purchase state");
                    break;
            }
        }

        

        private void PrepareToBuy()
        { 
            _processDataAction = PrepareToBuyProcessAction;
        }
        private void ReadyToBuy()
        {
            _processDataAction = ReadyToBuyProcessAction;
        }

        private void PrepareToSellProcessAction(Slice data, decimal currentPrice)
        {
            bool isTargetPriceAchieved = currentPrice > (1.0m + _percentagePriceGain) * _boughtPrice;
            bool isRSIOverBought = _rsi > 65;
            bool isPriceOverUpperBand = currentPrice > _bollingerBands.UpperBand;

            bool isRSILowBoutght = _rsi < 55;

            var bbLowSlope = GetSlope(_bbLowLine);
            var bbMidSlope = GetSlope(_bbMidLine);
            var bbUpSlope = GetSlope(_bbUpLine);

            if(bbMidSlope <= bbLowSlope && bbUpSlope <= bbMidSlope)
            {
                //_tradingPhase = PurchaseState.ReadyToSellLoss;
            }
            else if (isTargetPriceAchieved && isRSIOverBought)
            {
                _tradingPhase = PurchaseState.ReadyToSellGain;
            }
			
			if (isTargetPriceAchieved && isRSIOverBought && isPriceOverUpperBand)
            {
                _tradingPhase = PurchaseState.ReadyToSellGain;
            }
            
                // TODO: Collect statistics on price in between BB and decide based on that
                // check if price goes below the lower band and some stop loss percentage
                // Find a way to wait and not trigger stop loss directly after buy
                //if (isTargetPriceAchieved && isRSIOverBought && (highBand > lowBand))
                //{
                //    _tradingPhase = PurchaseState.ReadyToSellGain;
                //}
                //else if (isRSILowBoutght && (highBand <= lowBand))
                //{
                //    _tradingPhase = PurchaseState.ReadyToSellLoss;
                //}

                // check for limiting loss
            }

        private void ReadyToSellGainProcessAction(Slice data, decimal currentPrice)
        {
            if (IsOkToSell(data, currentPrice))
            {
#if !(LIVE_NO_TRADE)
                Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
#else
                    _bought = -1;
                    Log("Sell order: at price " + current_price);
#endif

#if LOG_INDICATORS
                    Log("SELL ORDER. VeryFastEMA: " + _very_fast_ema + " - FastEMA: " + _fast_ema + " - SlowEMA: " + _slow_ema + " - MACD: " + _macd.Histogram.Current.Value);
#endif
            }
        }

        private void ReadyToSellLossProcessAction(Slice data, decimal currentPrice)
        {
            var stopLossPrice = _boughtPrice * _stop_loss_percentage;

            Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
        }

        private bool IsOkToSell(Slice data, decimal currentPrice)
        {
            var bbHighSlope = GetSlope(_bbUpLine);
            var bbMidSlope = GetSlope(_bbMidLine);
            var bbLowSlope = GetSlope(_bbLowLine);
            var macdSlope = GetSlope(_macdLine);
            var macdSignalSlope = GetSlope(_macdSignalLine);

            var trailingStopPrice = _max_price_after_buy * (1.0m - _trailing_stop_gain_percentage);

            

            if (currentPrice < trailingStopPrice)
            {
                if (currentPrice < _bollingerBands.MiddleBand)
                {
                    return true;
                }
                if(macdSlope <= ConvertDegreeToRad(30) && bbMidSlope < bbLowSlope)
                {
                    return true;
                }
            }

            return false;
        }

        private void PrepareToBuyProcessAction(Slice data, decimal currentPrice)
        {
            bool transitionToNextState = false;

            if (currentPrice < _bollingerBands.LowerBand)
            {
                transitionToNextState = true;
            }
            else if (currentPrice < _bollingerBands.MiddleBand && _macd < _macd.Signal)
            {
                transitionToNextState = true; // _volumeCounter > 2; // _rsi >= 60
            }

            //if(_veryFastMACrossState.CrossState == CrossStateEnum.Down)
            //{
            //    if (_veryFastMA < _slowMA)
            //    {
            //        bool isPriceAndMAOk = currentPrice > _veryFastMA;
            //        bool isSlopeOk = veryFastSlope > slowSlope && veryFastSlope > Math.PI / 6;
            //        bool isMacdSlopeOk = /*macdSlope > 0.1*/ _macd.Histogram > 0;
            //        bool isAdxOk = _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex;

            //        transitionToNextState = isPriceAndMAOk && isSlopeOk && isAdxOk;
            //    }
            //}
            //else if(_veryFastMACrossState.CrossState == CrossStateEnum.Up)
            //{
            //    bool isPriceAndMAOk = currentPrice > _fastMA;
            //    bool isAdxOk = (_adx.PositiveDirectionalIndex - _adx.NegativeDirectionalIndex) > 2;

            //    bool isMACDSlopeOk = macdSlope > 0.2;
            //    bool isMACDOk = _macd.Histogram > 0;

            //    transitionToNextState = isPriceAndMAOk && isAdxOk && isMACDOk;
            //}
            //else
            //{
            //    bool isMAOk = _veryFastMA > _fastMA && _fastMA > _slowMA;
            //    bool isPriceOk = currentPrice > _veryFastMA;
            //    bool isAdxOk = (_adx.PositiveDirectionalIndex - _adx.NegativeDirectionalIndex) > 5;
            //    bool isVolumeOk = _volumeCounter > 2;

            //    transitionToNextState = isMAOk && isPriceOk && isAdxOk && isVolumeOk;
            //}

            if (transitionToNextState)
            {
                _tradingPhase = PurchaseState.ReadyToBuy;
            }
        }

        private void ReadyToBuyProcessAction(Slice data, decimal currentPrice)
        {
            if (IsOkToBuy(data, currentPrice))
            {
                const decimal round_multiplier = 1000m;
                decimal amount_to_buy = Portfolio.CashBook[CurrencyName].Amount * _amount_to_buy;
                decimal quantity = Math.Truncate(round_multiplier * amount_to_buy / currentPrice) / round_multiplier;
#if !(LIVE_NO_TRADE)
                var order = Buy(_symbol, quantity);
#else
                    _bought = 1;
                    Log("Buy order: " + quantity + " at price " + current_price);
#endif

#if LOG_INDICATORS
                    Log("BUY ORDER. VeryFastEMA: " + _very_fast_ema + " - FastEMA: " + _fast_ema + " - SlowEMA: " + _slow_ema + " - MACD: " + _macd.Histogram.Current.Value);
#endif
            }
        }

        private bool IsOkToBuy(Slice data, decimal currentPrice)
        {
            bool is_moving_averages_ok = /*_very_fast_wma > _slow_hullma &&*/ /*_very_fast_wma > _fast_lsma &&*/ currentPrice > _veryFastMA;

            var signalDeltaPercent = (_macd - _macd.Signal) / _macd.Fast;
            var tolerance = 0.0025m;

            var veryFastSlope = GetSlope(_veryFastMALine);
            var fastSlope = GetSlope(_fastMALine);
            var slowSlope = GetSlope(_slowMALine);

            var bbUpSlope = GetSlope(_bbUpLine);
            var bbMidSlope = GetSlope(_bbMidLine);
            var bbLowSlope = GetSlope(_bbLowLine);
            var macdSlope = GetSlope(_macdLine);
            var macdSignalSlope = GetSlope(_macdSignalLine);

            if(macdSlope >= 0 && macdSignalSlope >= 0)
            {
                if (currentPrice > _bollingerBands.LowerBand)
                {
                    return true;
                }
                else if (currentPrice > _bollingerBands.MiddleBand && _volumeCounter > 2)
                {
                    return true;
                }
            }            

            return false;
        }

        private bool IsStopLoss(Slice data)
        {
            decimal current_price = data[SymbolName].Value;

            return current_price < _stop_loss_percentage * _boughtPrice;
        }

        private decimal GetADXDifference()
        {
            return _adx.PositiveDirectionalIndex - _adx.NegativeDirectionalIndex;
        }               

        private void ResetCrossStates()
        {
            _veryFastMACrossState.Reset();
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null)
            {
                return;
            }

            Debug(Time + " " + orderEvent);

            if (orderEvent.Direction == OrderDirection.Buy && orderEvent.Status == OrderStatus.Filled)
            {
                _tradingPhase = PurchaseState.PrepareToSell;
                _boughtPrice = orderEvent.FillPrice;
                _max_price_after_buy = _boughtPrice;
                ResetCrossStates();
                ObjectStore.Save(LastBoughtObjectStoreKey, _boughtPrice.ToString(_culture_info));
            }

            if (orderEvent.Direction == OrderDirection.Sell && orderEvent.Status == OrderStatus.Filled)
            {
                _tradingPhase = PurchaseState.PrepareToBuy;
                _sold_price = orderEvent.FillPrice;
                _max_price_after_buy = decimal.MinValue;
                ResetCrossStates();
                ObjectStore.Save(LastSoldObjectStoreKey, _sold_price.ToString(_culture_info));
            }

            if (orderEvent.Status == OrderStatus.Invalid)
            {
                _tradingPhase = orderEvent.Direction == OrderDirection.Buy ? PurchaseState.PrepareToBuy : PurchaseState.PrepareToSell;
            }

            if (orderEvent.Status == OrderStatus.Submitted)
            {
                //_purchase_status = 0;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"{Time} - TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            Log($"{Time} - CashBook: {Portfolio.CashBook}");
        }

        private double GetSlope((double, double) line)
        {
            return line.Item2;
        }

        private double[] ConvertToDoubleArray<T>(T[] values, Func<T, double> selector)
        {
            return values.Select(selector).ToArray();

        }

        private double ConvertDegreeToRad(double deg)
        {
            return (Math.PI / 180.0) * deg;
        }

        internal class MinMaxMACD
        {
            private int _length = 0;
            private Queue<decimal> _values = new Queue<decimal>();

            public decimal Max
            {
                get
                {
                    return _values.Max();
                }
            }

            public decimal Min
            {
                get
                {
                    return _values.Min();
                }
            }

            public MinMaxMACD(int length)
            {
                _length = length;
            }

            public void OnData(decimal mcd_data)
            {
                _values.Enqueue(mcd_data);
                while (_values.Count > _length)
                {
                    _values.Dequeue();
                }
            }
        }
       
    }
}
