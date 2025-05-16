using FashionBot.Models;
using FashionBot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public interface IPromptService
{
    Task<string> GetImageDescriptionPromptAsync();
    Task<(string SystemPrompt, string UserPrompt)> GetOutfitGenerationPromptsAsync();
    Task<(string SystemPrompt, string UserPrompt)> GetMatchingItemsPromptsAsync();
}

public class PromptService : IPromptService
{
    private readonly IDatabaseService _databaseService;
    private readonly ILogger<PromptService> _logger;
    private readonly Prompts _prompts;

    public PromptService(
        IDatabaseService databaseService, 
        ILogger<PromptService> logger,
        IOptions<AppSettings> appSettings)
    {
        _databaseService = databaseService;
        _logger = logger;
        _prompts = appSettings.Value.Prompts;
    }

    public async Task<string> GetImageDescriptionPromptAsync()
    {
        return _prompts.ImageDescriptionPrompt;
    }

    public async Task<(string SystemPrompt, string UserPrompt)> GetOutfitGenerationPromptsAsync()
    {
        var prompts = await _databaseService.GetPromptsAsync();
        return (prompts?.OutfitGenerationSystemPrompt ?? string.Empty, 
                prompts?.OutfitGenerationUserPrompt ?? string.Empty);
    }

    public async Task<(string SystemPrompt, string UserPrompt)> GetMatchingItemsPromptsAsync()
    {
        var prompts = await _databaseService.GetPromptsAsync();
        return (prompts?.MatchingItemsSystemPrompt ?? string.Empty, 
                prompts?.MatchingItemsUserPrompt ?? string.Empty);
    }
}
