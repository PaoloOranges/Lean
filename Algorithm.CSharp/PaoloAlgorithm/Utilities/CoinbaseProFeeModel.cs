/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
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

namespace QuantConnect.Algorithm.CSharp.PaoloAlgorithm
{
    /// <summary>
    /// Coinbase Pro base-tier fee model (owner-confirmed 2026-09-30: taker 0.60% / maker 0.40%
    /// per side). Supersedes the Advanced-1 defaults (0.80% taker / 0.60% maker) that
    /// <see cref="QuantConnect.Orders.Fees.CoinbaseFeeModel"/> applies when constructed without
    /// arguments - which is what every program backtest so far used.
    ///
    /// Hygiene fix for card t_3409ddf9. Root-cause note (t_5a6205b8 audit said "maker fills were
    /// charged the 0.80% taker rate"): LEAN's maker/taker *classification* is correct
    /// (CoinbaseFeeModel.GetOrderFee prices a Limit as maker only when PostOnly or not marketable);
    /// the mispricing had two independent causes, neither in the classification:
    ///   1. Wrong tier: the harness never passed fee rates, so every fill - taker or not - was
    ///      priced at Advanced-1 (0.80% taker). The real account tier is Pro base (0.60%/0.40%).
    ///   2. No maker fills existed to misprice: v2 entries are MarketOrder (taker by nature) and
    ///      v2.1 entry limits are *marketable* (limit = close * 1.0005 >= ask), which Coinbase
    ///      fills as taker. "Maker entries" were maker in intent, not in execution.
    /// This subclass only re-tiers the rates; the maker/taker switch is inherited unchanged.
    /// </summary>
    public class CoinbaseProFeeModel : QuantConnect.Orders.Fees.CoinbaseFeeModel
    {
        /// <summary>Pro base maker fee (0.40%/side).</summary>
        public const decimal MakerProBase = 0.004m;

        /// <summary>Pro base taker fee (0.60%/side).</summary>
        public const decimal TakerProBase = 0.006m;

        /// <summary>
        /// Create the fee model with the owner-confirmed Pro base tier.
        /// </summary>
        public CoinbaseProFeeModel()
            : base(MakerProBase, TakerProBase)
        {
        }
    }
}
