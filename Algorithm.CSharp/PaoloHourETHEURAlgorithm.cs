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


namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class PaoloHourETHEURAlgorithm : QCAlgorithm
    {
        enum PurchaseStatus
        {
            Init,
            Bought,
            Sold,
            ReadyToBuy,
            ReadyToSell
        };

        private HullMovingAverage _slow_hullma;
        private LeastSquaresMovingAverage _fast_lsma;
        private LinearWeightedMovingAverage _very_fast_wma;

        private MovingAverageConvergenceDivergence _macd;
        //private ParabolicStopAndReverse _psar;
        private AverageDirectionalIndex _adx;

        private Maximum _maximum_price;
        private const decimal _stop_loss_percentage = 0.015m;

        private PurchaseStatus _purchase_status = PurchaseStatus.Init;
        private decimal _bought_price = 0;
        private decimal _max_price_after_buy = Decimal.MinValue;

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
        private const double resolutionInSeconds = 3600.0;
        private const string EmailAddress = "paolo.oranges@gmail.com";

        private CultureInfo _culture_info = CultureInfo.CreateSpecificCulture("en-GB");


        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.GDAX, AccountType.Cash);
            SetTimeZone(NodaTime.DateTimeZone.Utc);

            Resolution resolution = Resolution.Hour;

            SetStartDate(2023, 1, 1); // Set Start Date
            SetEndDate(2023, 03, 30); // Set End Date

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            //SetCash(CryptoName, 0.08m);

            _symbol = AddCrypto(SymbolName, resolution, Market.GDAX).Symbol;
            SetBenchmark(_symbol);

            const int fastValue = 55;
            const int slowValue = 110;
            const int veryFastValue = 15;
            const int signal = 8;

            _slow_hullma = HMA(_symbol, slowValue, resolution);
            _fast_lsma = LSMA(_symbol, fastValue, resolution);
            _very_fast_wma = LWMA(_symbol, veryFastValue, resolution);

            _macd = MACD(_symbol, fastValue, slowValue, veryFastValue, MovingAverageType.Exponential, resolution);
            //_psar = PSAR(_symbol, 0.02m, 0.005m, 1m, resolution);

            _adx = ADX(_symbol, fastValue, resolution);

            _maximum_price = MAX(_symbol, fastValue, resolution, x => ((TradeBar)x).Close);

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
            IndicatorBase[] indicatorsToPlot = { _slow_hullma, _fast_lsma, _very_fast_wma };
            PlotIndicator("Indicators", indicatorsToPlot);
            //PlotIndicator("Indicators", _psar);
            //PlotIndicator("Indicators", _maximum_price);
            IndicatorBase[] oscillatorsToPlot = { _adx, _adx.PositiveDirectionalIndex, _adx.NegativeDirectionalIndex };
            PlotIndicator("Oscillators", oscillatorsToPlot);
