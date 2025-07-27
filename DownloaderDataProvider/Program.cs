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

using NodaTime;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.DownloaderDataProvider.Launcher.Models;
using QuantConnect.DownloaderDataProvider.Launcher.Models.Constants;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Util;
using System.Globalization;
using System.Text.Json;
using DataFeeds = QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.DownloaderDataProvider.Launcher;
public static class Program
{
    /// <summary>
    /// Synchronizer in charge of guaranteeing a single operation per file path
    /// </summary>
    private readonly static KeyStringSynchronizer DiskSynchronizer = new();

    /// <summary>
    /// The provider used to cache history data files
    /// </summary>
    private static readonly IDataCacheProvider _dataCacheProvider = new DiskDataCacheProvider(DiskSynchronizer);

    /// <summary>
    /// Represents the time interval of 5 seconds.
    /// </summary>
    private static TimeSpan _logDisplayInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Provides access to exchange hours and raw data times zones in various markets
    /// </summary>
    private static readonly MarketHoursDatabase _marketHoursDatabase = MarketHoursDatabase.FromDataFolder();

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    public static void Main_OLD(string[] args)
    {
        // Parse report arguments and merge with config to use in the optimizer
        if (args.Length > 0)
        {
            Config.MergeCommandLineArgumentsWithConfiguration(DownloaderDataProviderArgumentParser.ParseArguments(args));
        }

        InitializeConfigurations();

        var dataDownloader = Composer.Instance.GetExportedValueByTypeName<IDataDownloader>(Config.Get(DownloaderCommandArguments.CommandDownloaderDataDownloader));
        var commandDataType = Config.Get(DownloaderCommandArguments.CommandDataType).ToUpperInvariant();

        switch (commandDataType)
        {
            case "UNIVERSE":
                RunUniverseDownloader(dataDownloader, new DataUniverseDownloadConfig());
                break;
            case "TRADE":
            case "QUOTE":
            case "OPENINTEREST":
                RunDownload(dataDownloader, new DataDownloadConfig(), Globals.DataFolder, _dataCacheProvider);
                break;
            default:
                Log.Error($"QuantConnect.DownloaderDataProvider.Launcher: Unsupported command data type '{commandDataType}'. Valid options: UNIVERSE, TRADE, QUOTE, OPENINTEREST.");
                break;
        }
    }

    // PAOLO EDIT
    private static readonly string _tickerFile = Path.Combine(Globals.DataFolder, "tickers.txt");
    private static readonly string _lastSuccessFilePath = Path.Combine(Globals.DataFolder, "last_success.json");
    private static readonly JsonSerializerOptions _jsonSerializationOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static List<string> _tickers = new List<string>();
    private static Dictionary<string, string> _tickersAndLastTime = new Dictionary<string, string>();

    private const string DATE_FORMAT = "yyyyMMdd-HH:mm:ss";
    private static void WriteDownloadProgressToFile()
    {        
        string jsonString = JsonSerializer.Serialize(_tickersAndLastTime, _jsonSerializationOptions);

        File.WriteAllText(_lastSuccessFilePath, jsonString);
    }

