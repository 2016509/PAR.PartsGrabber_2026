namespace PAR.PartsGrabber
{
    public class ModuleOptions
    {
        public static readonly string SectionName = "Module";

        public int Interval { get; set; }

        public bool UseExternalSourceHealthChecker { get; set; } = true;

        public int ApiRetryIntervalSeconds { get; set; } = 30;

        public string WorkerId { get; set; } =
            Environment.GetEnvironmentVariable("PARTS_GRABBER_WORKER_ID")
            ?? Environment.GetEnvironmentVariable("HOSTNAME")
            ?? $"parts-grabber-{Guid.NewGuid():N}";

        public int ClaimBatchSize { get; set; } = 1;

        public int LeaseSeconds { get; set; } = 3600;

        public int RetryDelaySeconds { get; set; } = 300;

        public int MaxAttempts { get; set; } = 5;
    }
}
