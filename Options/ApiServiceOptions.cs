using PAR.PartsGrabber.Options;

namespace PAR.PartsGrabber
{
    public class ApiServiceOptions
    {
        public static readonly string SectionName = "ApiService";

        public string BaseUrl { get; set; } = null!;

        public PartsServiceUrls ServiceUrls { get; set; } = new();
        public string PartsRoot => Resolve(ServiceUrls.Parts, nameof(ServiceUrls.Parts));
        public string ProxyRoot => Resolve(ServiceUrls.Proxy, nameof(ServiceUrls.Proxy));
        public string SourcesRoot => Resolve(ServiceUrls.PartSource, nameof(ServiceUrls.PartSource));
        public string ErrorsRoot => Resolve(ServiceUrls.ErrorLog, nameof(ServiceUrls.ErrorLog));

        private string Resolve(string? address, string service)
        {
            var root = ServiceUrls.Enabled ? address : BaseUrl;
            if (string.IsNullOrWhiteSpace(root) || !Uri.TryCreate(root, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Missing or invalid API address for {service}.");
            return root.TrimEnd('/');
        }

        public void ValidateEndpoints()
        {
            _ = PartsRoot; _ = ProxyRoot; _ = SourcesRoot; _ = ErrorsRoot;
        }

        public string GetProxiesUrl { get; set; } = null!;

        public string GetPartsWithStateUrl { get; set; } = null!;

        public string ClaimPartsAndReplacesUrl { get; set; } = "/PartsAndReplaces/claim";

        public string GetPartsSourcesUrl { get; set; } = null!;

        public string AddPartsNamesArchiveUrl { get; set; } = null!;

        public string AddReplacesArchiveArchiveUrl { get; set; } = null!;

        public string AddPartsPicArchiveUrl { get; set; } = null!;

        public string UpdatePartsAndReplacesStatusUrl { get; set; } = null!;

        public string UpdatePartsAndReplacesUrl { get; set; } = null!;

        public string MarkPartsAndReplacesFailedUrl { get; set; } = "/PartsAndReplaces/mark-failed";

        public string UpdateProxyStatusUrl { get; set; } = null!;

        public string UpdatePartSourceUrl { get; set; } = null!;

        public string SaveErrorUrl { get; set;} = null!;

        public string GetCachedPartData { get; set; } = null!;

        public string SaveCachedPartData { get; set; } = null!;
    }

    public sealed class PartsServiceUrls
    {
        public string? Parts { get; set; }
        public string? Proxy { get; set; }
        public string? PartSource { get; set; }
        public string? ErrorLog { get; set; }
        public bool Enabled => new[] { Parts, Proxy, PartSource, ErrorLog }.Any(x => !string.IsNullOrWhiteSpace(x));
    }
}
