namespace PAR.PartsGrabber.Options;

public sealed class MarkPartsAndReplacesFailedRequest
{
    public string WorkerId { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public int RetryDelaySeconds { get; set; } = 300;

    public int MaxAttempts { get; set; } = 5;
}
