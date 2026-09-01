namespace PAR.PartsGrabber.Options;

public sealed class ClaimPartsAndReplacesRequest
{
    public string WorkerId { get; set; } = string.Empty;

    public int BatchSize { get; set; } = 1;

    public int LeaseSeconds { get; set; } = 3600;
}
