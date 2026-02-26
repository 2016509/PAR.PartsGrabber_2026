using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PAR.PartsGrabber.Options
{
    public class TelegramOptions
    {
        public static readonly string SectionName = "Telegram";

        public string BotToken { get; set; }
        public long ChatId { get; set; }
    }
}
