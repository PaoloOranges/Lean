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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

using QuantConnect.Data;
using QuantConnect.Brokerages;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Data.Market;
using QuantConnect.Util;
using MathNet.Numerics;
using QuantConnect.Algorithm.CSharp.PaoloAlgorithm.Utilities;
using Accord.MachineLearning.Performance;


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
            ReadyToBuy,
            Bought,
            Sold,  
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

        private const decimal _stop_loss_percentage = 0.95m;

        private PurchaseState _purchase_status = PurchaseState.Init;
        private decimal _bought_price = 0;
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
        private const decimal _percentage_price_gain = 0.04m;

        private const int WarmUpTime = 440;
        private const double ResolutionInSeconds = 3600.0;
        private const string EmailAddress = "paolo.oranges@gmail.com";

        readonly private CultureInfo _culture_info = CultureInfo.CreateSpecificCulture("en-GB");

        // State counters
        const int CircularBufferLength = 10;
        readonly private double[] FixedArray = Enumerable.Range(0, CircularBufferLength).Select(x => Convert.ToDouble(x)).ToArray();

        FixedCircularBuffer<decimal> _fastMACircularBuffer = new FixedCircularBuffer<decimal>(CircularBufferLength);
        FixedCircularBuffer<decimal> _veryFastMACircularBuffer = new FixedCircularBuffer<decimal>(CircularBufferLength);
        FixedCircularBuffer<decimal> _slowMACircularBuffer = new FixedCircularBuffer<decimal>(CircularBufferLength);
        FixedCircularBuffer<decimal> _macdCircularBuffer = new FixedCircularBuffer<decimal>(CircularBufferLength);
        FixedCircularBuffer<decimal> _posADXCircularBuffer = new FixedCircularBuffer<decimal>(CircularBufferLength);
        FixedCircularBuffer<decimal> _negADXCircularBuffer = new FixedCircularBuffer<decimal>(CircularBufferLength);

        (double, double) _fastMALine;
        (double, double) _veryFastMALine;
        (double, double) _slowMALine;
        (double, double) _macdLine;
        (double, double) _posADXLine;
        (double, double) _negADXLine;

        private int _volumeCounter = 0; //how many consecutive + or - volume
        private decimal _lastVolume = 0;
        private decimal _lastPrice = 0;

        private CrossStateHandler _veryFastMACrossState = new CrossStateHandler();

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.Coinbase, AccountType.Cash);
            SetTimeZone(NodaTime.DateTimeZone.Utc);

            Resolution resolution = Resolution.Hour;

            SetStartDate(2023, 1, 1); // Set Start Date
            SetEndDate(2023, 04, 1); // Set End Date

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            _symbol = AddCrypto(SymbolName, resolution, Market.Coinbase).Symbol;
            //SetBenchmark(_symbol);

            const int FastPeriod = 55;
            const int SlowPeriod = 110;
            const int VeryFastPeriod = 15;
            const int Signal = 8;

            _slowMA = EMA(_symbol, SlowPeriod, resolution);
            _fastMA = EMA(_symbol, FastPeriod, resolution);
            _veryFastMA = EMA(_symbol, VeryFastPeriod, resolution);

            _macd = MACD(_symbol, FastPeriod, SlowPeriod, VeryFastPeriod, MovingAverageType.Exponential, resolution);
            //_psar = PSAR(_symbol, 0.02m, 0.005m, 1m, resolution);

            var adxPeriod = (FastPeriod + VeryFastPeriod) / 2;
            _adx = ADX(_symbol, adxPeriod, resolution);
            //_rsi = RSI(_symbol, slowPeriod, MovingAverageType.Exponential);

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
            IndicatorBase[] indicatorsToPlot = { _slowMA, _fastMA, _veryFastMA };
            PlotIndicator("Indicators", indicatorsToPlot);
            //PlotIndicator("Indicators", _psar);
            //PlotIndicator("Indicators", _maximum_price);
            IndicatorBase[] oscillatorsToPlot = { /*_adx,*/ _adx.PositiveDirectionalIndex, _adx.NegativeDirectionalIndex, _macd.Histogram};
            PlotIndicator("Oscillators", oscillatorsToPlot);
