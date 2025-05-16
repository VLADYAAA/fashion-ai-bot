using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FashionBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FashionBot.Services
{

    public class DatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly ILogger<DatabaseService> _logger;

        public DatabaseService(IOptions<DatabaseSettings> settings, ILogger<DatabaseService> logger)
        {
            _connectionString = settings?.Value?.ConnectionString ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InitializeDatabaseAsync()
        {
            try 
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                var commands = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS public.user_requests (
                        id UUID PRIMARY KEY,
                        user_id BIGINT NOT NULL,
                        request_type INTEGER NOT NULL,
                        images TEXT[] NOT NULL,
                        prompt TEXT,
                        result_url TEXT,
                        status INTEGER NOT NULL,
                        created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
                        processed_at TIMESTAMP WITH TIME ZONE
                    )",
                    @"CREATE TABLE IF NOT EXISTS public.app_settings (
                        id SERIAL PRIMARY KEY,
                        telegram_bot_token TEXT NOT NULL,
                        telegram_webhook_url TEXT,
                        telegram_admin_user_id BIGINT,
                        openai_api_key TEXT NOT NULL,
                        openai_base_url TEXT NOT NULL,
                        openai_image_model TEXT NOT NULL,
                        openai_text_model TEXT NOT NULL,
                        openai_max_concurrent_requests INTEGER NOT NULL,
                        openai_request_timeout_seconds INTEGER NOT NULL,
                        openai_image_size TEXT NOT NULL,
                        openai_image_quality TEXT NOT NULL,
                        openai_image_style TEXT NOT NULL,
                        rate_limit_requests_per_minute INTEGER DEFAULT 20,
                        rate_limit_max_concurrent_requests INTEGER DEFAULT 5,
                        updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                    )",
                    @"CREATE TABLE IF NOT EXISTS public.prompts (
                        id SERIAL PRIMARY KEY,
                        outfit_generation_system_prompt TEXT NOT NULL,
                        outfit_generation_user_prompt TEXT NOT NULL,
                        matching_items_system_prompt TEXT NOT NULL,
                        matching_items_user_prompt TEXT NOT NULL,
                        updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                    )",
                    @"CREATE TABLE IF NOT EXISTS public.user_settings (
                        user_id BIGINT PRIMARY KEY,
                        cached_prompt TEXT,
                        updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                    )"
                };

                foreach (var cmdText in commands)
                {
                    await using var cmd = new NpgsqlCommand(cmdText, connection);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database initialization failed");
                throw;
            }
        }

        public async Task SaveRequestAsync(FashionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Id == Guid.Empty) request.Id = Guid.NewGuid();

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var cmdText = @"
                INSERT INTO user_requests 
                    (id, user_id, request_type, images, prompt, result_url, status, created_at, processed_at)
                VALUES 
                    (@id, @userId, @requestType, @images, @prompt, @resultUrl, @status, @createdAt, @processedAt)
                ON CONFLICT (id) DO UPDATE SET
                    user_id = EXCLUDED.user_id,
                    request_type = EXCLUDED.request_type,
                    images = EXCLUDED.images,
                    prompt = EXCLUDED.prompt,
                    result_url = EXCLUDED.result_url,
                    status = EXCLUDED.status,
                    processed_at = EXCLUDED.processed_at";

            using var command = new NpgsqlCommand(cmdText, connection);
            
            command.Parameters.AddWithValue("id", request.Id);
            command.Parameters.AddWithValue("userId", request.UserId);
            command.Parameters.AddWithValue("requestType", (int)request.RequestType);
            
            command.Parameters.Add(new NpgsqlParameter("images", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = request.Images?.ToArray() ?? Array.Empty<string>()
            });
            
            command.Parameters.AddWithValue("prompt", 
                string.IsNullOrEmpty(request.Prompt) ? (object)DBNull.Value : request.Prompt);
            command.Parameters.AddWithValue("resultUrl", 
                string.IsNullOrEmpty(request.ResultUrl) ? (object)DBNull.Value : request.ResultUrl);
            command.Parameters.AddWithValue("status", (int)request.Status);
            command.Parameters.AddWithValue("createdAt", request.CreatedAt);
            command.Parameters.AddWithValue("processedAt", 
                request.ProcessedAt ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<FashionRequest> GetRequestAsync(Guid requestId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT * FROM user_requests WHERE id = @id";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("id", requestId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new FashionRequest
                {
                    Id = reader.GetGuid(0),
                    UserId = reader.GetInt64(1),
                    RequestType = (FashionRequestType)reader.GetInt32(2),
                    Images = reader.GetFieldValue<List<string>>(3),
                    Prompt = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ResultUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Status = (FashionRequestStatus)reader.GetInt32(6),
                    CreatedAt = reader.GetDateTime(7),
                    ProcessedAt = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8)
                };
            }

            return null;
        }

        public async Task UpdateAppSettingsAsync(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updateCommand = @"
                DELETE FROM app_settings;
                INSERT INTO app_settings (
                    telegram_bot_token, telegram_webhook_url, telegram_admin_user_id,
                    openai_api_key, openai_base_url, openai_image_model, openai_text_model,
                    openai_max_concurrent_requests, openai_request_timeout_seconds,
                    openai_image_size, openai_image_quality, openai_image_style,
                    rate_limit_requests_per_minute, rate_limit_max_concurrent_requests
                ) VALUES (
                    @botToken, @webhookUrl, @adminUserId,
                    @apiKey, @baseUrl, @imageModel, @textModel,
                    @maxConcurrentRequests, @requestTimeoutSeconds,
                    @imageSize, @imageQuality, @imageStyle,
                    @requestsPerMinute, @maxConcurrentRequestsLimit
                )";

            await using var cmd = new NpgsqlCommand(updateCommand, connection);
            cmd.Parameters.AddWithValue("botToken", settings.TelegramBotSettings.BotToken);
            cmd.Parameters.AddWithValue("webhookUrl", settings.TelegramBotSettings.WebhookUrl ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("adminUserId", settings.TelegramBotSettings.AdminUserId);
            cmd.Parameters.AddWithValue("apiKey", settings.OpenAISettings.ApiKey);
            cmd.Parameters.AddWithValue("baseUrl", settings.OpenAISettings.BaseUrl);
            cmd.Parameters.AddWithValue("imageModel", settings.OpenAISettings.ImageGenerationModel);
            cmd.Parameters.AddWithValue("textModel", settings.OpenAISettings.TextAnalysisModel);
            cmd.Parameters.AddWithValue("maxConcurrentRequests", settings.OpenAISettings.MaxConcurrentRequests);
            cmd.Parameters.AddWithValue("requestTimeoutSeconds", settings.OpenAISettings.RequestTimeoutSeconds);
            cmd.Parameters.AddWithValue("imageSize", settings.OpenAISettings.ImageSize);
            cmd.Parameters.AddWithValue("imageQuality", settings.OpenAISettings.ImageQuality);
            cmd.Parameters.AddWithValue("imageStyle", settings.OpenAISettings.ImageStyle);
            cmd.Parameters.AddWithValue("requestsPerMinute", settings.RateLimitSettings.RequestsPerMinute);
            cmd.Parameters.AddWithValue("maxConcurrentRequestsLimit", settings.RateLimitSettings.MaxConcurrentRequests);
            
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<AppSettings> GetAppSettingsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT * FROM app_settings ORDER BY id DESC LIMIT 1";
            await using var cmd = new NpgsqlCommand(query, connection);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new AppSettings
                {
                    TelegramBotSettings = new TelegramBotSettings
                    {
                        BotToken = reader.GetString(1),
                        WebhookUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                        AdminUserId = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
                    },
                    OpenAISettings = new OpenAISettings
                    {
                        ApiKey = reader.GetString(4),
                        BaseUrl = reader.GetString(5),
                        ImageGenerationModel = reader.GetString(6),
                        TextAnalysisModel = reader.GetString(7),
                        MaxConcurrentRequests = reader.GetInt32(8),
                        RequestTimeoutSeconds = reader.GetInt32(9),
                        ImageSize = reader.GetString(10),
                        ImageQuality = reader.GetString(11),
                        ImageStyle = reader.GetString(12)
                    },
                    RateLimitSettings = new RateLimitSettings
                    {
                        RequestsPerMinute = reader.IsDBNull(13) ? 20 : reader.GetInt32(13),
                        MaxConcurrentRequests = reader.IsDBNull(14) ? 5 : reader.GetInt32(14)
                    }
                }?? throw new InvalidOperationException("Failed to create AppSettings");
            }
            return null;
        }

        public async Task UpdatePromptsAsync(Prompts prompts)
        {
            if (prompts == null) throw new ArgumentNullException(nameof(prompts));

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updateCommand = @"
                DELETE FROM prompts;
                INSERT INTO prompts 
                (outfit_generation_system_prompt, outfit_generation_user_prompt, 
                matching_items_system_prompt, matching_items_user_prompt)
                VALUES (@outfitSystem, @outfitUser, @matchingSystem, @matchingUser)";

            await using var cmd = new NpgsqlCommand(updateCommand, connection);
            cmd.Parameters.AddWithValue("outfitSystem", prompts.OutfitGenerationSystemPrompt);
            cmd.Parameters.AddWithValue("outfitUser", prompts.OutfitGenerationUserPrompt);
            cmd.Parameters.AddWithValue("matchingSystem", prompts.MatchingItemsSystemPrompt);
            cmd.Parameters.AddWithValue("matchingUser", prompts.MatchingItemsUserPrompt);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<Prompts> GetPromptsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT * FROM prompts ORDER BY id DESC LIMIT 1";
            await using var cmd = new NpgsqlCommand(query, connection);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Prompts
                {
                    OutfitGenerationSystemPrompt = reader.GetString(1),
                    OutfitGenerationUserPrompt = reader.GetString(2),
                    MatchingItemsSystemPrompt = reader.GetString(3),
                    MatchingItemsUserPrompt = reader.GetString(4)
                };
            }

            return null;
        }

        public async Task UpdateOpenAISettingsAsync(OpenAISettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updateCommand = @"
                UPDATE app_settings SET
                    openai_api_key = @apiKey,
                    openai_base_url = @baseUrl,
                    openai_image_model = @imageModel,
                    openai_text_model = @textModel,
                    openai_max_concurrent_requests = @maxRequests,
                    openai_request_timeout_seconds = @timeout,
                    openai_image_size = @imageSize,
                    openai_image_quality = @imageQuality,
                    openai_image_style = @imageStyle,
                    updated_at = NOW()
                WHERE id = (SELECT MAX(id) FROM app_settings)";

            await using var cmd = new NpgsqlCommand(updateCommand, connection);
            cmd.Parameters.AddWithValue("apiKey", settings.ApiKey);
            cmd.Parameters.AddWithValue("baseUrl", settings.BaseUrl);
            cmd.Parameters.AddWithValue("imageModel", settings.ImageGenerationModel);
            cmd.Parameters.AddWithValue("textModel", settings.TextAnalysisModel);
            cmd.Parameters.AddWithValue("maxRequests", settings.MaxConcurrentRequests);
            cmd.Parameters.AddWithValue("timeout", settings.RequestTimeoutSeconds);
            cmd.Parameters.AddWithValue("imageSize", settings.ImageSize);
            cmd.Parameters.AddWithValue("imageQuality", settings.ImageQuality);
            cmd.Parameters.AddWithValue("imageStyle", settings.ImageStyle);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<string> GetUserCachedPromptAsync(long userId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT cached_prompt FROM user_settings WHERE user_id = @userId";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("userId", userId);

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString();
        }

        public async Task UpdateUserCachedPromptAsync(long userId, string prompt)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updateCommand = @"
                INSERT INTO user_settings (user_id, cached_prompt)
                VALUES (@userId, @prompt)
                ON CONFLICT (user_id) DO UPDATE SET
                    cached_prompt = EXCLUDED.cached_prompt,
                    updated_at = NOW()";

            await using var cmd = new NpgsqlCommand(updateCommand, connection);
            cmd.Parameters.AddWithValue("userId", userId);
            cmd.Parameters.AddWithValue("prompt", prompt ?? string.Empty);

            await cmd.ExecuteNonQueryAsync();
        }
    }


}