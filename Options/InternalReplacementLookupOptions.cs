namespace PAR.PartsGrabber.Options
{
    public class InternalReplacementLookupOptions
    {
        public static readonly string SectionName = "InternalReplacementLookup";

        public bool Enabled { get; set; } = true;

        public List<string> Sources { get; set; } = new();
    }
}
