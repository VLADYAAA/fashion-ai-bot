using System;
using System.Threading.Tasks;
using FashionBot.Models; // Добавляем это

namespace FashionBot.Services
{   
    public interface IDatabaseService
    {
        Task InitializeDatabaseAsync();
        Task SaveRequestAsync(FashionRequest request);
        Task<FashionRequest> GetRequestAsync(Guid requestId);
        Task UpdateAppSettingsAsync(AppSettings settings);
        Task<AppSettings> GetAppSettingsAsync();
        Task UpdatePromptsAsync(Prompts prompts);
        Task<Prompts> GetPromptsAsync();
        Task UpdateOpenAISettingsAsync(OpenAISettings settings);
        Task<string> GetUserCachedPromptAsync(long userId);  
        Task UpdateUserCachedPromptAsync(long userId, string prompt); 
    }
}