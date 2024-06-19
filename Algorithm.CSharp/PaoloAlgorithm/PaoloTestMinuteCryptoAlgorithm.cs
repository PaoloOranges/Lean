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

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class PaoloTestMinuteCryptoAlgorithm : QCAlgorithm
    {
        private ExponentialMovingAverage _fast;
        private LeastSquaresMovingAverage _slow;
        private MovingAverageConvergenceDivergence _macd;
        private AverageDirectionalIndex _adx;

        private decimal _max_macd = decimal.MinValue;
        private decimal _min_macd = decimal.MaxValue;
        private decimal _max_adx = decimal.MinValue;

        private MinMaxMACD _min_max_macd;
        private int _bought = -1;
        private decimal _price_bought = 0;

        private readonly string SymbolName = "BTCUSD";
        private readonly string CashName = "USD";
        private readonly string CryptoName = "BTC";

        private Symbol _symbol = null;

        private decimal _sold_price = decimal.MaxValue;
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            Resolution resolution = Resolution.Minute;

            SetStartDate(2020, 12, 1); // Set Start Date
            SetEndDate(2020, 12, 10); // Set End Date

            SetCash(CashName, 300);

            SetBrokerageModel(BrokerageName.GDAX, AccountType.Cash);

            // Find more symbols here: http://quantconnect.com/data
            _symbol = AddCrypto(SymbolName, resolution, Market.GDAX).Symbol;

            _fast = EMA(_symbol, 5, resolution);
            _slow = LSMA(_symbol, 30, resolution);
            _macd = MACD(_symbol, 10, 35, 15, MovingAverageType.Exponential, resolution);
            _adx = ADX(_symbol, 35, resolution);

            _min_max_macd = new MinMaxMACD(15);

            _bought = -1;

            SetWarmUp(30);
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

            OnDataDummy(data);
            //OnProcessData(data);

        }

        private void OnDataDummy(Slice data)
        {
            if (_bought < 0)
            {
                decimal btcPrice = Securities[SymbolName].Price;
                //if(btcPrice < 0.95m * _sold_price)
                {
                    decimal quantity = 0.001m;// Math.Round(1.0m / btcPrice, 3, MidpointRounding.ToZero);

                    Buy(_symbol, quantity);
                }
            }
            else if (_bought > 0)
            {
                decimal holding_value = Portfolio[SymbolName].Price;
                decimal current_price = data[SymbolName].Value;

                bool is_price_ok = current_price > 1.01m * _price_bought;
                //if (is_price_ok)
                {
                    Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
                }
            }
        }

        private void OnProcessData(Slice data)
        {
            _min_max_macd.OnData(_macd);

            if (_bought < 0)
            {
                bool is_adx_ok = _adx.NegativeDirectionalIndex > 24 && _adx.PositiveDirectionalIndex < 16;
                bool is_macd_ok = _macd > _macd.Signal; /*&& _macd - _min_max_macd.Min > 50.0m */;
                bool is_moving_averages_ok = _fast > _slow;
                if (is_adx_ok && is_macd_ok && is_moving_averages_ok)
                {
                    decimal btcPrice = Securities[SymbolName].Price;
                    const decimal round_multiplier = 1000m;
                    decimal amount_to_buy = Portfolio.CashBook[CashName].Amount;
                    decimal quantity = Math.Truncate(round_multiplier * amount_to_buy / btcPrice) / round_multiplier;
                    var order = Buy(_symbol, quantity);
                    _max_macd = decimal.MinValue;
                    _max_adx = decimal.MinValue;

                }
            }
            else if (_bought > 0)
            {
                _max_macd = Math.Max(_macd, _max_macd);
                _max_adx = Math.Max(_adx, _max_adx);

                // check on gain
                bool is_adx_ok = _adx.PositiveDirectionalIndex > 25 && _adx.NegativeDirectionalIndex < 20;
                bool is_macd_ok = _macd < _macd.Signal;
                bool is_moving_averages_ok = _fast < _slow;

                decimal holding_value = Portfolio[SymbolName].Price;
                decimal current_price = data[SymbolName].Value;

                //if(1.5m * holding_value < current_value)
                //{
                //    var order = Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
                //}
                //else
                {
                    bool is_price_ok = current_price > 1.1m * _price_bought;
                    if (is_adx_ok && is_macd_ok && is_moving_averages_ok && is_price_ok)
                    {
                        Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
                    }
                }

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