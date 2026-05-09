using KameraData.Data.Dto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PAR.PartsGrabber.Options
{
    public class CachedPartData
        {
        public int PartSourceId { get; set; }
        public string SourceName { get; set; } = null!;
        public string? Name { get; set; }
        public List<string> Replaces { get; set; } = new();
        public List<CachedPictureDto> Pictures { get; set; } = new();
        public string? SitePartNumber { get; set; }
        public double RegularPrice { get; set; }
        public int AttempsCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }

        public bool IsValid => !ExpiresAt.HasValue || ExpiresAt.Value > DateTime.UtcNow;
    }
 

    public class CachedPicture
    {
        public string LocalPath { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
