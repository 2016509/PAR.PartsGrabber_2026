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
                return EnabledSources.Contains(sourceName);
            }

            // Если нет белого списка, но есть черный - исключаем черный
            if (ExcludedSources.Any())
            {
                return !ExcludedSources.Contains(sourceName);
            }

            // Иначе кэшируем всё
            return true;
        }
    }
}