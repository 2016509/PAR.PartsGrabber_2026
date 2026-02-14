using HtmlAgilityPack;

namespace PAR.PartsGrabber
{
    public static class Normalizer
    {
        public static string Text(string input)
        {
            return HtmlEntity.DeEntitize(input)
                .Replace("\r", "")
                .Replace("\n", "")
                .Replace("\t", "")
                .Trim();
        }

        public static string ImageUrl(string url, string baseUrl)
        {
            var uri = new Uri(baseUrl);
            if (url.StartsWith("//"))
            {
                return $"{uri.Scheme}:{url}";
            }
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                return $"{uri.Scheme}://{url}";
            }

            return url;
        }

        public static string PartNumber(string input)
        {
            if (input.StartsWith("WPW")) 
            {
                return $"{input.Substring(2, input.Length - 2)}";
            }

            return input;
        }
    }
}