#endif

        }

        public override void PostInitialize()
        {
            var cryptoCashBook = Portfolio.CashBook[CryptoName];
            if (cryptoCashBook.Amount > 0)
            {
                Log(CryptoName + " amount in Portfolio: " + cryptoCashBook.Amount + " - Initialized to Bought");
                _purchase_status = PurchaseStatus.Bought;
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
                _purchase_status = PurchaseStatus.Sold;
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
                if(_purchase_status == PurchaseStatus.Bought && !HasBoughtPriceFromPreviousSession)
                {
                    _bought_price = _maximum_price;
                }
                return;
            }
            
            OnProcessData(data);
            
            DateTime UtcTimeNow = UtcTime;

            if (Math.Floor((UtcTimeNow - UtcTimeLast).TotalSeconds) > resolutionInSeconds)
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
            if (_purchase_status == PurchaseStatus.Bought)
            {
                decimal current_price = data[SymbolName].Value;
                _max_price_after_buy = Math.Max(_max_price_after_buy, current_price);
            }
        }

        private void OnProcessData(Slice data)
        {
#if LOG_INDICATORS
            Log("INDICATORS. VeryFastEMA: " + _very_fast_ema + " - FastEMA: " + _fast_ema + " - SlowEMA: " + _slow_ema + " - MACD: " + _macd.Histogram.Current.Value);
#endif
            decimal current_price = Securities[SymbolName].Price;

            if (_purchase_status == PurchaseStatus.Sold)
            {
                if (IsOkToBuy(data))
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
            else if (_purchase_status == PurchaseStatus.Bought)
            {
                if (IsOkToSell(data))
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
        }

        private bool IsOkToBuy(Slice data)
        {
            decimal current_price = data[SymbolName].Value;

            bool is_macd_ok = _macd.Histogram.Current.Value > 0;
            bool is_adx_ok = _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex; // try making combination with distance of positive and negative compared to other indicators

            bool is_moving_averages_ok = _very_fast_wma > _fast_lsma;
            //bool is_ao_ok = true; // _ao.AroonUp > 80;

            return is_moving_averages_ok && is_adx_ok; /*&& is_macd_ok && is_ao_ok*/;
        }

        private bool IsOkToSell(Slice data)
        {

            decimal current_price = data[SymbolName].Value;

            bool is_macd_ok = _macd.Histogram.Current.Value < 0;
            bool is_moving_averages_ok = _very_fast_wma < _fast_lsma ;//&& _very_fast_wma < _slow_hullma;
            //bool is_ao_ok = _ao.AroonUp < _ao.AroonDown;

            bool is_target_price_achieved = current_price > (1.0m + _percentage_price_gain) * _bought_price;

            if (is_target_price_achieved)
            {
                string body = "Price is ok, MACD is " + is_macd_ok + " with value " + _macd.Histogram.Current.Value + "\nVeryFastMA is " + is_moving_averages_ok + "\nAsset price is " + current_price + " and buy price is " + _bought_price;
                //Notify.Email(EmailAddress, "Price Ok for SELL", body);
                //Log(body);
            }

            bool is_stop_limit = current_price < 0.98m * _maximum_price;//current_price < current_price + (_maximum_price - current_price) * 0.90m;

            bool is_gain_ok = is_moving_averages_ok && is_target_price_achieved;
            //is_gain_ok = is_target_price_achieved && is_stop_limit;

            return is_target_price_achieved && (is_moving_averages_ok || is_stop_limit); /*|| IsStopLoss(data)*/;

        }

        private bool IsStopLoss(Slice data)
        {
            decimal current_price = data[SymbolName].Value;

            return current_price < (1.0m - _stop_loss_percentage) * _bought_price;
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
                _purchase_status = PurchaseStatus.Bought;
                _bought_price = orderEvent.FillPrice;
                _max_price_after_buy = _bought_price;
                ObjectStore.Save(LastBoughtObjectStoreKey, _bought_price.ToString(_culture_info));
            }

            if (orderEvent.Direction == OrderDirection.Sell && orderEvent.Status == OrderStatus.Filled)
            {
                _purchase_status = PurchaseStatus.Sold;
                _sold_price = orderEvent.FillPrice;
                _max_price_after_buy = Decimal.MinValue;
                ObjectStore.Save(LastSoldObjectStoreKey, _sold_price.ToString(_culture_info));
            }

            if (orderEvent.Status == OrderStatus.Invalid)
            {
                _purchase_status = orderEvent.Direction == OrderDirection.Buy ? PurchaseStatus.Sold : PurchaseStatus.Bought;
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

        internal class MinMaxMACD
        {
            private int _length = 0;

            private Queue<decimal> _datas = new Queue<decimal>();

            public decimal Max
            {
                get
                {
                    return _datas.Max();
                }
            }

            public decimal Min
            {
                get
                {
                    return _datas.Min();
                }
            }

            public MinMaxMACD(int length)
            {
                _length = length;
            }

            public void OnData(decimal mcd_data)
            {
                _datas.Enqueue(mcd_data);
                while (_datas.Count > _length)
                {
                    _datas.Dequeue();
                }
            }


        }
    }
}
