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

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Brokerages;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Interfaces;

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
        private ExponentialMovingAverage _fast;
        private ExponentialMovingAverage _slow;
        private MovingAverageConvergenceDivergence _macd;
        private AverageDirectionalIndex _adx;

        private decimal _max_adx = Decimal.MinValue;

        private MinMaxMACD _min_max_macd;
        private int _bought = -1;
        private decimal _price_bought = 0;

        private readonly string SymbolName = "BTCUSD";
        private readonly string CashName = "USD";
        private readonly string CryptoName = "BTC";

        private Symbol _symbol = null;

        private decimal _sold_price = Decimal.MaxValue;

        private bool _isReadyToTrade = false;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            Resolution resolution = Resolution.Minute;

            SetStartDate(2020, 1, 11); // Set Start Date
            SetEndDate(2020, 12, 12); // Set End Date

            SetCash(CashName, 300);

            SetBrokerageModel(BrokerageName.GDAX, AccountType.Cash);
            SetTimeZone(NodaTime.DateTimeZone.Utc);

            // Find more symbols here: http://quantconnect.com/data
            _symbol = AddCrypto(SymbolName, resolution, Market.GDAX).Symbol;

            _fast = EMA(_symbol, 15, resolution);
            _slow = EMA(_symbol, 45, resolution);
            _macd = MACD(_symbol, 10, 45, 15, MovingAverageType.Exponential, resolution);

            _min_max_macd = new MinMaxMACD(15);

            _bought = -1;

            _isReadyToTrade = false;

            SetWarmUp(0);
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public override void OnData(Slice data)
        {
            if (Portfolio.CashBook["USD"].ConversionRate == 0
                || Portfolio.CashBook["BTC"].ConversionRate == 0)
            {
                Log($"{CashName} conversion rate: {Portfolio.CashBook[CashName].ConversionRate}");
                Log($"{CryptoName} conversion rate: {Portfolio.CashBook[CryptoName].ConversionRate}");

                throw new Exception("Conversion rate is 0");
            }

            if (IsWarmingUp)
            {
                return;
            }

            if(_isReadyToTrade)
            {
                OnProcessData(data);
            }
            else
            {
                OnPrepareToTrade(data);
            }

        }

        private void OnProcessData(Slice data)
        {
            _min_max_macd.OnData(_macd);

            if (_bought < 0)
            {
                //bool is_adx_ok = _adx.NegativeDirectionalIndex > 24 && _adx.PositiveDirectionalIndex < 16;
                bool is_macd_ok = _macd.Histogram.Current.Value > 0;                
                bool is_moving_averages_ok = _fast > _slow;
                if (/*is_adx_ok && */is_macd_ok && is_moving_averages_ok)
                {
                    decimal btcPrice = Securities[SymbolName].Price;
                    decimal quantity = Math.Truncate(1000m * Portfolio.CashBook[CashName].Amount / btcPrice) / 1000m;
                    var order = Buy(_symbol, quantity);
                }
            }
            else if (_bought > 0)
            {
                if (IsOkToSell(data))
                {
                    Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
                }
            }
        }
        
        private void OnPrepareToTrade(Slice data)
        {
            if (IsOkToSell(data))
            {
                _isReadyToTrade = true;
            }
        }
        
        private bool IsOkToSell(Slice data)
        {

            // check on gain
            //bool is_adx_ok = _adx.PositiveDirectionalIndex > 25 && _adx.NegativeDirectionalIndex < 20;
            bool is_macd_ok = _macd.Histogram.Current.Value < 0;
            bool is_moving_averages_ok = _fast < _slow;

            decimal holding_value = Portfolio[SymbolName].Price;
            decimal current_price = data[SymbolName].Value;

            //if(1.5m * holding_value < current_value)
            //{
            //    return true;
            //}
            //else
            {
                bool is_price_ok = current_price > 1.05m * _price_bought;
                return /*is_adx_ok && */is_macd_ok && is_moving_averages_ok && is_price_ok;
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            Debug(Time + " " + orderEvent);
            if (orderEvent.Direction == OrderDirection.Buy && orderEvent.Status == OrderStatus.Filled)
            {
                _bought = 1;
                _price_bought = orderEvent.FillPrice;
            }

            if (orderEvent.Direction == OrderDirection.Sell && orderEvent.Status == OrderStatus.Filled)
            {
                _bought = -1;
                _sold_price = orderEvent.FillPrice;
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
