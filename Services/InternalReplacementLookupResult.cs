using PAR.ParseLib;

namespace PAR.PartsGrabber
{
    public class InternalReplacementLookupResult
    {
        public bool Found => Replaces.Count > 0;

        public List<string> Replaces { get; } = new();

        public List<string> Sources { get; } = new();

        public List<ParsingPart> ParsingParts { get; } = new();

        public bool FallbackRequired => !Found;
    }
}
