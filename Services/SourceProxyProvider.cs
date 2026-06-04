using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;

namespace PAR.PartsGrabber
{
    public class SourceProxyProvider
    {
        private readonly IApiService _apiService;
        private readonly ApiServiceOptions _apiServiceOptions;
        private readonly ModuleOptions _moduleOptions;
        private readonly SiteProxyCheckerService _siteProxyChecker;
        private readonly ModuleMetrics _moduleMetrics;
        private readonly ILogger _logger;

        public SourceProxyProvider(
            IApiService apiService,
            IOptions<ApiServiceOptions> apiServiceOptions,
            IOptions<ModuleOptions> moduleOptions,
            SiteProxyCheckerService siteProxyChecker,
            ModuleMetrics moduleMetrics,
            ILogger logger)
        {
            _apiService = apiService;
            _apiServiceOptions = apiServiceOptions.Value;
            _moduleOptions = moduleOptions.Value;
            _siteProxyChecker = siteProxyChecker;
            _moduleMetrics = moduleMetrics;
            _logger = logger;
        }

        public async Task<List<CheckProxyResult>> GetActiveSourceProxiesAsync(CancellationToken cancellationToken = default)
        {
            var proxies = await _apiService.Get<Proxy>(_apiServiceOptions.BaseUrl + _apiServiceOptions.GetProxiesUrl);
            var activeProxies = proxies
                .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.IP) && x.Port is > 0)
                .ToList();

            var partSources = await _apiService.Get<PartSource>(_apiServiceOptions.BaseUrl + _apiServiceOptions.GetPartsSourcesUrl);
            var activePartSources = partSources
                .Where(x => x.Status)
                .ToList();

            _moduleMetrics.ReportApiAvailable();
            _moduleMetrics.UpdateActiveProxiesCount(activeProxies.Count);
            _moduleMetrics.UpdateActiveSourcesCount(activePartSources.Count);

            if (_moduleOptions.UseExternalSourceHealthChecker)
            {
                var sourceProxies = partSources
                    .Select(source => new CheckProxyResult
                    {
                        PartSource = source,
                        Proxies = source.Status ? activeProxies.ToList() : new List<Proxy>()
                    })
                    .ToList();

                var webBindings = sourceProxies.Sum(x => x.Proxies.Count);
                var dbOnlySources = sourceProxies.Count(x => !x.PartSource.Status);
                _moduleMetrics.UpdateSourceProxyBindingsCount(webBindings);

                _logger.LogInformation(
                    "Loaded source/proxy state from API. ExternalHealthChecker=True, TotalSources={TotalSources}, ActiveSources={ActiveSources}, DbOnlySources={DbOnlySources}, ActiveProxies={ActiveProxies}, WebBindings={WebBindings}",
                    partSources.Count,
                    activePartSources.Count,
                    dbOnlySources,
                    activeProxies.Count,
                    webBindings);

                return sourceProxies;
            }

            _logger.LogWarning(
                "Legacy source proxy check is enabled. ExternalHealthChecker=False, ActiveSources={ActiveSources}, ActiveProxies={ActiveProxies}",
                activePartSources.Count,
                activeProxies.Count);

            var checkedSourceProxies = await _siteProxyChecker.CheckProxies(activeProxies, activePartSources);
            var usableSourceProxies = checkedSourceProxies
                .Where(x => x.Proxies.Count > 0)
                .ToList();

            _moduleMetrics.UpdateSourceProxyBindingsCount(usableSourceProxies.Sum(x => x.Proxies.Count));

            return usableSourceProxies;
        }
    }
}
