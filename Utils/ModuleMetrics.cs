using Microsoft.Extensions.Logging;
using Prometheus;
using System.Diagnostics;

namespace PAR.PartsGrabber
{
    public class ModuleMetrics
    {
        private readonly ILogger<ModuleMetrics> _logger;

        // Основные метрики
        public readonly Gauge ModuleHealthy = Metrics.CreateGauge("partsgrabber_module_healthy", "1=healthy, 0=unhealthy");
        public readonly Counter PartsProcessed = Metrics.CreateCounter("partsgrabber_parts_processed_total", "Total parts processed", new[] { "status" });
        public readonly Histogram ProcessingDuration = Metrics.CreateHistogram("partsgrabber_processing_duration_seconds", "Processing duration");
        public readonly Counter ErrorsTotal = Metrics.CreateCounter("partsgrabber_errors_total", "Total errors", new[] { "type" });
        public readonly Gauge ActiveProxies = Metrics.CreateGauge("partsgrabber_active_proxies", "Number of active proxies");
        public readonly Gauge UptimeSeconds = Metrics.CreateGauge("partsgrabber_uptime_seconds", "Uptime in seconds");

        // ✅ НОВЫЕ: кэш метрики
        public readonly Counter CacheHits = Metrics.CreateCounter("partsgrabber_cache_hits_total", "Cache hits", new[] { "site" });
        public readonly Counter CacheMisses = Metrics.CreateCounter("partsgrabber_cache_misses_total", "Cache misses", new[] { "site" });

        private readonly CancellationTokenSource _cts = new();
        private DateTime _startTime = DateTime.UtcNow;

        public ModuleMetrics(ILogger<ModuleMetrics> logger)
        {
            _logger = logger;
            ModuleHealthy.Set(1);
            UptimeSeconds.Set(0);
        }

        public void StartHealthCheck()
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        ModuleHealthy.Set(1);
                        UptimeSeconds.Set((DateTime.UtcNow - _startTime).TotalSeconds);
                        await Task.Delay(30000, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Health check failed");
                        ModuleHealthy.Set(0);
                    }
                }
            });
        }

        public void ReportPartProcessed(bool success)
        {
            PartsProcessed.WithLabels(success ? "success" : "error").Inc();
        }

        public void ReportProcessingDuration(double seconds)
        {
            ProcessingDuration.Observe(seconds);
        }

        public void ReportError(string errorType)
        {
            ErrorsTotal.WithLabels(errorType).Inc();
        }

        public void UpdateActiveProxiesCount(int count)
        {
            ActiveProxies.Set(count);
        }

        public void ReportCacheHit(string site) => CacheHits.WithLabels(site).Inc();
        public void ReportCacheMiss(string site) => CacheMisses.WithLabels(site).Inc();

        public void ReportFatalError()
        {
            ModuleHealthy.Set(0);
            _cts.Cancel();
        }

        public void UpdateUptime()
        {
            UptimeSeconds.Set((DateTime.UtcNow - _startTime).TotalSeconds); // ✅
        }
    }
}