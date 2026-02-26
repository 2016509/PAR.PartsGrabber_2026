using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace PAR.PartsGrabber
{
    public class ProcessService
    {
        private readonly ProcessParsingResultService _processParsingResultService;
        private readonly ModuleOptions _options;
        private readonly ApiServiceOptions _apiServiceOptions;
        private readonly IApiService _apiService;
        private readonly ILogger _logger;
        private readonly ParseService _parseService;
        private readonly ITelegramNotificationService _telegramService;

        public ProcessService(
            ProcessParsingResultService processParsingResultService,
            IOptions<ModuleOptions> options,
            IOptions<ApiServiceOptions> apiServiceOptions,
            IApiService apiService,
            ILogger logger,
            ParseService parseService,
            ITelegramNotificationService telegramService)
        {
            _processParsingResultService = processParsingResultService;
            _options = options.Value;
            _apiServiceOptions = apiServiceOptions.Value;
            _apiService = apiService;
            _logger = logger;
            _parseService = parseService;
            _telegramService = telegramService;
        }

        /// <summary>
        /// Returns next scheduled run time (UTC).
        /// </summary>
        public async Task<DateTime> Process(DateTime nextRunUtc, List<CheckProxyResult> sourceProxies)
        {
            if (DateTime.UtcNow <= nextRunUtc)
                return nextRunUtc;

            var partsFromAPI = await _apiService.Get<PartsAndReplace>(_apiServiceOptions.BaseUrl + _apiServiceOptions.GetPartsWithStateUrl);

            foreach (var part in partsFromAPI)
            {
                if (string.IsNullOrEmpty(part.MainPartNumber))
                {
                    _logger.LogInformation("The part with id: {Id} is missing a part number.", part.Id);
                    continue;
                }

                _logger.LogInformation("START PROCESSING WITH: {Part}", part.MainPartNumber);
                var processStartTime = DateTime.UtcNow;

                try
                {
                    // Жёсткий таймаут 1 минута
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    var parsingResults = await _parseService.Parse(part.MainPartNumber, sourceProxies, cts.Token);

                    // Проверяем таймаут ПОСЛЕ await (partial данные готовы)
                    bool isTimeout = cts.Token.IsCancellationRequested;
                    if (isTimeout)
                    {
                        _logger.LogWarning("Timeout after 1 min for part {Part}. Saving partial results.", part.MainPartNumber);
                        await _telegramService.SendTimeoutNotificationAsync("Timeout after 1 min for part "+ part.MainPartNumber +". Saving partial results.", processStartTime);

                        // Логика для bool Status + WithErrorToSave
                        foreach (var result in parsingResults)
                        {
                            result.AttempsCount++;  // увеличиваем попытки

                            // Проверяем наличие данных (partial success)
                            bool hasData = !string.IsNullOrEmpty(result.Name) ||
                                         result.Replaces.Any() ||
                                         result.ParsingPictures.Any() ||
                                         !string.IsNullOrEmpty(result.SitePartNumber) ||
                                         result.RegularPrice > 0;

                            if (!hasData)
                            {
                                result.WithErrorToSave = true;  // empty → error (Status=false)
                            }
                            // Иначе: WithErrorToSave=false → success (Status=true), partial данные сохранятся
                        }
                    }

                    var tasks = new List<Task>();

                    foreach (var parsingResult in parsingResults)
                    {
                        if (parsingResult.WithErrorToSave)
                        {
                            parsingResult.PartSource.Status = false;
                            var url = $"{_apiServiceOptions.BaseUrl}{_apiServiceOptions.UpdatePartSourceUrl}/{parsingResult.PartSource.Id}";
                            tasks.Add(_apiService.Put(url, parsingResult.PartSource));

                            tasks.Add(_apiService.Post(
                                _apiServiceOptions.BaseUrl + _apiServiceOptions.SaveErrorUrl,
                                new ErrorLog
                                {
                                    error_message = $"Site {parsingResult.PartSource.SourceName} not responding{(isTimeout ? " (timeout)" : "")}",
                                    script_name = "ParsGrabber"
                                }));
                        }
                        else
                        {
                            tasks.Add(_processParsingResultService.Save(parsingResult, part, cts.Token));
                        }
                    }

                    await Task.WhenAll(tasks);

                    var partSources = sourceProxies.Select(x => x.PartSource).ToList();
                    await _processParsingResultService.UpdatePartsAndReplace(parsingResults, part, partSources, cts.Token);
                }
                catch (OperationCanceledException ex)
                {
                    if (ex.CancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Parse cancelled by timeout for {Part}", part.MainPartNumber);
                        // Partial данные уже обработаны в if(isTimeout) или exception в парсерах
                    }
                    else
                    {
                        throw;  // другие отмены пробрасываем
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ERROR PROCESSING {Part}", part.MainPartNumber);
                }

                _logger.LogInformation("END PROCESSING WITH: {Part}", part.MainPartNumber);
            }

            return DateTime.UtcNow.AddSeconds(Convert.ToDouble(_options.Interval));
        }
    }
}
