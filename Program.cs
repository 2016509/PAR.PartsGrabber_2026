using KameraData.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;
using Serilog;
using System.Net;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PAR.PartsGrabber
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var services = Initialize();
            await using var serviceProvider = services.BuildServiceProvider();

            var apiService = serviceProvider.GetRequiredService<IApiService>();
            var processService = serviceProvider.GetRequiredService<ProcessService>();
            var logger = serviceProvider.GetRequiredService<ILogger>();
            var siteProxyChecker = serviceProvider.GetRequiredService<SiteProxyCheckerService>();

            // Start Chromium once
            var pw = serviceProvider.GetRequiredService<PlaywrightFetcher>();
            await pw.EnsureStartedAsync();

            var apiServiceOptions = serviceProvider.GetRequiredService<IOptions<ApiServiceOptions>>();
            var moduleOptions = serviceProvider.GetRequiredService<IOptions<ModuleOptions>>();

            var activeSourceProxies = new List<CheckProxyResult>();

            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 100;

            try
            {
                var proxies = await apiService.Get<Proxy>(apiServiceOptions.Value.BaseUrl + apiServiceOptions.Value.GetProxiesUrl);
                var activeProxies = proxies.Where(x => x.IsActive).ToList();

                var partsSources = await apiService.Get<PartSource>(apiServiceOptions.Value.BaseUrl + apiServiceOptions.Value.GetPartsSourcesUrl);
                var activePartsSources = partsSources.Where(x => x.Status).ToList();

                var sourceProxies = await siteProxyChecker.CheckProxies(activeProxies, activePartsSources);

                foreach (var sourceProxy in sourceProxies)
                {
                    if (sourceProxy.Proxies.Count == 0)
                    {
                        sourceProxy.PartSource.Status = false;

                        var putUrl = apiServiceOptions.Value.BaseUrl + apiServiceOptions.Value.UpdatePartSourceUrl + $"/{sourceProxy.PartSource.Id}";
                        await apiService.Put(putUrl, sourceProxy.PartSource);

                        await apiService.Post(
                            apiServiceOptions.Value.BaseUrl + apiServiceOptions.Value.SaveErrorUrl,
                            new ErrorLog
                            {
                                error_message = $"Couldn't select a suitable proxy for the site {sourceProxy.PartSource.SourceName}",
                                script_name = "ParsGrabber"
                            });
                    }
                    else
                    {
                        activeSourceProxies.Add(sourceProxy);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected exception in Main");
                Environment.Exit(1);
            }

            logger.LogInformation("Press ESC to stop");

            var nextRunUtc = DateTime.UtcNow.AddSeconds(Convert.ToDouble(moduleOptions.Value.Interval));

            while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape))
            {
                try
                {
                    nextRunUtc = await processService.Process(nextRunUtc, activeSourceProxies);
                }
                catch (EntityNotFoundException)
                {
                    // No work found; schedule next run
                    nextRunUtc = DateTime.UtcNow.AddSeconds(Convert.ToDouble(moduleOptions.Value.Interval));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Fatal error in main loop");
                    Environment.Exit(1);
                }

                await Task.Delay(200);
            }
        }

        private static ServiceCollection Initialize()
        {
            var services = new ServiceCollection();
            var configuration = ConfigureOptions(services);
            ConfigureLogger(services, configuration);
            ConfigureHttpClients(services);
            ConfigureServices(services);

            return services;
        }

        private static void ConfigureHttpClients(ServiceCollection services)
        {
            services.AddHttpClient<ApiService>();
            services.AddHttpClient<ProcessParsingResultService>();
        }

        private static void ConfigureServices(ServiceCollection services)
        {
            services.AddTransient<IApiService, ApiService>();

            // One Playwright per process
            services.AddSingleton<PlaywrightFetcher>();

            // Singleton is OK: internal state is in-memory (leases/gates) and service holds pooled clients.
            services.AddSingleton<SiteProxyCheckerService>();

            services.AddTransient<IParsersFactory, ParsersFactory>();
            services.AddTransient<ProcessParsingResultService>();
            services.AddTransient<ParseService>();
            services.AddTransient<ProcessService>();

            services.AddTransient<IParser, XPartSupplyParser>();
            services.AddTransient<IParser, PartsDrParser>();
            services.AddTransient<IParser, AppliancePartsHQParser>();
            services.AddTransient<IParser, MajorAppliancePartsParser>();
            services.AddTransient<IParser, PartSelectParser>();
            services.AddTransient<IParser, EbayParser>();
            services.AddTransient<IParser, AmazonCOMParser>();
            services.AddTransient<IParser, AmazonCAParser>();
            services.AddTransient<IParser, SearsPartsDirectParser>();
        }

        private static IConfigurationRoot ConfigureOptions(IServiceCollection services)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

            var configuration = builder.Build();

            services.AddOptions();

            services.AddOptions<ApiServiceOptions>().Bind(configuration.GetSection(ApiServiceOptions.SectionName));
            services.AddOptions<SitesToParseOptions>().Bind(configuration);
            services.AddOptions<SitesToCheckProxyOptions>().Bind(configuration);
            services.AddOptions<ModuleOptions>().Bind(configuration.GetSection(ModuleOptions.SectionName));

            return configuration;
        }

        private static void ConfigureLogger(ServiceCollection services, IConfiguration configuration)
        {
            var loggerConfiguration = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            var factory = new LoggerFactory();
            factory.AddSerilog(loggerConfiguration);

            var logger = factory.CreateLogger("PAR.PartsGrabber");
            services.AddSingleton(logger);
        }
    }
}