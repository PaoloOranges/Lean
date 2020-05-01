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
    public class PaoloTestCryptoAlgorithm : QCAlgorithm
    {
        private ExponentialMovingAverage _fast;
        private ExponentialMovingAverage _slow;
        private MovingAverageConvergenceDivergence _macd;
        private decimal _max_macd = Decimal.MinValue;
        private decimal _min_macd = Decimal.MaxValue;
        private decimal _max_adx = Decimal.MinValue;
        private AverageDirectionalIndex _adx;

        private int _bought = -1;
        private DateTime bought_time;

        private readonly string SymbolName = "BTCUSD";
        private readonly string CashName = "USD";
        private readonly string CryptoName = "BTC";

        private Symbol _symbol = null;

        private Action<Slice> _on_data_action = null;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            Resolution resolution = Resolution.Hour;
            _on_data_action = OnDataDaily;

            if(resolution == Resolution.Hour)
            {
                _on_data_action = OnDataHourly;
            }

            SetStartDate(2019, 2, 5); // Set Start Date
            SetEndDate(2020, 4, 1); // Set End Date
                       
            // Set Strategy Cash (EUR)
            // EUR/USD conversion rate will be updated dynamically
            //SetCash("EUR", 300);
            SetCash(CashName, 300);

            // Add some coins as initial holdings
            // When connected to a real brokerage, the amount specified in SetCash
            // will be replaced with the amount in your actual account.
            //SetCash("BTC", 1m);

            SetBrokerageModel(BrokerageName.GDAX, AccountType.Cash);

            
            // You can uncomment the following line when live trading with GDAX,
            // to ensure limit orders will only be posted to the order book and never executed as a taker (incurring fees).
            // Please note this statement has no effect in backtesting or paper trading.
            // DefaultOrderProperties = new GDAXOrderProperties { PostOnly = true };

            // Find more symbols here: http://quantconnect.com/data
            _symbol = AddCrypto(SymbolName, resolution, Market.GDAX).Symbol;
            

            // create two moving averages
            _fast = EMA(_symbol, 5, resolution);
            _slow = EMA(_symbol, 15, resolution);
            _macd = MACD(_symbol, 5, 25, 15, MovingAverageType.Exponential, resolution);
            _adx = ADX(_symbol, 15, resolution);

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

            if(IsWarmingUp)
            {
                return;
            }

            _on_data_action(data);

        }

        private void OnDataDaily(Slice data)
        {
            if (_bought < 0)
            {
                if (_adx > 15.0 && _macd > 0)
                {
                    decimal btcPrice = Securities[SymbolName].Price;
                    decimal quantity = Math.Round(Portfolio.CashBook["USD"].Amount / btcPrice, 2);
                    Buy(_symbol, quantity);
                    _bought = 1;
                    bought_time = data.Time;
                    _max_macd = Decimal.MinValue;

                }
            }
            else
            {
                _max_macd = Math.Max(_macd, _max_macd);

                if (_macd <= 0.5m * _max_macd)
                {
                    Liquidate(_symbol);
                    _bought = -1;

                }
            }
        }

        private void OnDataHourly(Slice data)
        {
            if (_bought < 0)
            {

                const double adx_limit = 25.0;
                if (_adx.PositiveDirectionalIndex > adx_limit && _adx.PositiveDirectionalIndex > _adx.NegativeDirectionalIndex && _macd > 30.0)
                {
                    decimal btcPrice = Securities[SymbolName].Price;
                    decimal quantity = Math.Round(Portfolio.CashBook[CashName].Amount / btcPrice, 2);
                    var order = Buy(_symbol, quantity);
                    _max_macd = Decimal.MinValue;
                    _max_adx = Decimal.MinValue;

                }
            }
            else if(_bought > 0)
            {
                _max_macd = Math.Max(_macd, _max_macd);
                _max_adx = Math.Max(_adx, _max_adx);

                // check on gain
                bool is_adx_ok = _adx < 0.9m * _max_adx;

                decimal holding_value = Portfolio[SymbolName].HoldingsValue;
                decimal current_value = data[SymbolName].Value;

                if(1.5m * holding_value < current_value)
                {
                    var order = Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
                }
                else
                {
                    bool is_price_ok = 1.20m * holding_value < current_value;
                    if (_macd <= 0.5m * _max_macd && is_adx_ok && is_price_ok)
                    {
                        var order = Sell(_symbol, Portfolio.CashBook[CryptoName].Amount);
                    }
                }

                
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            Debug(Time + " " + orderEvent);
            if(orderEvent.Direction == OrderDirection.Buy && orderEvent.Status == OrderStatus.Filled)
            {
                _bought = 1;
                bought_time = orderEvent.UtcTime;
            }

            if (orderEvent.Direction == OrderDirection.Sell && orderEvent.Status == OrderStatus.Filled)
            {
                _bought = -1;
                bought_time = orderEvent.UtcTime;
            }

            if(orderEvent.Status == OrderStatus.Invalid)
            {
                _bought = orderEvent.Direction == OrderDirection.Buy ? -1 : 1;
            }
            if(orderEvent.Status == OrderStatus.Submitted)
            {
                _bought = 0;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"{Time} - TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            Log($"{Time} - CashBook: {Portfolio.CashBook}");
        }
    }
}
