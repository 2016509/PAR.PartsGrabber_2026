using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.PartsGrabber;
using PAR.PartsGrabber.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PAR.ParseLib
{
    public class CachedParseServiceDecorator : IParseService
    {
        private readonly IParseService _inner;
        private readonly DatabasePartsService _databaseService;
        private readonly InternalReplacementLookupService _internalReplacementLookupService;
        private readonly ILogger _logger;
        private readonly ModuleMetrics _metrics;
        private readonly CacheOptions _options;

        public CachedParseServiceDecorator(
            IParseService inner,
            DatabasePartsService databaseService,
            InternalReplacementLookupService internalReplacementLookupService,
            ILogger logger,
            ModuleMetrics metrics,
            IOptions<CacheOptions> options)
        {
            _inner = inner;
            _databaseService = databaseService;
            _internalReplacementLookupService = internalReplacementLookupService;
            _logger = logger;
            _metrics = metrics;
            _options = options.Value;
        }

        public async Task<List<ParsingPart>> Parse(
            string partNumber,
            List<CheckProxyResult> sourceProxies,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var internalLookup = await _internalReplacementLookupService.LookupAsync(
                partNumber,
                sourceProxies,
                cancellationToken);

            var results = new List<ParsingPart>(internalLookup.ParsingParts);
            var sourcesToParseFromWeb = new List<(CheckProxyResult SourceProxy, PartSource PartSource, int Index)>();
            var sourceProxiesForRegularParsing = internalLookup.Found
                ? sourceProxies
                    .Where(x => !_internalReplacementLookupService.IsInternalReplacementSource(x.PartSource.SourceName))
                    .ToList()
                : sourceProxies;

            if (internalLookup.Found)
            {
                _metrics.ReportOnlineReplacementFallback(false);

                _logger.LogInformation(
                    "Internal replacements found for {PartNumber}. ReplacesCount={ReplacesCount}. Sources={Sources}. OnlineReplacementFallback=False",
                    partNumber,
                    internalLookup.Replaces.Count,
                    string.Join(", ", internalLookup.Sources));
            }
            else
            {
                _metrics.ReportOnlineReplacementFallback(true);

                _logger.LogInformation(
                    "Internal replacements not found for {PartNumber}. OnlineReplacementFallback=True",
                    partNumber);
            }

            for (int i = 0; i < sourceProxiesForRegularParsing.Count; i++)
            {
                var sourceProxy = sourceProxiesForRegularParsing[i];
                var isInternalReplacementSource = _internalReplacementLookupService
                    .IsInternalReplacementSource(sourceProxy.PartSource.SourceName);
                var canParseFromWeb = sourceProxy.PartSource.Status && sourceProxy.Proxies.Count > 0;
                var useDatabase = _options.ShouldCacheSource(sourceProxy.PartSource.SourceName)
                    && !(internalLookup.FallbackRequired && isInternalReplacementSource);

                if (useDatabase)
                {
                    // Пытаемся получить данные из grabber_parts
                    var dbPart = await _databaseService.GetPartDataFromDatabaseAsync(
                        partNumber,
                        sourceProxy.PartSource,
                        cancellationToken);

                    if (dbPart != null)
                    {
                        // Данные из БД успешно получены
                        results.Add(dbPart);
                        _metrics.ReportCacheHit(sourceProxy.PartSource.SourceName);
                        _logger.LogInformation("Using data from grabber_parts for {PartNumber} from {Source}",
                            partNumber, sourceProxy.PartSource.SourceName);
                    }
                    else
                    {
                        if (!canParseFromWeb)
                        {
                            _logger.LogInformation(
                                "No data in grabber_parts for {PartNumber} from {Source}, but web parsing is skipped because source is inactive or has no active proxies. SourceStatus={SourceStatus}, ProxyCount={ProxyCount}",
                                partNumber,
                                sourceProxy.PartSource.SourceName,
                                sourceProxy.PartSource.Status,
                                sourceProxy.Proxies.Count);
                            _metrics.ReportCacheMiss(sourceProxy.PartSource.SourceName);
                            continue;
                        }

                        // Нет данных в БД - нужно спарсить из веба
                        _logger.LogInformation("No data in grabber_parts for {PartNumber} from {Source}, will parse from web",
                            partNumber, sourceProxy.PartSource.SourceName);
                        results.Add(null!);
                        sourcesToParseFromWeb.Add((sourceProxy, sourceProxy.PartSource, results.Count - 1));
                        _metrics.ReportCacheMiss(sourceProxy.PartSource.SourceName);
                    }
                }
                else
                {
                    if (!canParseFromWeb)
                    {
                        _logger.LogInformation(
                            "Web parsing skipped for {PartNumber} from {Source}. SourceStatus={SourceStatus}, ProxyCount={ProxyCount}",
                            partNumber,
                            sourceProxy.PartSource.SourceName,
                            sourceProxy.PartSource.Status,
                            sourceProxy.Proxies.Count);
                        continue;
                    }

                    if (isInternalReplacementSource && internalLookup.FallbackRequired)
                    {
                        _logger.LogInformation(
                            "Internal replacements fallback enabled for {PartNumber} from {Source}, will parse from web",
                            partNumber,
                            sourceProxy.PartSource.SourceName);
                    }

                    // Не в белом списке - всегда парсим из веба
                    results.Add(null!);
                    sourcesToParseFromWeb.Add((sourceProxy, sourceProxy.PartSource, results.Count - 1));
                }
            }

            // Парсим из веба то, что не нашли в БД или что не настроено на БД
            if (sourcesToParseFromWeb.Any())
            {
                _logger.LogInformation("Parsing {Count} web sources for {PartNumber}: {Sources}",
                    sourcesToParseFromWeb.Count, partNumber,
                    string.Join(", ", sourcesToParseFromWeb.Select(x => x.PartSource.SourceName)));

                var missingSourceProxies = sourcesToParseFromWeb.Select(x => x.SourceProxy).ToList();
                var parsedResults = await _inner.Parse(partNumber, missingSourceProxies, cancellationToken);

                int parsedIndex = 0;
                for (int i = 0; i < sourcesToParseFromWeb.Count; i++)
                {
                    var parsed = parsedResults[parsedIndex++];
                    var originalIndex = sourcesToParseFromWeb[i].Index;
                    results[originalIndex] = parsed;
                }
            }

            return results.Where(r => r != null).ToList();
        }
    }
}
