using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FashionBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FashionBot.Services
{
    public class OpenAIService : IOpenAIService, IDisposable
    {
        private readonly OpenAISettings _settings;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _rateLimiter;
        private readonly ILogger<OpenAIService> _logger;

        public OpenAIService(IOptions<OpenAISettings> settings, ILogger<OpenAIService> logger)
        {
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            ValidateSettings();

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_settings.BaseUrl),
                Timeout = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds)
            };

            ConfigureHttpClient();
            _rateLimiter = new SemaphoreSlim(_settings.MaxConcurrentRequests);
        }

        private void ValidateSettings()
        {
            if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
                throw new ArgumentException("OpenAI BaseUrl is not configured");

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new ArgumentException("OpenAI ApiKey is not configured");

            if (string.IsNullOrWhiteSpace(_settings.ImageGenerationModel))
                throw new ArgumentException("ImageGenerationModel is not configured");

            if (string.IsNullOrWhiteSpace(_settings.TextAnalysisModel))
                throw new ArgumentException("TextAnalysisModel is not configured");
        }

        private void ConfigureHttpClient()
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<string> GenerateMatchingOutfitAsync(
            string baseImageUrl,
            string additionalPrompt,
            string systemPrompt,
            string userPromptTemplate)
        {
            if (string.IsNullOrWhiteSpace(baseImageUrl))
                throw new ArgumentException("Base image URL is required");

            await _rateLimiter.WaitAsync();
            try
            {
                // 1. Анализ изображения и генерация промта
                var dallePrompt = await GenerateDallePrompt(
                    baseImageUrl,
                    systemPrompt,
                    userPromptTemplate,
                    additionalPrompt);

                // 2. Генерация изображения
                return await GenerateImageFromPrompt(dallePrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GenerateMatchingOutfitAsync");
                throw;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        private async Task<string> GenerateDallePrompt(
            string imageUrl,
            string systemPrompt,
            string userPromptTemplate,
            string additionalPrompt)
        {
            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = imageUrl } },
                        new { type = "text", text = userPromptTemplate },
                        new { type = "text", text = $"Additional requirements: {additionalPrompt}" }
                    }
                }
            };

            var requestData = new
            {
                model = _settings.TextAnalysisModel,
                messages,
                max_tokens = 1000,
                temperature = 0.1
            };

            var response = await _httpClient.PostAsJsonAsync("chat/completions", requestData);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API Error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
            return result?.Choices?.FirstOrDefault()?.Message?.Content;
        }

        private async Task<string> GenerateImageFromPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt cannot be empty");

            var requestData = new
            {
                model = _settings.ImageGenerationModel,
                prompt = $"{prompt}\n\nIMPORTANT: Create vertical portrait (9:16) image with full-body model",
                size = "1024x1792", // Вертикальный формат
                quality = _settings.ImageQuality,
                style = _settings.ImageStyle,
                response_format = "url",
                n = 1
            };

            var response = await _httpClient.PostAsJsonAsync("images/generations", requestData);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API Error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAIImageResponse>();
            return result?.Data?.FirstOrDefault()?.Url;
        }

        public async Task<string> GenerateImageFromClothesAsync(
            List<string> imageUrls,
            string additionalPrompt,
            string systemPrompt,
            string userPromptTemplate)
        {
            if (imageUrls == null || !imageUrls.Any())
                throw new ArgumentException("At least one image URL is required");

            await _rateLimiter.WaitAsync();
            try
            {
                // 1. Анализ изображений и генерация промта
                var dallePrompt = await GenerateDallePromptForMultipleImages(
                    imageUrls,
                    systemPrompt,
                    userPromptTemplate,
                    additionalPrompt);

                // 2. Генерация изображения
                return await GenerateImageFromPrompt(dallePrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GenerateImageFromClothesAsync");
                throw;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        private async Task<string> GenerateDallePromptForMultipleImages(
            List<string> imageUrls,
            string systemPrompt,
            string userPromptTemplate,
            string additionalPrompt)
        {
            var contentItems = new List<object>();

            foreach (var url in imageUrls)
            {
                contentItems.Add(new { type = "image_url", image_url = new { url } });
            }

            contentItems.Add(new { type = "text", text = userPromptTemplate });
            contentItems.Add(new { type = "text", text = $"Additional requirements: {additionalPrompt}" });

            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = contentItems.ToArray()
                }
            };

            var requestData = new
            {
                model = _settings.TextAnalysisModel,
                messages,
                max_tokens = 1000,
                temperature = 0.1
            };

            var response = await _httpClient.PostAsJsonAsync("chat/completions", requestData);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API Error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
            return result?.Choices?.FirstOrDefault()?.Message?.Content;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _rateLimiter?.Dispose();
        }
    }
    public class OpenAIChatResponse
    {
        public List<OpenAIChatChoice>? Choices { get; set; }
    }

    public class OpenAIChatChoice
    {
        public OpenAIChatMessage? Message { get; set; }
    }

    public class OpenAIChatMessage
    {
        public string? Content { get; set; }
    }

    public class OpenAIImageResponse
    {
        public List<OpenAIImageData>? Data { get; set; }
    }

    public class OpenAIImageData
    {
        public string? Url { get; set; }
        public string? B64_Json { get; set; }
    }
}