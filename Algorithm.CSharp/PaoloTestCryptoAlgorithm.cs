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
    public class PaoloTestCryptoAlgorithm : QCAlgorithm, IRegressionAlgorithmDefinition
    {
        private ExponentialMovingAverage _fast;
        private ExponentialMovingAverage _slow;

        private bool _bought = false;
        readonly string SymbolName = "BTCUSD";

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetStartDate(2020, 1, 1); // Set Start Date
            SetEndDate(2020, 4, 1); // Set End Date
                       
            // Set Strategy Cash (EUR)
            // EUR/USD conversion rate will be updated dynamically
            //SetCash("EUR", 300);
            SetCash("USD", 300);

            // Add some coins as initial holdings
            // When connected to a real brokerage, the amount specified in SetCash
            // will be replaced with the amount in your actual account.
            //SetCash("BTC", 1m);

            SetBrokerageModel(BrokerageName.GDAX, AccountType.Cash);

            SetWarmUp(new TimeSpan(24, 0, 0));
            // You can uncomment the following line when live trading with GDAX,
            // to ensure limit orders will only be posted to the order book and never executed as a taker (incurring fees).
            // Please note this statement has no effect in backtesting or paper trading.
            // DefaultOrderProperties = new GDAXOrderProperties { PostOnly = true };

            // Find more symbols here: http://quantconnect.com/data
            var symbol = AddCrypto(SymbolName, Resolution.Hour, Market.GDAX).Symbol;
            

            // create two moving averages
            _fast = EMA(symbol, 5, Resolution.Hour);
            _slow = EMA(symbol, 14, Resolution.Hour);
            _bought = false;
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
                Log($"EUR conversion rate: {Portfolio.CashBook["USD"].ConversionRate}");
                Log($"BTC conversion rate: {Portfolio.CashBook["BTC"].ConversionRate}");

                throw new Exception("Conversion rate is 0");
            }

    
            
                // To include any initial holdings, we read the LTC amount from the cashbook
                // instead of using Portfolio["LTCUSD"].Quantity

                if (_fast > _slow)
                {
                    if(!_bought)
                    {
                        decimal btcPrice = Securities[SymbolName].Price;
                        decimal quantity = Math.Round(Portfolio.CashBook["USD"].Amount / btcPrice, 2);
                        Buy(SymbolName, quantity);
                        _bought = true;
                    }
                }
                else
                {
                    if (Portfolio.CashBook["BTC"].Amount > 0)
                    {
                        // The following two statements currently behave differently if we have initial holdings:
                        // https://github.com/QuantConnect/Lean/issues/1860

                        Liquidate(SymbolName);
                        // SetHoldings("LTCUSD", 0);
                        _bought = false;
                    }
                }
            
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

        /// <summary>
        /// This is used by the regression test system to indicate if the open source Lean repository has the required data to run this algorithm.
        /// </summary>
        public bool CanRunLocally { get; } = true;

        /// <summary>
        /// This is used by the regression test system to indicate which languages this algorithm is written in.
        /// </summary>
        public Language[] Languages { get; } = { Language.CSharp, Language.Python };

        /// <summary>
        /// This is used by the regression test system to indicate what the expected statistics are from running the algorithm
        /// </summary>
        public Dictionary<string, string> ExpectedStatistics => new Dictionary<string, string>
        {
            {"Total Trades", "10"},
            {"Average Win", "0%"},
            {"Average Loss", "0%"},
            {"Compounding Annual Return", "0%"},
            {"Drawdown", "0%"},
            {"Expectancy", "0"},
            {"Net Profit", "0%"},
            {"Sharpe Ratio", "0"},
            {"Probabilistic Sharpe Ratio", "0%"},
            {"Loss Rate", "0%"},
            {"Win Rate", "0%"},
            {"Profit-Loss Ratio", "0"},
            {"Alpha", "0"},
            {"Beta", "0"},
            {"Annual Standard Deviation", "0"},
            {"Annual Variance", "0"},
            {"Information Ratio", "0"},
            {"Tracking Error", "0"},
            {"Treynor Ratio", "0"},
            {"Total Fees", "$85.33"},
            {"Fitness Score", "0.5"},
            {"Kelly Criterion Estimate", "0"},
            {"Kelly Criterion Probability Value", "0"},
            {"Sortino Ratio", "79228162514264337593543950335"},
            {"Return Over Maximum Drawdown", "-43.917"},
            {"Portfolio Turnover", "1.028"},
            {"Total Insights Generated", "0"},
            {"Total Insights Closed", "0"},
            {"Total Insights Analysis Completed", "0"},
            {"Long Insight Count", "0"},
            {"Short Insight Count", "0"},
            {"Long/Short Ratio", "100%"},
            {"Estimated Monthly Alpha Value", "$0"},
            {"Total Accumulated Estimated Alpha Value", "$0"},
            {"Mean Population Estimated Insight Value", "$0"},
            {"Mean Population Direction", "0%"},
            {"Mean Population Magnitude", "0%"},
            {"Rolling Averaged Population Direction", "0%"},
            {"Rolling Averaged Population Magnitude", "0%"},
            {"OrderListHash", "1073240275"}
        };
    }
}
