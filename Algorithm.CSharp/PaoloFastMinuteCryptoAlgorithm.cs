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
using QuantConnect.Data;
using QuantConnect.Brokerages;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Interfaces;
using System.Globalization;


namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class PaoloFastMinuteCryptoAlgorithm : QCAlgorithm
    {
        private ExponentialMovingAverage _very_fast_ema;
        private ExponentialMovingAverage _fast_ema;
        private ExponentialMovingAverage _slow_ema;
        private HullMovingAverage _slow_hullma;
        private LeastSquaresMovingAverage _fast_lsma;

        private MovingAverageConvergenceDivergence _macd;
        private ParabolicStopAndReverse _psar;

        private Maximum _maximumPrice;

        private int _bought = -1;
        private decimal _bought_price = 0;

        private const string CryptoName = "ETH";
        private const string CurrencyName = "EUR";
        private const string SymbolName = CryptoName + CurrencyName;

        // Objsect store to save last buy and sell price for different deploy
        private const string LastBoughtObjectStoreKey = SymbolName + "-last-buy";
        private const string LastSoldObjectStoreKey = SymbolName + "-last-sell";

        private Symbol _symbol = null;

        private decimal _sold_price = 0m;

        private bool _is_ready_to_trade = false;

        private const decimal _amount_to_buy = 0.75m;
        private const decimal _percentage_price_gain = 0.1m;

        private const int WarmUpTime = 60;
        private double resolutionInSeconds = 60.0;
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

            if(resolution == Resolution.Hour)
            {
                resolutionInSeconds = 3600.0;
            }
            SetStartDate(2021, 3, 1); // Set Start Date
            SetEndDate(2021, 6, 6); // Set End Date

            SetAccountCurrency(CurrencyName);
            SetCash(1000);

            //SetCash(CryptoName, 0.08m);

            _symbol = AddCrypto(SymbolName, resolution, Market.GDAX).Symbol;
            SetBenchmark(_symbol);

            const int veryFastValue = 5;
            const int fastValue = 15;
            const int slowValue = 30;
            const int signal = 8;

            _very_fast_ema = EMA(_symbol, veryFastValue, resolution);
            _fast_ema = EMA(_symbol, fastValue, resolution);
            _slow_ema = EMA(_symbol, slowValue, resolution);
            _slow_hullma = HMA(_symbol, slowValue, resolution);
            _fast_lsma = LSMA(_symbol, fastValue, resolution);

            _macd = MACD(_symbol, fastValue, slowValue, signal, MovingAverageType.Exponential, resolution);
            _psar = PSAR(_symbol, 0.025m, 0.025m, 0.25m, resolution);

            _maximumPrice = MAX(_symbol, WarmUpTime, resolution);
            
            SetWarmUp(TimeSpan.FromDays(7));

        }

        public override void PostInitialize()
        {

            if (Portfolio.CashBook[CryptoName].Amount > 0)
            {
                Log(CryptoName + " amount in Portfolio: " + Portfolio.CashBook[CryptoName].Amount + " - Initialized to Bought");
                _bought = 1;
                if (HasBoughtPriceFromPreviousSession)
                {
                    string bought_price = ObjectStore.Read(LastBoughtObjectStoreKey);
                    Log("Previous Purchase found: " + bought_price );
                    _bought_price = Convert.ToDecimal(bought_price, _culture_info);
                }
                _is_ready_to_trade = true;
            }
            else
            {
                Log(CurrencyName + " amount in Portfolio: " + Portfolio.CashBook[CurrencyName].Amount + " - Initialized to Sold");
                _bought = -1;
                if(HasSoldPriceFromPreviousSession)
                {
                    string sold_price = ObjectStore.Read(LastSoldObjectStoreKey);
                    Log("Previous Sell found: " + sold_price);
                    _sold_price = Convert.ToDecimal(sold_price, _culture_info);
                }

                _is_ready_to_trade = false;
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
            if (IsWarmingUp)
            {
                if(_bought < 0 && !HasSoldPriceFromPreviousSession)
                {
                    _sold_price = _maximumPrice;
                }               
                return;
            }

            if(_is_ready_to_trade)
            {
                OnProcessData(data);
            }
            else
            {
                OnPrepareToTrade(data);
            }

            DateTime UtcTimeNow = UtcTime;
            
            if (Math.Floor((UtcTimeNow - UtcTimeLast).TotalSeconds) > resolutionInSeconds)
            {
                
                Log("WrongTime! Last: " + UtcTimeLast.ToString(_culture_info) + " Now: " + UtcTimeNow.ToString(_culture_info));
            }
            UtcTimeLast = UtcTimeNow;

#if DEBUG && PLOT_CHART
            Plot(SymbolName, "Price", data[SymbolName].Value);

            Plot("Indicators", "MACD", _macd.Histogram.Current.Value);
            Plot("Indicators", "VeryFastMA", _very_fast_ema);
            Plot("Indicators", "FastMA", _fast_ema);
            Plot("Indicators", "SlowMA", _slow_ema);
#endif
        }

        private void OnProcessData(Slice data)
        {
#if LOG_INDICATORS
            Log("INDICATORS. VeryFastEMA: " + _very_fast_ema + " - FastEMA: " + _fast_ema + " - SlowEMA: " + _slow_ema + " - MACD: " + _macd.Histogram.Current.Value);
#endif

            decimal current_price = Securities[SymbolName].Price;
            if (_bought < 0)
            {
                bool is_macd_ok = _macd.Histogram.Current.Value > 0;
                bool is_moving_averages_ok = /*_fast_ema > _slow_ema && _very_fast_ema > _fast_ema*/ _fast_lsma > _slow_hullma ;
                bool is_psar_ok = current_price > _psar;

                if (is_moving_averages_ok /*&& is_macd_ok*/)
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
            else if (_bought > 0)
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

        private void OnPrepareToTrade(Slice data)
        {
            if (IsOkToSell(data))
            {
                _is_ready_to_trade = true;
                //Notify.Email(EmailAddress, "Algorithm Ready to Trade", "Ready to trade");
                Log("Algorithm Ready to Trade");
            }
        }
        
        private bool IsOkToSell(Slice data)
        {

            decimal current_price = data[SymbolName].Value;
            
            //bool is_adx_ok = _adx.PositiveDirectionalIndex > 25 && _adx.NegativeDirectionalIndex < 20;
            bool is_macd_ok = _macd.Histogram.Current.Value < 0;
            bool is_moving_averages_ok = _fast_lsma < _slow_hullma; //_very_fast_ema < _fast_ema;


            bool is_price_ok = current_price > (1.0m + _percentage_price_gain) * _bought_price;

            if (is_price_ok)
            {
                string body = "Price is ok, MACD is " + is_macd_ok + " with value " + _macd.Histogram.Current.Value + "\nVeryFastMA is " + is_moving_averages_ok + "\nAsset price is " + current_price + " and buy price is " + _bought_price;
                //Notify.Email(EmailAddress, "Price Ok for SELL", body);
                //Log(body);
            }

            bool is_gain_ok = is_moving_averages_ok /*&& is_macd_ok*/ && is_price_ok;
            bool is_stop_loss = _fast_ema < _slow_ema && is_macd_ok;
            return is_gain_ok;

        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if(orderEvent == null)
            {
                return;
            }

            Debug(Time + " " + orderEvent);

            if (orderEvent.Direction == OrderDirection.Buy && orderEvent.Status == OrderStatus.Filled)
            {
                _bought = 1;
                _bought_price = orderEvent.FillPrice;
                ObjectStore.Save(LastBoughtObjectStoreKey, _bought_price.ToString(_culture_info));
            }

            if (orderEvent.Direction == OrderDirection.Sell && orderEvent.Status == OrderStatus.Filled)
            {
                _bought = -1;
                _sold_price = orderEvent.FillPrice;
                ObjectStore.Save(LastSoldObjectStoreKey, _sold_price.ToString(_culture_info));
            }

            if (orderEvent.Status == OrderStatus.Invalid)
            {
                _bought = orderEvent.Direction == OrderDirection.Buy ? -1 : 1;
            }

            if (orderEvent.Status == OrderStatus.Submitted)
            {
                _bought = 0;
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
