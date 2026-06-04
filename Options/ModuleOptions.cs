namespace PAR.PartsGrabber
{
    public class ModuleOptions
    {
        public static readonly string SectionName = "Module";

        public int Interval { get; set; }

        public bool UseExternalSourceHealthChecker { get; set; } = true;

        public int ApiRetryIntervalSeconds { get; set; } = 30;
    }
}
