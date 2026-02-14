namespace PAR.PartsGrabber
{
    public class SitesToCheckProxyOptions
    {
        public static readonly string SectionName = "SitesToCheckProxy";

        public List<string> SitesToCheckProxy { get; set; } = new List<string>();
    }
}