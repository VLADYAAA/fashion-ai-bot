using System.ComponentModel.DataAnnotations;
using FashionBot.Services;
using Microsoft.Extensions.Logging;
namespace FashionBot.Models
{
   public class Prompts
    {
        [Required]
        public string ImageDescriptionPrompt { get; set; } = string.Empty;
        [Required]
        public string OutfitGenerationSystemPrompt { get; set; } = string.Empty;
        
        [Required]
        public string OutfitGenerationUserPrompt { get; set; } = string.Empty;
        [Required]
        public string MatchingItemsSystemPrompt { get; set; } = string.Empty;
        [Required]
        public string MatchingItemsUserPrompt { get; set; } = string.Empty;
    }
}