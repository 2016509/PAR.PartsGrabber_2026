namespace PAR.PartsGrabber
{
    public class ApiServiceOptions
    {
        public static readonly string SectionName = "ApiService";

        public string BaseUrl { get; set; } = null!;

        public string GetProxiesUrl { get; set; } = null!;

        public string GetPartsWithStateUrl { get; set; } = null!;

        public string GetPartsSourcesUrl { get; set; } = null!;

        public string AddPartsNamesArchiveUrl { get; set; } = null!;

        public string AddReplacesArchiveArchiveUrl { get; set; } = null!;

        public string AddPartsPicArchiveUrl { get; set; } = null!;

        public string UpdatePartsAndReplacesStatusUrl { get; set; } = null!;

        public string UpdatePartsAndReplacesUrl { get; set; } = null!;

        public string UpdateProxyStatusUrl { get; set; } = null!;

        public string UpdatePartSourceUrl { get; set; } = null!;

        public string SaveErrorUrl { get; set;} = null!;
    }
}