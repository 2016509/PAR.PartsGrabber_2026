using KameraData.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;
using PAR.ParseLib.Parsers;
using PAR.ParseLib.Selenium;
using PAR.PartsGrabber.Options;
using Prometheus;
using Serilog;
using System.Net;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PAR.PartsGrabber
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            
            var services = Initialize();
            await using var serviceProvider = services.BuildServiceProvider();
             // KestrelMetricServer
            using var metricServer = new KestrelMetricServer(
                hostname: "0.0.0.0", port: 9105, url: "/metrics"); // urlPath, не url!
            metricServer.Start();

            Console.WriteLine("🚀 PartsGrabber started!");
            Console.WriteLine("📊 Metrics: http://localhost:9105/metrics");
            var moduleMetrics = serviceProvider.GetRequiredService<ModuleMetrics>();
            moduleMetrics.StartHealthCheck();

            var processService = serviceProvider.GetRequiredService<ProcessService>();
            var logger = serviceProvider.GetRequiredService<ILogger>();

            // Start Chromium once
            var pw = serviceProvider.GetRequiredService<PlaywrightFetcher>();
            await pw.EnsureStartedAsync();

            var moduleOptions = serviceProvider.GetRequiredService<IOptions<ModuleOptions>>();

            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 100;

            logger.LogInformation("Press ESC, Ctrl+C, or send SIGTERM to stop");

            var nextRunUtc = DateTime.UtcNow.AddSeconds(Convert.ToDouble(moduleOptions.Value.Interval));

            while (!IsStopRequested(cts.Token))
            {
                try
                {
                    nextRunUtc = await processService.Process(nextRunUtc);
                }
                catch (EntityNotFoundException)
                {
                    nextRunUtc = DateTime.UtcNow.AddSeconds(Convert.ToDouble(moduleOptions.Value.Interval));
                }
                catch (Exception ex)
                {
                    moduleMetrics.ReportApiUnavailable();
                    moduleMetrics.ReportError("main_loop_error");
                    logger.LogError(
                        ex,
                        "Error in main loop. Module stays alive and will retry after {RetryInSeconds} seconds",
                        moduleOptions.Value.ApiRetryIntervalSeconds);
                    nextRunUtc = DateTime.UtcNow.AddSeconds(moduleOptions.Value.ApiRetryIntervalSeconds);
                }
                await Task.Delay(200);
            }
        }

        private static bool IsStopRequested(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return true;

            if (Console.IsInputRedirected)
                return false;

            return Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape;
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

            services.AddHttpClient<ITelegramNotificationService, TelegramNotificationService>();
        }

        private static void ConfigureServices(ServiceCollection services)
        {
            services.AddSingleton<DatabasePartsService>();  
            services.AddSingleton<InternalReplacementLookupService>();

            services.AddTransient<IApiService, ApiService>();
            services.AddSingleton<ModuleMetrics>();
            services.AddSingleton<PlaywrightFetcher>();
            services.AddSingleton<SiteProxyCheckerService>();
            services.AddSingleton<ProxiedHttpClientPool>();
            services.AddSingleton<SourceProxyProvider>();

            services.AddTransient<IParsersFactory, ParsersFactory>();
            services.AddTransient<ProcessParsingResultService>();
            services.AddTransient<ParseService>();
           
            // Регистрируем декоратор
            services.AddTransient<IParseService>(sp =>
             new CachedParseServiceDecorator(
                 sp.GetRequiredService<ParseService>(),
                 sp.GetRequiredService<DatabasePartsService>(),
                 sp.GetRequiredService<InternalReplacementLookupService>(),
                 sp.GetRequiredService<ILogger>(),
                 sp.GetRequiredService<ModuleMetrics>(),
                 sp.GetRequiredService<IOptions<CacheOptions>>()  // ← Добавить эту строку
             ));

            services.AddTransient<ProcessService>();

            // Регистрация парсеров
            var parserTypes = new[]
            {
        typeof(XPartSupplyParser), typeof(PartsDrParser), typeof(AppliancePartsHQParser),
        typeof(MajorAppliancePartsParser), typeof(PartSelectParser), typeof(EbayParser),
        typeof(AmazonCOMParser), typeof(AmazonCAParser), typeof(SearsPartsDirectParser),
        typeof(ApWagnerParser), typeof(AppliancePartsProsParser)
    };

            foreach (var parserType in parserTypes)
            {
                services.AddTransient(typeof(IParser), parserType);
            }
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
            services.AddOptions<TelegramOptions>().Bind(configuration.GetSection(TelegramOptions.SectionName));
            services.AddOptions<CacheOptions>().Bind(configuration.GetSection(CacheOptions.SectionName));
            services.AddOptions<InternalReplacementLookupOptions>().Bind(configuration.GetSection(InternalReplacementLookupOptions.SectionName));
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
