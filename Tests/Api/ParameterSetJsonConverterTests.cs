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
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Optimizer.Parameters;

namespace QuantConnect.Tests.API
{
    [TestFixture]
    public class ParameterSetJsonConverterTests
    {
        private string _validSerialization = "{\"parameterSet\":{\"pinocho\":\"19\",\"pepe\":\"-1\"}}";

        [Test]
        public void SerializationNulls()
        {
            var parameterSet = new ParameterSet(0, null);

            var serialized = JsonConvert.SerializeObject(parameterSet);
            Assert.AreEqual("{}", serialized);
        }

        [Test]
        public void Serialization()
        {
            var parameterSet = new ParameterSet(18,
                new Dictionary<string, string>
                {
                    { "pinocho", "19" },
                    { "pepe", "-1" }
                });

            var serialized = JsonConvert.SerializeObject(parameterSet);

            Assert.AreEqual(_validSerialization, serialized);
        }

        [Test]
        public void Deserialization()
        {
            var deserialized = JsonConvert.DeserializeObject<ParameterSet>(_validSerialization);
            Assert.IsNotNull(deserialized);
            Assert.AreEqual(-1, deserialized.Id);
            Assert.IsTrue(deserialized.Value.Count == 2);
            Assert.IsTrue(deserialized.Value["pinocho"] == "19");
            Assert.IsTrue(deserialized.Value["pepe"] == "-1");
        }
    }
}
