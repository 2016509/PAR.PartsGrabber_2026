using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PAR.ParseLib;
using PAR.PartsGrabber.Options;
using System.Text;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PAR.PartsGrabber
{
    public class InternalReplacementLookupService
    {
        private readonly DatabasePartsService _databasePartsService;
        private readonly ILogger _logger;
        private readonly ModuleMetrics _metrics;
        private readonly InternalReplacementLookupOptions _options;

        public InternalReplacementLookupService(
            DatabasePartsService databasePartsService,
            ILogger logger,
            ModuleMetrics metrics,
            IOptions<InternalReplacementLookupOptions> options)
        {
            _databasePartsService = databasePartsService;
            _logger = logger;
            _metrics = metrics;
            _options = options.Value;
        }

        public async Task<InternalReplacementLookupResult> LookupAsync(
            string partNumber,
            List<CheckProxyResult> sourceProxies,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var result = new InternalReplacementLookupResult();
            var normalizedPartNumber = NormalizePartNumber(partNumber);

            if (!_options.Enabled)
            {
                _metrics.ReportInternalReplacementLookup("disabled");
                _logger.LogInformation(
                    "Internal replacements DB lookup disabled. PartNumber={PartNumber}. LookupScope=ReplacementsOnly",
                    normalizedPartNumber);
                return result;
            }

            var configuredSites = GetConfiguredSites();
            if (configuredSites.Count == 0)
            {
                _metrics.ReportInternalReplacementLookup("skipped");
                _logger.LogWarning(
                    "Internal replacements DB lookup skipped because no sources are configured. PartNumber={PartNumber}. LookupScope=ReplacementsOnly",
                    normalizedPartNumber);
                return result;
            }

            var internalSources = sourceProxies
                .Where(x => IsInternalReplacementSource(x.PartSource.SourceName))
                .ToList();
            var missingInternalSites = configuredSites
                .Where(site => !internalSources.Any(x =>
                    SourceMatches(site, x.PartSource.SourceName)))
                .ToList();

            _logger.LogInformation(
                "Internal replacements DB lookup started. PartNumber={PartNumber}, NormalizedPartNumber={NormalizedPartNumber}, ConfiguredSources={ConfiguredSources}, CandidateSources={CandidateSources}, TotalSources={TotalSources}. LookupScope=ReplacementsOnly",
                partNumber,
                normalizedPartNumber,
                string.Join(", ", configuredSites),
                internalSources.Count,
                sourceProxies.Count);

            if (missingInternalSites.Count > 0)
            {
                _logger.LogWarning(
                    "Internal replacements DB lookup cannot check configured source(s) because they are missing from PartSource API list. PartNumber={PartNumber}, MissingSources={MissingSources}",
                    normalizedPartNumber,
                    string.Join(", ", missingInternalSites));
            }

            if (internalSources.Count == 0)
            {
                _metrics.ReportInternalReplacementLookup("skipped");

                _logger.LogInformation(
                    "Internal replacements lookup skipped for {PartNumber}. No internal sources are available in source list. FallbackRequired={FallbackRequired}",
                    partNumber,
                    result.FallbackRequired);
                return result;
            }

            var distinctReplaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceProxy in internalSources)
            {
                ct.ThrowIfCancellationRequested();

                var sourceName = sourceProxy.PartSource.SourceName;
                var internalSourceName = ToInternalSourceName(sourceName);

                _logger.LogInformation(
                    "Internal replacements DB lookup source started. PartNumber={PartNumber}, Source={Source}, InternalSource={InternalSource}, SourceStatus={SourceStatus}, ProxyCount={ProxyCount}. OnlineSearchNotUsed=True",
                    normalizedPartNumber,
                    sourceName,
                    internalSourceName,
                    sourceProxy.PartSource.Status,
                    sourceProxy.Proxies.Count);

                var dbPart = await _databasePartsService.GetPartDataFromDatabaseAsync(
                    normalizedPartNumber,
                    sourceProxy.PartSource,
                    ct);

                var sourceReplaces = dbPart?.Replaces?
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList() ?? new List<string>();

                if (sourceReplaces.Count == 0)
                {
                    _logger.LogInformation(
                        "Internal replacements DB lookup source miss. PartNumber={PartNumber}, Source={Source}, InternalSource={InternalSource}, ReplacesCount=0",
                        normalizedPartNumber,
                        sourceName,
                        internalSourceName);
                    continue;
                }

                var sourceDistinct = new List<string>();
                foreach (var replace in sourceReplaces)
                {
                    var normalizedReplace = NormalizePartNumber(replace);
                    if (string.IsNullOrEmpty(normalizedReplace))
                        continue;

                    if (distinctReplaces.TryAdd(normalizedReplace, replace.Trim()))
                    {
                        sourceDistinct.Add(replace.Trim());
                    }
                }

                if (sourceDistinct.Count == 0)
                    continue;

                dbPart!.Replaces = sourceDistinct;
                result.ParsingParts.Add(dbPart);
                result.Sources.Add(internalSourceName);
                _metrics.ReportInternalReplacementReplaces(internalSourceName, sourceDistinct.Count);

                _logger.LogInformation(
                    "Internal replacements DB lookup source hit. PartNumber={PartNumber}, Source={Source}, InternalSource={InternalSource}, RawReplacesCount={RawReplacesCount}, DistinctReplacesCount={DistinctReplacesCount}, Replaces={Replaces}",
                    normalizedPartNumber,
                    sourceName,
                    internalSourceName,
                    sourceReplaces.Count,
                    sourceDistinct.Count,
                    string.Join(", ", sourceDistinct));
            }

            result.Replaces.AddRange(distinctReplaces.Values);
            _metrics.ReportInternalReplacementLookup(result.Found ? "hit" : "miss");

            _logger.LogInformation(
                "Internal replacements DB lookup complete. PartNumber={PartNumber}, Found={Found}, DistinctReplacesCount={ReplacesCount}, Sources={Sources}, FallbackRequired={FallbackRequired}, LookupScope=ReplacementsOnly",
                normalizedPartNumber,
                result.Found,
                result.Replaces.Count,
                result.Sources.Count == 0 ? "none" : string.Join(", ", result.Sources),
                result.FallbackRequired);

            return result;
        }

        public bool IsInternalReplacementSource(string sourceName)
        {
            return _options.Enabled
                && GetConfiguredSites().Any(site => SourceMatches(site, sourceName));
        }

        private string ToInternalSourceName(string sourceName)
        {
            var source = GetConfiguredSites().FirstOrDefault(site =>
                SourceMatches(site, sourceName));

            return source == null
                ? $"internal_grabber:{sourceName}"
                : $"internal_grabber:{source}";
        }

        private List<string> GetConfiguredSites()
        {
            return _options.Sources
                .Select(NormalizeSource)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool SourceMatches(string configuredSource, string sourceName)
        {
            var configured = NormalizeSource(configuredSource);
            var source = NormalizeSource(sourceName);

            return configured.Equals(source, StringComparison.OrdinalIgnoreCase)
                || configured.Contains(source, StringComparison.OrdinalIgnoreCase)
                || source.Contains(configured, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSource(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                normalized = uri.Host;

            if (normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[4..];

            return normalized.TrimEnd('/').ToLowerInvariant();
        }

        private static string NormalizePartNumber(string partNumber)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                return string.Empty;

            var builder = new StringBuilder(partNumber.Length);

            foreach (var ch in partNumber.Trim().ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                    builder.Append(ch);
            }

            var normalized = builder.ToString();
            return normalized.StartsWith("WPW", StringComparison.Ordinal)
                ? normalized[2..]
                : normalized;
        }
    }
}
