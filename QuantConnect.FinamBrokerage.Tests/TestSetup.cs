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
using System.IO;
using System.Collections;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Finam.Tests
{
    /// <summary>
    /// One-time test bootstrap: loads <c>config.json</c> (so <c>data-folder</c> and the default
    /// map-file provider are available for equity <see cref="Symbol"/> creation) and overlays any
    /// <c>QC_*</c> environment variables. Mirrors the standard QC brokerage test setup.
    /// </summary>
    [SetUpFixture]
    public class TestSetup
    {
        [OneTimeSetUp]
        public void GlobalSetup()
        {
            Log.LogHandler = new CompositeLogHandler();
            ReloadConfiguration();

            // SecurityIdentifier resolves equities via Composer.GetPart<IMapFileProvider>(); in a bare
            // test process nothing registers one (live trading does), so Symbol.Create(Equity) NREs.
            // Register a local provider explicitly.
            var mapFileProvider = new LocalDiskMapFileProvider();
            mapFileProvider.Initialize(new DefaultDataProvider());
            Composer.Instance.AddPart<IMapFileProvider>(mapFileProvider);
        }

        public static void ReloadConfiguration()
        {
            // NUnit may set the working directory to a temp folder; point it at the test bin output.
            var dir = TestContext.CurrentContext.TestDirectory;
            Environment.CurrentDirectory = dir;
            Directory.SetCurrentDirectory(dir);

            Config.Reset();

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var envKey = entry.Key.ToString();
                if (envKey != null && envKey.StartsWith("QC_"))
                {
                    var key = envKey.Substring(3).Replace("_", "-").ToLowerInvariant();
                    Config.Set(key, entry.Value?.ToString());
                }
            }

            Globals.Reset();
        }
    }
}
