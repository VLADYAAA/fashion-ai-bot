using FashionBot.Models;
using System.ComponentModel.DataAnnotations;
namespace FashionBot.Models
{
public class RateLimitSettings

    {
        [Range(1, 100)]
        public int RequestsPerMinute { get; set; } = 20;
        [Range(1, 20)]
        public int MaxConcurrentRequests { get; set; } = 5;
    }
}