    private static void InitTickersAndLastTimeFromFile()
    {
        if (File.Exists(_lastSuccessFilePath))
        {
            string jsonString = File.ReadAllText(_lastSuccessFilePath);
            _tickersAndLastTime = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString) ?? new Dictionary<string, string>();
        }
    }

    private static void InitializeTickerFile()
    {
        if (!File.Exists(_tickerFile))
        {
            File.WriteAllText(_tickerFile, string.Empty);
        }
        _tickers = File.ReadAllLines(_tickerFile).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
    }

    public static void Main(string[] args)
    {
        // Parse report arguments and merge with config to use in the optimizer
        if (args.Length > 0)
        {
            Config.MergeCommandLineArgumentsWithConfiguration(DownloaderDataProviderArgumentParser.ParseArguments(args));
        }

        InitializeConfigurations();
        var dataDownloader = new BrokerageDataDownloader();

        InitializeTickerFile();
        InitTickersAndLastTimeFromFile();

        Resolution[] TIME_RESOLUTIONS = { Resolution.Minute, Resolution.Hour, Resolution.Daily };

        foreach (var ticker in _tickers)
        {
            if(!_tickersAndLastTime.TryGetValue(ticker, out string fromDateStr))
            {
                fromDateStr = new DateTime(DateTime.Now.Year - 2, 1, 1, 0, 0, 0).ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
            }

            DateTime fromDate = DateTime.ParseExact(fromDateStr, DATE_FORMAT, CultureInfo.InvariantCulture);
            DateTime toDate = DateTime.UtcNow;

            foreach(var resolution in TIME_RESOLUTIONS)
            {
                Log.Trace($"DownloaderDataProvider.Main(): Downloading {ticker} at {resolution} resolution from {fromDate} to {toDate}.");

                var symbolObject = Symbol.Create(ticker, SecurityType.Crypto, Market.Coinbase);

                DataDownloadConfig dataDownloadConfig = new DataDownloadConfig(TickType.Trade, SecurityType.Equity, resolution, fromDate, toDate, Market.Coinbase, new List<Symbol> { symbolObject });

                RunDownload(dataDownloader, dataDownloadConfig, Globals.DataFolder, _dataCacheProvider);
                
            }
            _tickersAndLastTime[ticker] = toDate.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);

            WriteDownloadProgressToFile();
        }

    }
    /// <summary>
    /// Executes a data download operation using the specified data downloader.
    /// </summary>
    /// <param name="dataDownloader">An instance of an object implementing the <see cref="IDataDownloader"/> interface, responsible for downloading data.</param>
    /// <param name="dataDownloadConfig">Configuration settings for the data download operation.</param>
    /// <param name="dataDirectory">The directory where the downloaded data will be stored.</param>
    /// <param name="dataCacheProvider">The provider used to cache history data files</param>
    /// <param name="mapSymbol">True if the symbol should be mapped while writing the data</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dataDownloader"/> is null.</exception>
    public static void RunDownload(IDataDownloader dataDownloader, DataDownloadConfig dataDownloadConfig, string dataDirectory, IDataCacheProvider dataCacheProvider, bool mapSymbol = true)
    {
        if (dataDownloader == null)
        {
            throw new ArgumentNullException(nameof(dataDownloader), "The data downloader instance cannot be null. Please ensure that a valid instance of data downloader is provided.");
        }

        var totalDownloadSymbols = dataDownloadConfig.Symbols.Count;
        var completeSymbolCount = 0;
        var startDownloadUtcTime = DateTime.UtcNow;

        foreach (var symbol in dataDownloadConfig.Symbols)
        {
            var downloadParameters = new DataDownloaderGetParameters(symbol, dataDownloadConfig.Resolution, dataDownloadConfig.StartDate, dataDownloadConfig.EndDate, dataDownloadConfig.TickType);

            Log.Trace($"DownloaderDataProvider.Main(): Starting download {downloadParameters}");
            var downloadedData = dataDownloader.Get(downloadParameters);

            if (downloadedData == null)
            {
                completeSymbolCount++;
                Log.Trace($"DownloaderDataProvider.Main(): No data available for the following parameters: {downloadParameters}");
                continue;
            }

            var (dataTimeZone, exchangeTimeZone) = GetDataAndExchangeTimeZoneBySymbol(symbol);

            var writer = new LeanDataWriter(dataDownloadConfig.Resolution, symbol, dataDirectory, dataDownloadConfig.TickType, dataCacheProvider, mapSymbol: mapSymbol);

            var groupedData = DataFeeds.DownloaderDataProvider.FilterAndGroupDownloadDataBySymbol(
                downloadedData,
                symbol,
                dataDownloadConfig.DataType,
                exchangeTimeZone,
                dataTimeZone,
                downloadParameters.StartUtc,
                downloadParameters.EndUtc);

            var lastLogStatusTime = DateTime.UtcNow;

            foreach (var data in groupedData)
            {
                writer.Write(data.Select(data =>
                {
                    var utcNow = DateTime.UtcNow;
                    if (utcNow - lastLogStatusTime >= _logDisplayInterval)
                    {
                        lastLogStatusTime = utcNow;
                        Log.Trace($"Downloading data for {downloadParameters.Symbol}. Please hold on...");
                    }
                    return data;
                }));
            }

            completeSymbolCount++;
            var symbolPercentComplete = (double)completeSymbolCount / totalDownloadSymbols * 100;
            Log.Trace($"DownloaderDataProvider.RunDownload(): {symbolPercentComplete:F2}% complete ({completeSymbolCount} out of {totalDownloadSymbols} symbols)");

            Log.Trace($"DownloaderDataProvider.RunDownload(): Download completed for {downloadParameters.Symbol} at {downloadParameters.Resolution} resolution, " +
                $"covering the period from {dataDownloadConfig.StartDate} to {dataDownloadConfig.EndDate}.");
        }
        Log.Trace($"All downloads completed in {(DateTime.UtcNow - startDownloadUtcTime).TotalSeconds:F2} seconds.");
    }

    /// <summary>
    /// Initiates the universe downloader using the provided configuration.
    /// </summary>
    /// <param name="dataDownloader">The data downloader instance.</param>
    /// <param name="dataUniverseDownloadConfig">The universe download configuration.</param>
    private static void RunUniverseDownloader(IDataDownloader dataDownloader, DataUniverseDownloadConfig dataUniverseDownloadConfig)
    {
        foreach (var symbol in dataUniverseDownloadConfig.Symbols)
        {
            var universeDownloadParameters = new DataUniverseDownloaderGetParameters(symbol, dataUniverseDownloadConfig.StartDate, dataUniverseDownloadConfig.EndDate);
            UniverseExtensions.RunUniverseDownloader(dataDownloader, universeDownloadParameters);
        }
    }

    /// <summary>
    /// Retrieves the data time zone and exchange time zone associated with the specified symbol.
    /// </summary>
    /// <param name="symbol">The symbol for which to retrieve time zones.</param>
    /// <returns>
    /// A tuple containing the data time zone and exchange time zone.
    /// The data time zone represents the time zone for data related to the symbol.
    /// The exchange time zone represents the time zone for trading activities related to the symbol.
    /// </returns>
    private static (DateTimeZone dataTimeZone, DateTimeZone exchangeTimeZone) GetDataAndExchangeTimeZoneBySymbol(Symbol symbol)
    {
        var entry = _marketHoursDatabase.GetEntry(symbol.ID.Market, symbol, symbol.SecurityType);
        return (entry.DataTimeZone, entry.ExchangeHours.TimeZone);
    }

    /// <summary>
    /// Initializes various configurations for the application.
    /// This method sets up logging, data providers, map file providers, and factor file providers.
    /// </summary>
    /// <remarks>
    /// The method reads configuration values to determine whether debugging is enabled,
    /// which log handler to use, and which data, map file, and factor file providers to initialize.
    /// </remarks>
    /// <seealso cref="Log"/>
    /// <seealso cref="Config"/>
    /// <seealso cref="Composer"/>
    /// <seealso cref="ILogHandler"/>
    /// <seealso cref="IDataProvider"/>
    /// <seealso cref="IMapFileProvider"/>
    /// <seealso cref="IFactorFileProvider"/>
    public static void InitializeConfigurations()
    {
        Log.DebuggingEnabled = Config.GetBool("debug-mode", false);
        Log.LogHandler = Composer.Instance.GetExportedValueByTypeName<ILogHandler>(Config.Get("log-handler", "CompositeLogHandler"));

        var dataProvider = Composer.Instance.GetExportedValueByTypeName<IDataProvider>("DefaultDataProvider");
        var mapFileProvider = Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>(Config.Get("map-file-provider", "LocalDiskMapFileProvider"));
        var factorFileProvider = Composer.Instance.GetExportedValueByTypeName<IFactorFileProvider>(Config.Get("factor-file-provider", "LocalDiskFactorFileProvider"));

        var optionChainProvider = Composer.Instance.GetPart<IOptionChainProvider>();
        if (optionChainProvider == null)
        {
            var historyManager = Composer.Instance.GetExportedValueByTypeName<HistoryProviderManager>(nameof(HistoryProviderManager));
            historyManager.Initialize(new HistoryProviderInitializeParameters(null, null, dataProvider, _dataCacheProvider,
                mapFileProvider, factorFileProvider, _ => { }, false, new DataPermissionManager(), null, new AlgorithmSettings()));
            var baseOptionChainProvider = new LiveOptionChainProvider();
            baseOptionChainProvider.Initialize(new(mapFileProvider, historyManager));
            optionChainProvider = new CachingOptionChainProvider(baseOptionChainProvider);
            Composer.Instance.AddPart(optionChainProvider);
        }

        mapFileProvider.Initialize(dataProvider);
        factorFileProvider.Initialize(mapFileProvider, dataProvider);
    }
}
