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
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Common.Securities
{
    // The tests have been verified using the CBOE Margin Calculator
    // http://www.cboe.com/trading-tools/calculators/margin-calculator

    [TestFixture]
    public class OptionMarginBuyingPowerModelTests
    {
        // Test class to enable calling protected methods

        [Test]
        public void OptionMarginBuyingPowerModelInitializationTests()
        {
            var tz = TimeZones.NewYork;
            var option = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_P_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            var buyingPowerModel = new OptionMarginModel();

            // we test that options dont have leverage (100%) and it cannot be changed
            Assert.AreEqual(1m, buyingPowerModel.GetLeverage(option));
            Assert.Throws<InvalidOperationException>(() => buyingPowerModel.SetLeverage(option, 10m));
            Assert.AreEqual(1m, buyingPowerModel.GetLeverage(option));
        }

        [Test]
        public void TestLongCallsPuts()
        {
            const decimal price = 1.2345m;
            const decimal underlyingPrice = 200m;
            var tz = TimeZones.NewYork;

            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionPut = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_P_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionPut.SetMarketPrice(new Tick { Value = price });
            optionPut.Underlying = equity;
            optionPut.Holdings.SetHoldings(1m, 2);

            var optionCall = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_C_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionCall.SetMarketPrice(new Tick { Value = price });
            optionCall.Underlying = equity;
            optionCall.Holdings.SetHoldings(1.5m, 2);

            var buyingPowerModel = new OptionMarginModel();

            // we expect long positions to be 100% charged.
            Assert.AreEqual(optionPut.Holdings.AbsoluteHoldingsCost, buyingPowerModel.GetMaintenanceMargin(optionPut));
            Assert.AreEqual(optionCall.Holdings.AbsoluteHoldingsCost, buyingPowerModel.GetMaintenanceMargin(optionCall));
        }

        [Test]
        public void TestShortCallsITM()
        {
            const decimal price = 14m;
            const decimal underlyingPrice = 196m;
            var tz = TimeZones.NewYork;

            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionCall = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_C_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionCall.SetMarketPrice(new Tick { Value = price });
            optionCall.Underlying = equity;
            optionCall.Holdings.SetHoldings(price, -2);

            var buyingPowerModel = new OptionMarginModel();

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (14 + 0.2 * 196) = 10640
            Assert.AreEqual(10640m, buyingPowerModel.GetMaintenanceMargin(optionCall));
        }

        [Test]
        public void TestShortCallsOTM()
        {
            const decimal price = 14m;
            const decimal underlyingPrice = 180m;
            var tz = TimeZones.NewYork;

            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionCall = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_C_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionCall.SetMarketPrice(new Tick { Value = price });
            optionCall.Underlying = equity;
            optionCall.Holdings.SetHoldings(price, -2);

            var buyingPowerModel = new OptionMarginModel();

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (14 + 0.2 * 180 - (192 - 180)) = 7600
            Assert.AreEqual(7600, (double)buyingPowerModel.GetMaintenanceMargin(optionCall), 0.01);
        }

        [Test]
        public void TestShortPutsITM()
        {
            const decimal price = 14m;
            const decimal underlyingPrice = 182m;
            var tz = TimeZones.NewYork;

            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionPut = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_P_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionPut.SetMarketPrice(new Tick { Value = price });
            optionPut.Underlying = equity;
            optionPut.Holdings.SetHoldings(price, -2);

            var buyingPowerModel = new OptionMarginModel();

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (14 + 0.2 * 182) = 10080
            Assert.AreEqual(10080m, buyingPowerModel.GetMaintenanceMargin(optionPut));
        }

        [Test]
        public void TestShortPutsOTM()
        {
            const decimal price = 14m;
            const decimal underlyingPrice = 196m;
            var tz = TimeZones.NewYork;

            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionCall = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbols.SPY_P_192_Feb19_2016,
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionCall.SetMarketPrice(new Tick { Value = price });
            optionCall.Underlying = equity;
            optionCall.Holdings.SetHoldings(price, -2);

            var buyingPowerModel = new OptionMarginModel();

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (14 + 0.2 * 196 - (196 - 192)) = 9840
            Assert.AreEqual(9840, (double)buyingPowerModel.GetMaintenanceMargin(optionCall), 0.01);
        }

        [Test]
        public void TestShortPutFarITM()
        {
            const decimal price = 0.18m;
            const decimal underlyingPrice = 200m;

            var tz = TimeZones.NewYork;
            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionPutSymbol = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American, OptionRight.Put, 207m, new DateTime(2015, 02, 27));
            var optionPut = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), optionPutSymbol, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionPut.SetMarketPrice(new Tick { Value = price });
            optionPut.Underlying = equity;
            optionPut.Holdings.SetHoldings(price, -2);

            var buyingPowerModel = new OptionMarginModel();

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (0.18 + 0.2 * 200) = 8036
            Assert.AreEqual(8036, (double)buyingPowerModel.GetMaintenanceMargin(optionPut), 0.01);
        }

        [Test]
        public void TestShortPutMovingFarITM()
        {
            const decimal optionPriceStart = 4.68m;
            const decimal underlyingPriceStart = 192m;
            const decimal optionPriceEnd = 0.18m;
            const decimal underlyingPriceEnd = 200m;

            var tz = TimeZones.NewYork;
            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPriceStart });

            var optionPutSymbol = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American, OptionRight.Put, 207m, new DateTime(2015, 02, 27));
            var optionPut = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), optionPutSymbol, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionPut.SetMarketPrice(new Tick { Value = optionPriceStart });
            optionPut.Underlying = equity;
            optionPut.Holdings.SetHoldings(optionPriceStart, -2);

            var buyingPowerModel = new OptionMarginModel();

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (4.68 + 0.2 * 192) = 8616
            Assert.AreEqual(8616, (double)buyingPowerModel.GetMaintenanceMargin(optionPut), 0.01);

            equity.SetMarketPrice(new Tick { Value = underlyingPriceEnd });
            optionPut.SetMarketPrice(new Tick { Value = optionPriceEnd });

            // short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (4.68 + 0.2 * 200) = 8936
            Assert.AreEqual(8936, (double)buyingPowerModel.GetMaintenanceMargin(optionPut), 0.01);
        }

        [TestCase(0)]
        [TestCase(10000)]
        public void NonAccountCurrency_GetBuyingPower(decimal nonAccountCurrencyCash)
        {
            var algorithm = new QCAlgorithm();
            algorithm.SubscriptionManager.SetDataManager(new DataManagerStub(algorithm));
            algorithm.Portfolio.SetAccountCurrency("EUR");
            algorithm.Portfolio.SetCash(10000);
            algorithm.Portfolio.SetCash(Currencies.USD, nonAccountCurrencyCash, 0.88m);

            var option = algorithm.AddOption("SPY");

            var buyingPowerModel = new OptionMarginModel();
            var quantity = buyingPowerModel.GetBuyingPower(new BuyingPowerParameters(
                algorithm.Portfolio, option, OrderDirection.Buy));

            Assert.AreEqual(10000m + algorithm.Portfolio.CashBook[Currencies.USD].ValueInAccountCurrency,
                quantity.Value);
        }

        // For -1.5% target (15k), we can short -2 contracts for 478 margin requirement per unit
        [TestCase(0, -2, -.015)] // Open Short (0 + -2 = -2)
        [TestCase(-1, -1, -.015)] // Short to Shorter (-1 + -1 = -2)
        [TestCase(-2, 0, -.015)] // No action
        [TestCase(2, -4, -.015)] // Long To Short (2 + -4 = -2)

        // -40% Target (~-400k), we can short -58 contracts for 478 margin requirement per unit
        [TestCase(0, -58, -0.40)] // Open Short (0 + -58 = -58)
        [TestCase(-2, -56, -0.40)] // Short to Shorter (-2 + -56 = -58)
        [TestCase(2, -60, -0.40)] // Long To Short (2 + -60 = -58)

        // 40% Target (~400k), we can buy 836 contracts
        [TestCase(0, 836, 0.40)] // Open Long (0 + 836 = 836)
        [TestCase(-2, 838, 0.40)] // Short to Long (-2 + 838 = 836)
        [TestCase(2, 834, 0.40)] // Long To Longer (2 + 834 = 836)

        // ~0.04% Target (~400). This is below the needed margin for one unit. We end up at 0 holdings for all cases.
        [TestCase(0, 0, 0.0004)] // Open Long (0 + 0 = 0)
        [TestCase(-2, 2, 0.0004)] // Short to Long (-2 + 2 = 0)
        [TestCase(2, -2, 0.0004)] // Long To Longer (2 + -2 = 0)
        public void CallOTM_MarginRequirement(int startingHoldings, int expectedOrderSize, decimal targetPercentage)
        {
            // Initialize algorithm
            var algorithm = new QCAlgorithm();
            algorithm.SetFinishedWarmingUp();
            algorithm.Transactions.SetOrderProcessor(new FakeOrderProcessor());

            algorithm.SetCash(1000000);
            algorithm.SubscriptionManager.SetDataManager(new DataManagerStub(algorithm));

            var optionSymbol = Symbols.CreateOptionSymbol("SPY", OptionRight.Call, 411m, DateTime.UtcNow);
            var option = algorithm.AddOptionContract(optionSymbol);

            option.Holdings.SetHoldings(4.74m, startingHoldings);
            Assert.GreaterOrEqual(algorithm.Portfolio.MarginRemaining, 0);
            option.FeeModel = new ConstantFeeModel(0);
            option.SetLeverage(1);

            // Update option data
            UpdatePrice(option, 4.78m);

            // Update the underlying data
            UpdatePrice(option.Underlying, 395.51m);

            var model = new OptionMarginModel();
            var result = model.GetMaximumOrderQuantityForTargetBuyingPower(algorithm.Portfolio, option, targetPercentage, 0);
            Assert.AreEqual(expectedOrderSize, result.Quantity);

            var initialPortfolioValue = algorithm.Portfolio.TotalPortfolioValue;
            var initialMarginUsed = algorithm.Portfolio.TotalMarginUsed;
            option.Holdings.SetHoldings(4.74m, result.Quantity + startingHoldings);

            if (option.Holdings.Invested)
            {
                Assert.LessOrEqual(Math.Abs(initialMarginUsed - algorithm.Portfolio.TotalMarginUsed), initialPortfolioValue * Math.Abs(targetPercentage));
            }
        }

        [TestCase(0)]
        [TestCase(-10)]
        public void GetsMaintenanceMarginForAPotentialShortPositionWithoutInitialHoldings(decimal initialHoldings)
        {
            // Computing the maintenance margin for a potential position is useful because it will be used to check whether there is
            // enough available buying power to open said new position.

            const decimal price = 1.6m;
            const decimal underlyingPrice = 410m;
            var tz = TimeZones.NewYork;

            var equity = new QuantConnect.Securities.Equity.Equity(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), Symbols.SPY, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            equity.SetMarketPrice(new Tick { Value = underlyingPrice });

            var optionCall = new Option(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbol.CreateOption("SPY", Market.USA, OptionStyle.American, OptionRight.Call, 408m, new DateTime(2023, 04, 03)),
                    Resolution.Minute,
                    tz,
                    tz,
                    true,
                    false,
                    false
                ),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties("", Currencies.USD, 100, 0.01m, 1),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            optionCall.SetMarketPrice(new Tick { Value = price });
            optionCall.Underlying = equity;
            optionCall.Holdings.SetHoldings(price, initialHoldings);

            var buyingPowerModel = new OptionMarginModel();

            if (initialHoldings == 0)
            {
                // No holdings for the option, so no maintenance margin expected
                Assert.AreEqual(0m, buyingPowerModel.GetMaintenanceMargin(optionCall));
            }
            else
            {
                // Margin = 10 * 100 * (1.6 + 0.2 * 410) = 83600
                Assert.AreEqual(83600m, buyingPowerModel.GetMaintenanceMargin(optionCall));
            }

            // Short option positions are very expensive in terms of margin.
            // Margin = 2 * 100 * (1.6 + 0.2 * 410) = 16720
            Assert.AreEqual(16720m, buyingPowerModel.GetMaintenanceMargin(MaintenanceMarginParameters.ForQuantityAtCurrentPrice(optionCall, -2)).Value);
        }

        private static void UpdatePrice(Security security, decimal close)
        {
            security.SetMarketPrice(new TradeBar
            {
                Time = DateTime.Now,
                Symbol = security.Symbol,
                Open = close,
                High = close,
                Low = close,
                Close = close
            });
        }
    }
}
