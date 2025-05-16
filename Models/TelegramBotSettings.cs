using System.ComponentModel.DataAnnotations;
namespace FashionBot.Models
{
    public class TelegramBotSettings
    {
        [Required(ErrorMessage = "BotToken is required")]
        public string BotToken { get; set; } = string.Empty;
        public string WebhookUrl { get; set; } = string.Empty;
        public long AdminUserId { get; set; }
    }
}