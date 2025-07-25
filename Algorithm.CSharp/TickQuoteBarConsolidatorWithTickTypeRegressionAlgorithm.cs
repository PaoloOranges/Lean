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
 *
*/

using System;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Securities.Future;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// This algorithm tests the functionality of the TickQuoteBarConsolidator with tick data.
    /// The SubscriptionManager.AddConsolidator method uses a Quote TickType
    /// It checks if data consolidation occurs as expected for the given time period. If consolidation does not happen, a RegressionTestException is thrown.
    /// </summary>
    public class TickQuoteBarConsolidatorWithTickTypeRegressionAlgorithm : QCAlgorithm, IRegressionAlgorithmDefinition
    {
        private Dictionary<Symbol, TickQuoteBarConsolidator> _consolidators = new Dictionary<Symbol, TickQuoteBarConsolidator>();
        private bool _itWasConsolidated;
        protected Future GoldFuture { get; set; }
        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 9);

            GoldFuture = AddFuture("GC", Resolution.Tick, Market.COMEX);
            GoldFuture.SetFilter(0, 180);
        }

        private void OnConsolidated(object sender, QuoteBar bar)
        {
            _itWasConsolidated = true;
        }

        public override void OnData(Slice slice)
        {
            if (!_consolidators.ContainsKey(GoldFuture.Mapped))
            {
                var consolidator = new TickQuoteBarConsolidator(TimeSpan.FromSeconds(10));
                consolidator.DataConsolidated += OnConsolidated;
                AddConsolidator(consolidator);
                _consolidators[GoldFuture.Mapped] = consolidator;
            }
        }

        protected virtual void AddConsolidator(TickQuoteBarConsolidator consolidator)
        {
            SubscriptionManager.AddConsolidator(GoldFuture.Mapped, consolidator, TickType.Quote);
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_itWasConsolidated)
            {
                throw new RegressionTestException("TickQuoteBarConsolidator did not consolidate any data.");
            }
        }

        /// <summary>
        /// This is used by the regression test system to indicate if the open source Lean repository has the required data to run this algorithm.
        /// </summary>
        public bool CanRunLocally { get; } = true;

        /// <summary>
        /// This is used by the regression test system to indicate which languages this algorithm is written in.
        /// </summary>
        public List<Language> Languages { get; } = new() { Language.CSharp };

        /// <summary>
        /// Data Points count of all timeslices of algorithm
        /// </summary>
        public long DataPoints => 1082920;

        /// <summary>
        /// Data Points count of the algorithm history
        /// </summary>
        public int AlgorithmHistoryDataPoints => 0;

        /// <summary>
        /// Final status of the algorithm
        /// </summary>
        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        /// <summary>
        /// This is used by the regression test system to indicate what the expected statistics are from running the algorithm
        /// </summary>
        public Dictionary<string, string> ExpectedStatistics => new Dictionary<string, string>
        {
            {"Total Orders", "0"},
            {"Average Win", "0%"},
            {"Average Loss", "0%"},
            {"Compounding Annual Return", "0%"},
            {"Drawdown", "0%"},
            {"Expectancy", "0"},
            {"Start Equity", "100000"},
            {"End Equity", "100000"},
            {"Net Profit", "0%"},
            {"Sharpe Ratio", "0"},
            {"Sortino Ratio", "0"},
            {"Probabilistic Sharpe Ratio", "0%"},
            {"Loss Rate", "0%"},
            {"Win Rate", "0%"},
            {"Profit-Loss Ratio", "0"},
            {"Alpha", "0"},
            {"Beta", "0"},
            {"Annual Standard Deviation", "0"},
            {"Annual Variance", "0"},
            {"Information Ratio", "5.524"},
            {"Tracking Error", "0.136"},
            {"Treynor Ratio", "0"},
            {"Total Fees", "$0.00"},
            {"Estimated Strategy Capacity", "$0"},
            {"Lowest Capacity Asset", ""},
            {"Portfolio Turnover", "0%"},
            {"Drawdown Recovery", "0"},
            {"OrderListHash", "d41d8cd98f00b204e9800998ecf8427e"}
        };
    }
}
