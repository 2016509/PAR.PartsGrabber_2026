using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;

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

        public ProcessService(
            ProcessParsingResultService processParsingResultService,
            IOptions<ModuleOptions> options,
            IOptions<ApiServiceOptions> apiServiceOptions,
            IApiService apiService,
            ILogger logger,
            ParseService parseService)
        {
            _processParsingResultService = processParsingResultService;
            _options = options.Value;
            _apiServiceOptions = apiServiceOptions.Value;
            _apiService = apiService;
            _logger = logger;
            _parseService = parseService;
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

                try
                {
                    var parsingResults = await _parseService.Parse(part.MainPartNumber, sourceProxies);

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
                                    error_message = $"Site {parsingResult.PartSource.SourceName} not responding",
                                    script_name = "ParsGrabber"
                                }));
                        }
                        else
                        {
                            tasks.Add(_processParsingResultService.Save(parsingResult, part));
                        }
                    }

                    await Task.WhenAll(tasks);

                    var partSources = sourceProxies.Select(x => x.PartSource).ToList();
                    await _processParsingResultService.UpdatePartsAndReplace(parsingResults, part, partSources);
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