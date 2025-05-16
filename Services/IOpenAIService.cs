using System.Collections.Generic;
using System.Threading.Tasks;

namespace FashionBot.Services
{
public interface IOpenAIService
{
    Task<string> GenerateImageFromClothesAsync(List<string> imageUrls, string additionalPrompt, string systemPrompt, string userPromptTemplate);
    Task<string> GenerateMatchingOutfitAsync(string baseImageUrl, string additionalPrompt, string systemPrompt, string userPromptTemplate);
}
}