#endif

        }

        public override void PostInitialize()
        {

            var cryptoCashBook = Portfolio.CashBook[CryptoName];
            if (cryptoCashBook.Amount > 0)
            {
                Log(CryptoName + " amount in Portfolio: " + cryptoCashBook.Amount + " - Initialized to Bought");
                _purchase_status = PurchaseState.Bought;
                if (HasBoughtPriceFromPreviousSession)
                {
                    string bought_price = ObjectStore.Read(LastBoughtObjectStoreKey);
                    Log("Previous Purchase found: " + bought_price);
                    _bought_price = Convert.ToDecimal(bought_price, _culture_info);
                }
            }
            else
            {
                Log(CurrencyName + " amount in Portfolio: " + Portfolio.CashBook[CurrencyName].Amount + " - Initialized to Sold");
                _purchase_status = PurchaseState.Sold;
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
                if (_purchase_status == PurchaseState.Bought && !HasBoughtPriceFromPreviousSession)
                {
                    _bought_price = Math.Max(data[SymbolName].Value, _bought_price);
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
            _veryFastMACircularBuffer.Push(_veryFastMA);
            _fastMACircularBuffer.Push(_fastMA);
            _slowMACircularBuffer.Push(_slowMA);
            _macdCircularBuffer.Push(_macd);
            _posADXCircularBuffer.Push(_adx.PositiveDirectionalIndex);
            _negADXCircularBuffer.Push(_adx.NegativeDirectionalIndex);

            _fastMALine = Fit.Line(FixedArray, ConvertToDoubleArray(_fastMACircularBuffer));
            _veryFastMALine = Fit.Line(FixedArray, ConvertToDoubleArray(_veryFastMACircularBuffer));
            _slowMALine = Fit.Line(FixedArray, ConvertToDoubleArray(_slowMACircularBuffer));
            _macdLine = Fit.Line(FixedArray, ConvertToDoubleArray(_macdCircularBuffer));
            _posADXLine = Fit.Line(FixedArray, ConvertToDoubleArray(_posADXCircularBuffer));
            _negADXLine = Fit.Line(FixedArray, ConvertToDoubleArray(_negADXCircularBuffer));

            decimal current_price = data[SymbolName].Value;
            switch (_purchase_status)
            {
                case PurchaseState.Bought:
                    {
                        _max_price_after_buy = Math.Max(_max_price_after_buy, current_price);
                    }
                    break;                
                case PurchaseState.Sold:
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
            decimal current_price = data[SymbolName].Value; // Value == Close
            switch (_purchase_status)
            {
                case PurchaseState.Init:
                    break;
                case PurchaseState.ReadyToBuy:
                    OnReadyToBuy(data, current_price);
                    break;
                case PurchaseState.Bought:
                    OnBought(data, current_price);
                    break;
                case PurchaseState.Sold:
                    OnPrepareToBuy(data, current_price);
                    break;
                default:
                    Error("Invalid Purchase state");
                    break;
            }
        }

        private void OnBought(Slice data, decimal current_price)
        {
            if (IsOkToSell(data, current_price))
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

        private void OnReadyToBuy(Slice data, decimal current_price)
        {
            if (IsOkToBuy(data, current_price))
            {
                const decimal round_multiplier = 1000m;
                decimal amount_to_buy = Portfolio.CashBook[CurrencyName].Amount * _amount_to_buy;
                decimal quantity = Math.Truncate(round_multiplier * amount_to_buy / current_price) / round_multiplier;
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

        private void OnPrepareToBuy(Slice data, decimal current_price)
        {
            bool transitionToNextState = false;

            var veryFastSlope = GetSlope(_veryFastMALine);
            var fastSlope = GetSlope(_fastMALine);
            var slowSlope = GetSlope(_slowMALine);
            var macdSlope = GetSlope(_macdLine);
                        
            if(_veryFastMACrossState.CrossState == CrossStateEnum.Down)
            {
                if (_veryFastMA < _slowMA)
                {
                    bool isPriceAndMAOk = current_price > _veryFastMA;
                    bool isSlopeOk = veryFastSlope > slowSlope && veryFastSlope > Math.PI / 6;
                    bool isMacdSlopeOk = macdSlope > 0.1;
                    bool isAdxOk = _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex;

                    transitionToNextState = isPriceAndMAOk && isMacdSlopeOk && isSlopeOk;
                }
            }
            else if(_veryFastMACrossState.CrossState == CrossStateEnum.Up)
            {
                bool isMAOk = _veryFastMA > _fastMA && _fastMA > _slowMA;
                bool isAdxOk = _adx.PositiveDirectionalIndex - _adx.NegativeDirectionalIndex > 5;
                bool isMACDOk = _macd > 0;
                bool isMACDSlopeOk = macdSlope > 0;

                transitionToNextState = isMAOk && isAdxOk && isMACDOk && isMACDSlopeOk;
            }

            if(transitionToNextState)
            {
                _purchase_status = PurchaseState.ReadyToBuy;
            }
        }

        private bool IsOkToBuy(Slice data, decimal current_price)
        {
            bool is_moving_averages_ok = /*_very_fast_wma > _slow_hullma &&*/ /*_very_fast_wma > _fast_lsma &&*/ current_price > _veryFastMA;

            var signalDeltaPercent = (_macd - _macd.Signal) / _macd.Fast;
            var tolerance = 0.0025m;

            var veryFastSlope = GetSlope(_veryFastMALine);
            var fastSlope = GetSlope(_fastMALine);
            var slowSlope = GetSlope(_slowMALine);
            var macdSlope = GetSlope(_macdLine);

            //if (_veryFastMA < _slowMA)
            //{
            //    bool isVolumeOk = _volumeCounter > 2 && _lastVolume > 0;
            //    bool isPriceAndMAOk = current_price > _fastMA;
            //    bool isMacdSlopeOk = macdSlope > 0.1;
            //    bool isSlopeOk = veryFastSlope > slowSlope;
            //    bool isAdxOk = _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex;

            //    return isPriceAndMAOk && isVolumeOk && isMacdSlopeOk && isSlopeOk;
            //}
            //else if(_veryFastMA < _fastMA)
            //{
            //    bool isSlopeOk = veryFastSlope > fastSlope /*&& veryFastSlope > 0.5*/;

            //    bool isMacdSlopeOk = macdSlope > 0.1;
            //    bool isVolumeOk = _volumeCounter > 2 && _lastVolume > 0;

            //    bool isPriceAndMAOk = current_price > _veryFastMA;

            //    return isPriceAndMAOk && isVolumeOk && isMacdSlopeOk && isSlopeOk;

            //}             
            //else
            //{
            //    bool isSlopeOk = veryFastSlope > fastSlope;
            //    bool isAdxOk = _adx.PositiveDirectionalIndex - _adx.NegativeDirectionalIndex > 9;
            //    bool isPriceOk = current_price > _veryFastMA;

            //    return isAdxOk && isPriceOk && isSlopeOk;
            //}

            //if (_veryFastMACrossState.CrossState == CrossStateEnum.None)
            //{
            //    bool is_adx_ok = GetADXDifference() > 10; // try making combination with distance of positive and negative compared to other indicators

            //    return is_moving_averages_ok && is_adx_ok && signalDeltaPercent > tolerance /* && isVolumeOk && is_macd_ok*/;
            //}
            //else
            //{
            //    bool slowSlopeOk = GetSlope(_veryFastMALine) > 0.0 && GetSlope(_veryFastMALine) > GetSlope(_fastMALine) && GetSlope(_slowMALine) >= 0.0;
            //    bool isMACrossingOk = _veryFastMACrossState.CrossState == CrossStateEnum.Up;
            //    bool isPriceOverFast = current_price > _veryFastMA;
            //    bool isAdxOk = GetADXDifference() > 0;

            //    return is_moving_averages_ok && isPriceOverFast && isAdxOk && slowSlopeOk;
            //}

            //bool is_macd_ok = _macd.Histogram.Current.Value > 0;
            //bool is_adx_ok = GetADXDifference() > 10; // try making combination with distance of positive and negative compared to other indicators

            //bool is_moving_averages_ok = /*_very_fast_wma > _slow_hullma &&*/ /*_very_fast_wma > _fast_lsma &&*/ current_price > _slowMA;

            //bool isVolumeOk = _volumeCounter > 1;

            //return is_moving_averages_ok && is_adx_ok /* && isVolumeOk && is_macd_ok*/;
            return true;
        }

        private bool IsOkToSell(Slice data, decimal current_price)
        {
            bool is_macd_ok = _macd.Histogram.Current.Value < 0;
            const decimal cross_ma_linear_interp_value = 0.3m;
            var cross_ma_value = _veryFastMA * cross_ma_linear_interp_value + _fastMA * (1m - cross_ma_linear_interp_value);

            bool is_moving_averages_ok = current_price < cross_ma_value && _veryFastMA < _fastMA;

            bool is_adx_ok = GetADXDifference() < 10; // compute line and see slope

            bool is_target_price_achieved = current_price > (1.0m + _percentage_price_gain) * _bought_price;

            if (is_target_price_achieved)
            {
                string body = "Price is ok, MACD is " + is_macd_ok + " with value " + _macd.Histogram.Current.Value + "\nVeryFastMA is " + is_moving_averages_ok + "\nAsset price is " + current_price + " and buy price is " + _bought_price;
                //Notify.Email(EmailAddress, "Price Ok for SELL", body);
                //Log(body);
            }

            bool slowSlopeOk = GetSlope(_slowMALine) <= 0.0 && GetSlope(_veryFastMALine) < 1.5 * GetSlope(_fastMALine);

            bool is_stop_limit = slowSlopeOk && current_price < 0.94m * _max_price_after_buy; ; // _veryFastMA < _fastMA && _fastMA < _slowMA && _adx.PositiveDirectionalIndex < _adx.NegativeDirectionalIndex && _macd < 0 ;  


            bool is_gain_ok = is_moving_averages_ok && is_target_price_achieved;
            //is_gain_ok = is_target_price_achieved && is_stop_limit;

            return (is_gain_ok && slowSlopeOk) || is_stop_limit  /*|| IsStopLoss(data)*/;

        }

        private bool IsStopLoss(Slice data)
        {
            decimal current_price = data[SymbolName].Value;

            return current_price < _stop_loss_percentage * _bought_price;
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
                _purchase_status = PurchaseState.Bought;
                _bought_price = orderEvent.FillPrice;
                _max_price_after_buy = _bought_price;
                ResetCrossStates();
                ObjectStore.Save(LastBoughtObjectStoreKey, _bought_price.ToString(_culture_info));
            }

            if (orderEvent.Direction == OrderDirection.Sell && orderEvent.Status == OrderStatus.Filled)
            {
                _purchase_status = PurchaseState.Sold;
                _sold_price = orderEvent.FillPrice;
                _max_price_after_buy = decimal.MinValue;
                ResetCrossStates();
                ObjectStore.Save(LastSoldObjectStoreKey, _sold_price.ToString(_culture_info));
            }

            if (orderEvent.Status == OrderStatus.Invalid)
            {
                _purchase_status = orderEvent.Direction == OrderDirection.Buy ? PurchaseState.Sold : PurchaseState.Bought;
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

        private double[] ConvertToDoubleArray<T>(FixedCircularBuffer<T> fixedCircularBuffer)
        {
            return fixedCircularBuffer.ToArray().Select(x => Convert.ToDouble(x)).ToArray();
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
