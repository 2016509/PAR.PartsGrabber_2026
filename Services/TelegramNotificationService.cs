// ITelegramNotificationService.cs
using Microsoft.Extensions.Options;
using PAR.PartsGrabber.Options;
using Microsoft.Extensions.Logging;

public interface ITelegramNotificationService
{
    Task SendTimeoutNotificationAsync(string partNumber, DateTime startTime);
}

// TelegramNotificationService.cs
public class TelegramNotificationService : ITelegramNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _botToken;
    private readonly long _chatId;

    public TelegramNotificationService(HttpClient httpClient, ILogger logger, IOptions<TelegramOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _botToken = options.Value.BotToken;
        _chatId = options.Value.ChatId;
    }

    public async Task SendTimeoutNotificationAsync(string partNumber, DateTime startTime)
    {
        try
        {
            var message = $"⏰ **TIMEOUT PartsGrabber**\n" +
                         $"Деталь: `{partNumber}`\n" +
                         $"Время работы: {DateTime.UtcNow - startTime:mm\\:ss}\n" +
                         $"Данные собраны частично";

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", _chatId.ToString()),
                new KeyValuePair<string, string>("text", message),
                new KeyValuePair<string, string>("parse_mode", "Markdown")
            });

            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation("Telegram уведомление отправлено для {Part}", partNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка отправки Telegram уведомления для {Part}", partNumber);
        }
    }
}
