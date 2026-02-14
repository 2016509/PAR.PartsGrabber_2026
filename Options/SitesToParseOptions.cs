using PAR.ParseLib;

namespace PAR.PartsGrabber
{
    public class SitesToParseOptions
    {
        public static readonly string SectionName = "SitesToParse";

        public List<SiteToParse> SitesToParse { get; set; } = new List<SiteToParse>();
    }
}