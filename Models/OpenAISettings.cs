
using System.ComponentModel.DataAnnotations;
public class OpenAISettings
{
    [Required(ErrorMessage = "ApiKey is required")]
    public string ApiKey { get; set; } = string.Empty;
    [Required(ErrorMessage = "BaseUrl is required")]
    [Url(ErrorMessage = "BaseUrl must be a valid URL")]
    public string BaseUrl { get; set; } = string.Empty;
    [Required]
    public string ImageGenerationModel { get; set; } = "dall-e-3";
    [Required]
    public string TextAnalysisModel { get; set; } = "gpt-4";
    [Range(1, 10)]
    public int MaxConcurrentRequests { get; set; } = 3;
    [Range(10, 300)]
    public int RequestTimeoutSeconds { get; set; } = 60;
    [Required]
    public string ImageSize { get; set; } = "1024x1024";
    [Required]
    public string ImageQuality { get; set; } = "standard";
    [Required]
    public string ImageStyle { get; set; } = "vivid";
}