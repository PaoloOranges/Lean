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
        private AverageDirectionalIndex _adx;

        private bool _bought = false;
        private DateTime bought_time;

        private readonly string SymbolName = "BTCUSD";
        private readonly string CashName = "USD";
        private readonly string CryptoName = "BTC";

        private Symbol _symbol = null;
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            Resolution resolution = Resolution.Daily;
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

            _bought = false;

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

            if(!_bought)
            {
                if(_adx > 15.0 && _macd > 0)
                {
                    decimal btcPrice = Securities[SymbolName].Price;
                    decimal quantity = Math.Round(Portfolio.CashBook["USD"].Amount / btcPrice, 2);
                    Buy(_symbol, quantity);
                    _bought = true;
                    bought_time = data.Time;
                    _max_macd = Decimal.MinValue;


                }
            }
            else
            {
                TimeSpan span = data.Time - bought_time;
                _max_macd = Math.Max(_macd, _max_macd);
                if (_macd <= 0.5m * _max_macd)
                {
                    Liquidate(_symbol);
                    _bought = false;

                }
            }

            // To include any initial holdings, we read the LTC amount from the cashbook
            // instead of using Portfolio["LTCUSD"].Quantity

            //if (_fast > _slow)
            //{
            //    if (!_bought)
            //    {
            //        decimal btcPrice = Securities[SymbolName].Price;
            //        decimal quantity = Math.Round(Portfolio.CashBook["USD"].Amount / btcPrice, 2);
            //        Buy(_symbol, quantity);
            //        _bought = true;
            //    }
            //}
            //else
            //{
            //    if (Portfolio.CashBook["BTC"].Amount > 0)
            //    {
            //        // The following two statements currently behave differently if we have initial holdings:
            //        // https://github.com/QuantConnect/Lean/issues/1860

            //        Liquidate(_symbol);
            //        // SetHoldings("LTCUSD", 0);
            //        _bought = false;
            //    }
            //}

        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            Debug(Time + " " + orderEvent);
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"{Time} - TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            Log($"{Time} - CashBook: {Portfolio.CashBook}");
        }
    }
}
