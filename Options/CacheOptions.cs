// Options/CacheOptions.cs
namespace PAR.PartsGrabber.Options
{
    public class CacheOptions
    {
        public static readonly string SectionName = "Cache";

        /// <summary>
        /// Включено ли кэширование
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Белый список - только эти источники будут кэшироваться (если пустой - кэшируются все)
        /// </summary>
        public List<string> EnabledSources { get; set; } = new();

        /// <summary>
        /// Черный список - эти источники НЕ будут кэшироваться
        /// </summary>
        public List<string> ExcludedSources { get; set; } = new();

        /// <summary>
        /// Время жизни кэша в днях
        /// </summary>
        public int DefaultTtlDays { get; set; } = 30;

        /// <summary>
        /// Проверяет, должен ли источник кэшироваться
        /// </summary>
        public bool ShouldCacheSource(string sourceName)
        {
            if (!Enabled) return false;

            // Если есть белый список - только из него
            if (EnabledSources.Any())
            {
                return EnabledSources.Any(x => SourceMatches(x, sourceName));
            }

            // Если нет белого списка, но есть черный - исключаем черный
            if (ExcludedSources.Any())
            {
                return !ExcludedSources.Contains(sourceName);
            }

            // Иначе кэшируем всё
            return true;
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
    }
}
