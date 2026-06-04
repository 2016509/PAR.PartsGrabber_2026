using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;
using PAR.PartsGrabber.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PAR.PartsGrabber
{
    public class DatabasePartsService
    {
        private readonly IApiService _apiService;
        private readonly ILogger _logger;
        private readonly ModuleMetrics _metrics;
        private readonly ApiServiceOptions _options;

        public DatabasePartsService(
            IApiService apiService,
            IOptions<ApiServiceOptions> options,
            ILogger logger,
            ModuleMetrics metrics)
        {
            _apiService = apiService;
            _logger = logger;
            _metrics = metrics;
            _options = options.Value;
        }

        /// <summary>
        /// Получить данные детали из БД для конкретного источника
        /// </summary>
        public async Task<ParsingPart?> GetPartDataFromDatabaseAsync(
           string partNumber,
           PartSource targetSource,
           CancellationToken ct = default)
        {
            try
            {
                // Запрашиваем данные из Web API
                var url = $"{_options.BaseUrl}{_options.GetCachedPartData}?partNumber={Uri.EscapeDataString(partNumber)}&sourceSite={Uri.EscapeDataString(targetSource.SourceName)}";
                var dbData = await _apiService.GetSingle<CachedPartData>(url, ct);

                if (dbData == null)
                {
                    _logger.LogDebug("No data in grabber_parts for {PartNumber} from {Source}",
                        partNumber, targetSource.SourceName);
                    return null;
                }
                
                   
                // Создаем ParsingPart ИЗ БД (как если бы мы спарсили)
                var parsingPart = new ParsingPart
                {
                    PartSource = targetSource,
                    Name = dbData.Name,
                    Replaces = dbData.Replaces ?? new List<string>(),
                    ParsingPictures = dbData.Pictures?.Select(p => new ParsingPicture
                    {
                        Url = p.Url,
                        LocalPath = p.LocalPath
                    }).ToList() ?? new List<ParsingPicture>(),
                    SitePartNumber = dbData.SitePartNumber,
                    RegularPrice = dbData.RegularPrice,
                    AttempsCount = dbData.AttempsCount,
                    WithErrorToSave = false,
                    UsedProxy = null,
                    UsedPlaywright = false
                };

                    _logger.LogInformation("Retrieved data from grabber_parts for {PartNumber} from {Source}. Found {ReplacesCount} replaces",
                        partNumber, targetSource.SourceName, parsingPart.Replaces.Count);
                    return parsingPart;
                
               
            }
            catch (EntityNotFoundException)
            {
                _logger.LogDebug(
                    "No data in grabber_parts for {PartNumber} from {Source}",
                    partNumber,
                    targetSource.SourceName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get data from grabber_parts for {PartNumber} from {Source}",
                    partNumber, targetSource.SourceName);
                return null;
            }
        }
    }

}
