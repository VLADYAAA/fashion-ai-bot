using FashionBot.Models;
using System.ComponentModel.DataAnnotations;
namespace FashionBot.Models
{
    public class AppSettings
    {
        [Required]
        public TelegramBotSettings TelegramBotSettings { get; set; } = new();
        [Required]
        public OpenAISettings OpenAISettings { get; set; } = new();
        [Required]
        public DatabaseSettings DatabaseSettings { get; set; } = new();
        [Required]
        public Prompts Prompts { get; set; } = new();
        [Required]
        public RateLimitSettings RateLimitSettings { get; set; } = new();
    }
    
}