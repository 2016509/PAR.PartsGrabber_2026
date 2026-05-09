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

namespace PAR.PartsGrabber
{
    public class DatabasePartsService
    {
        private readonly IApiService _apiService;
        private readonly ILogger<DatabasePartsService> _logger;
        private readonly ModuleMetrics _metrics;
        private readonly ApiServiceOptions _options;

        public DatabasePartsService(
            IApiService apiService,
            IOptions<ApiServiceOptions> options,
            ILogger<DatabasePartsService> logger,
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
                var url = $"{_options.BaseUrl}{_options.GetCachedPartData}?partNumber={partNumber}&sourceSite={targetSource.SourceName}";
                var dbData = await _apiService.Get<CachedPartData>(url, ct);

                if (dbData == null)
                {
                    _logger.LogDebug("No data in grabber_parts for {PartNumber} from {Source}",
                        partNumber, targetSource.SourceName);
                    return null;
                }
                var replace  = dbData.FirstOrDefault();
                
                   
                // Создаем ParsingPart ИЗ БД (как если бы мы спарсили)
                var parsingPart = new ParsingPart
                {
                    PartSource = targetSource,
                    Name = replace.Name,
                    Replaces = replace.Replaces ?? new List<string>(),
                    ParsingPictures = replace.Pictures?.Select(p => new ParsingPicture
                    {
                        Url = p.Url,
                        LocalPath = p.LocalPath
                    }).ToList() ?? new List<ParsingPicture>(),
                    SitePartNumber = replace.SitePartNumber,
                    RegularPrice = replace.RegularPrice,
                    AttempsCount = replace.AttempsCount,
                    WithErrorToSave = false,
                    UsedProxy = null,
                    UsedPlaywright = false
                };

                    _logger.LogInformation("Retrieved data from grabber_parts for {PartNumber} from {Source}. Found {ReplacesCount} replaces",
                        partNumber, targetSource.SourceName, parsingPart.Replaces.Count);
                    return parsingPart;
                
               
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