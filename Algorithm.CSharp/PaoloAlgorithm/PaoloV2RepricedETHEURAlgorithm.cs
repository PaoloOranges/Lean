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
    /// Harness-layer repricing wrapper for the v2 fee-bug hygiene re-run (card t_3409ddf9).
    ///
    /// ZERO logic change to PaoloV2ETHEURAlgorithm@00686124b: this class only swaps the ETHEUR
    /// security's fee model from the Advanced-1 default (taker 0.80% / maker 0.60%) injected by
    /// SetBrokerageModel(Coinbase)/SetFeeModel in the base Initialize() to the owner-confirmed
    /// Coinbase Pro base tier (taker 0.60% / maker 0.40%, CoinbaseProFeeModel). Fee models are
    /// consulted at fill time, so overriding after base.Initialize() is sufficient and complete.
    ///
    /// Why a wrapper instead of editing the v2 file: the A/B protocol pins v2 @ 00686124b as
    /// unmodified logic; the fee tier is harness configuration, not strategy. Same reasoning
    /// keeps v1 (PaoloHourETHEURAlgorithm) untouched: v1 trades pure taker paths at Advanced-1
    /// and its pinned numbers (-9.337% / 44.0% / EUR 46.55 / 7 orders) are the exact-parity
    /// regression check for this batch - the fix must not move them.
    ///
    /// Selection is per-symbol (ETHEUR only): benchmark/quote-currency securities keep their
    /// defaults; they never generate fee-bearing fills here.
    /// </summary>
    public class PaoloV2RepricedETHEURAlgorithm : PaoloV2ETHEURAlgorithm
    {
        /// <summary>
        /// Runs the unmodified v2 Initialize(), then reprices ETHEUR fills at the Pro base tier.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();

            foreach (var security in Securities.Values)
            {
                if (security.Symbol.Value == "ETHEUR")
                {
                    security.SetFeeModel(new CoinbaseProFeeModel());
                    Debug($"t_3409ddf9 repricing: {security.Symbol} fee model -> Pro base (taker {CoinbaseProFeeModel.TakerProBase:P}, maker {CoinbaseProFeeModel.MakerProBase:P})");
                }
            }
        }
    }
}
