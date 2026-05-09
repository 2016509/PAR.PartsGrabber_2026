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

namespace PAR.ParseLib
{
    public class CachedParseServiceDecorator : IParseService
    {
        private readonly IParseService _inner;
        private readonly DatabasePartsService _databaseService;
        private readonly ILogger<CachedParseServiceDecorator> _logger;
        private readonly ModuleMetrics _metrics;
        private readonly CacheOptions _options;

        public CachedParseServiceDecorator(
            IParseService inner,
            DatabasePartsService databaseService,
            ILogger<CachedParseServiceDecorator> logger,
            ModuleMetrics metrics,
            IOptions<CacheOptions> options)
        {
            _inner = inner;
            _databaseService = databaseService;
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

            var results = new List<ParsingPart>();
            var sourcesToParseFromWeb = new List<(CheckProxyResult SourceProxy, PartSource PartSource, int Index)>();

            for (int i = 0; i < sourceProxies.Count; i++)
            {
                var sourceProxy = sourceProxies[i];
                var useDatabase = _options.ShouldCacheSource(sourceProxy.PartSource.SourceName);

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
                        // Нет данных в БД - нужно спарсить из веба
                        _logger.LogInformation("No data in grabber_parts for {PartNumber} from {Source}, will parse from web",
                            partNumber, sourceProxy.PartSource.SourceName);
                        sourcesToParseFromWeb.Add((sourceProxy, sourceProxy.PartSource, i));
                        results.Add(null!);
                        _metrics.ReportCacheMiss(sourceProxy.PartSource.SourceName);
                    }
                }
                else
                {
                    // Не в белом списке - всегда парсим из веба
                    sourcesToParseFromWeb.Add((sourceProxy, sourceProxy.PartSource, i));
                    results.Add(null!);
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