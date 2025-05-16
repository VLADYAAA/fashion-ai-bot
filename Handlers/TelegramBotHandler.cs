

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FashionBot.Models;
using FashionBot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace FashionBot.Handlers
{
    public class TelegramBotHandler
    {
        private readonly TelegramBotClient _botClient;
        private readonly IOpenAIService _openAIService;
        private readonly IRequestQueueService _queueService;
        private readonly IDatabaseService _databaseService;
        private readonly ILogger<TelegramBotHandler> _logger;
        private readonly AppSettings _appSettings;
        private readonly ITelegramResponseQueue _responseQueue;
        private readonly IPromptService _promptService;
        
        private readonly ConcurrentDictionary<long, UserState> _userStates = new();

        public TelegramBotHandler(
            IOptions<AppSettings> appSettings,
            IOpenAIService openAIService,
            IRequestQueueService queueService,
            IDatabaseService databaseService,
            ILogger<TelegramBotHandler> logger,
            ITelegramResponseQueue responseQueue,
            IPromptService promptService)
        {
            _appSettings = appSettings?.Value ?? throw new ArgumentNullException(nameof(appSettings));
            _openAIService = openAIService ?? throw new ArgumentNullException(nameof(openAIService));
            _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _responseQueue = responseQueue ?? throw new ArgumentNullException(nameof(responseQueue));
            _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));

            if (string.IsNullOrWhiteSpace(_appSettings.TelegramBotSettings?.BotToken))
            {
                throw new ArgumentException("Bot token is not configured");
            }

            _botClient = new TelegramBotClient(_appSettings.TelegramBotSettings.BotToken);
            _logger.LogInformation("Telegram bot client initialized");
        }
        

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Message is not { } message)
                    return;

                var chatId = message.Chat.Id;
                var userId = message.From.Id;

                var userState = _userStates.GetOrAdd(userId, id => new UserState { UserId = id });

                if (message.Text is { } messageText)
                {
                    if (messageText.StartsWith("/start"))
                    {
                        var cachedPrompt = await _databaseService.GetUserCachedPromptAsync(userId);
                        var welcomeMessage = "👋 Привет! Я бот для создания модных образов.\n\n";

                        if (string.IsNullOrEmpty(cachedPrompt))
                        {
                            welcomeMessage += "Перед началом работы установите свой промпт командой /updateprompt [ваш промпт].\n" +
                                            "Например: /updateprompt деловой стиль\n\n" +
                                            "Это поможет мне лучше понимать ваши предпочтения при создании образов.";
                        }
                        else
                        {
                            welcomeMessage += $"Ваш текущий промпт: {cachedPrompt}\n" +
                                            "Вы можете изменить его командой /updateprompt [новый промпт]\n\n" +
                                            "Выберите действие в меню ниже.";
                        }

                        await _responseQueue.EnqueueResponseAsync(async bot =>
                        {
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: welcomeMessage,
                                cancellationToken: cancellationToken);
                            await ShowMainMenu(chatId, cancellationToken);
                        });
                        return;
                    }

                    if (messageText.StartsWith("/settings") && userId == _appSettings.TelegramBotSettings.AdminUserId)
                    {
                        await _responseQueue.EnqueueResponseAsync(async bot =>
                            await HandleSettingsCommand(chatId, messageText, cancellationToken));
                        return;
                    }

                    if (messageText.StartsWith("/updateprompt"))
                    {
                        await _responseQueue.EnqueueResponseAsync(async bot =>
                            await HandleUpdatePromptCommand(chatId, messageText, cancellationToken));
                        return;
                    }
                    if (userState.CurrentState == UserStateState.WaitingForPromptInput)
                    {
                        if (!string.IsNullOrWhiteSpace(messageText))
                        {
                            await _databaseService.UpdateUserCachedPromptAsync(userId, messageText);
                            userState.CurrentState = UserStateState.Idle;
                            
                            await _responseQueue.EnqueueResponseAsync(async bot => 
                            {
                                await bot.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: $"✅ Промпт сохранен: {messageText}",
                                    cancellationToken: cancellationToken);
                                await ShowMainMenu(chatId, cancellationToken);
                            });
                        }
                        else
                        {
                            await _responseQueue.EnqueueResponseAsync(async bot => 
                                await bot.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Промпт не может быть пустым. Попробуйте еще раз.",
                                    cancellationToken: cancellationToken));
                        }
                        return;
                    }

                    switch (userState.CurrentState)
                    {
                        case UserStateState.WaitingForOutfitPrompt:
                            await HandlePromptInput(userId, messageText);
                            await _responseQueue.EnqueueResponseAsync(async bot =>
                                await bot.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Теперь отправьте фотографии одежды, которую хотите объединить в образ.",
                                    cancellationToken: cancellationToken));
                            userState.CurrentState = UserStateState.WaitingForOutfitImages;
                            return;

                        case UserStateState.WaitingForMatchingPrompt:
                            await HandlePromptInput(userId, messageText);
                            await _responseQueue.EnqueueResponseAsync(async bot =>
                                await bot.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Теперь отправьте фотографию одежды, для которой нужно подобрать образ.",
                                    cancellationToken: cancellationToken));
                            userState.CurrentState = UserStateState.WaitingForMatchingImage;
                            return;
                    }
                }

                if (message.Photo is { } photos)
                {
                    var photo = photos.Last();
                    var fileId = photo.FileId;
                    var fileInfo = await _botClient.GetFileAsync(fileId, cancellationToken);
                    var fileUrl = $"https://api.telegram.org/file/bot{_appSettings.TelegramBotSettings.BotToken}/{fileInfo.FilePath}";

                    switch (userState.CurrentState)
                    {
                        case UserStateState.WaitingForOutfitImages:
                            userState.Images.Add(fileUrl);
                            await _responseQueue.EnqueueResponseAsync(async bot =>
                                await bot.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: $"Фотография получена. Отправьте еще или нажмите /generate для создания образа.",
                                    cancellationToken: cancellationToken));
                            return;

                        case UserStateState.WaitingForMatchingImage:
                            userState.Images.Add(fileUrl);
                            await _responseQueue.EnqueueResponseAsync(async bot =>
                                await ProcessFashionRequest(userState, chatId, cancellationToken));
                            return;
                    }
                }

                if (message.Text == "Объединить вещи в образ")
                {
                    var cachedPrompt = await _databaseService.GetUserCachedPromptAsync(userId);
                    if (string.IsNullOrEmpty(cachedPrompt))
                    {
                        await _responseQueue.EnqueueResponseAsync(async bot =>
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "❌ Сначала установите промпт командой /updateprompt [ваш промпт]",
                                cancellationToken: cancellationToken));
                        return;
                    }

                    userState.CurrentState = UserStateState.WaitingForOutfitImages;
                    userState.RequestType = RequestType.CombineOutfit;
                    userState.Images.Clear();
                    userState.Prompt = cachedPrompt;

                    await _responseQueue.EnqueueResponseAsync(async bot =>
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Отправьте фотографии одежды, которую хотите объединить в образ. Когда закончите, нажмите /generate",
                            cancellationToken: cancellationToken));
                    return;
                }

                if (message.Text == "Подобрать образ к вещи")
                {
                    var cachedPrompt = await _databaseService.GetUserCachedPromptAsync(userId);
                    if (string.IsNullOrEmpty(cachedPrompt))
                    {
                        await _responseQueue.EnqueueResponseAsync(async bot =>
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "❌ Сначала установите промпт командой /updateprompt [ваш промпт]",
                                cancellationToken: cancellationToken));
                        return;
                    }

                    userState.CurrentState = UserStateState.WaitingForMatchingImage;
                    userState.RequestType = RequestType.MatchOutfit;
                    userState.Images.Clear();
                    userState.Prompt = cachedPrompt;

                    await _responseQueue.EnqueueResponseAsync(async bot =>
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Отправьте фотографию одежды, для которой нужно подобрать образ",
                            cancellationToken: cancellationToken));
                    return;
                }
                if (message.Text == "Установить промпт")
                {
                    userState.CurrentState = UserStateState.WaitingForPromptInput;

                    var currentPrompt = await _databaseService.GetUserCachedPromptAsync(userId);
                    var promptMessage = string.IsNullOrEmpty(currentPrompt)
                        ? "Введите ваш промпт (например: 'деловой стиль', 'повседневный образ', 'спортивный стиль'):"
                        : $"Текущий промпт: {currentPrompt}\n\nВведите новый промпт:";

                    await _responseQueue.EnqueueResponseAsync(async bot =>
                        await bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: promptMessage,
                            cancellationToken: cancellationToken));
                    return;
                }

                if (message.Text == "/generate" && userState.CurrentState == UserStateState.WaitingForOutfitImages)
                {
                    if (userState.Images.Count == 0)
                    {
                        await _responseQueue.EnqueueResponseAsync(async bot =>
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Сначала отправьте фотографии одежды.",
                                cancellationToken: cancellationToken));
                        return;
                    }

                    await _responseQueue.EnqueueResponseAsync(async bot =>
                        await ProcessFashionRequest(userState, chatId, cancellationToken));
                    return;
                }

                await _responseQueue.EnqueueResponseAsync(async bot =>
                    await ShowMainMenu(chatId, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update");
            }
        }

        private async Task ShowMainMenu(long chatId, CancellationToken cancellationToken)
        {
            var replyKeyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Объединить вещи в образ" },
                new KeyboardButton[] { "Подобрать образ к вещи" },
                new KeyboardButton[] { "Установить промпт" }
            })
            {
                ResizeKeyboard = true
            };

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите режим работы:",
                replyMarkup: replyKeyboard,
                cancellationToken: cancellationToken);
        }

        private async Task ProcessFashionRequest(UserState userState, long chatId, CancellationToken cancellationToken)
        {
            try
            {
                if (userState?.Images == null || !userState.Images.Any())
                {
                    await _botClient.SendTextMessageAsync(chatId, 
                        userState.RequestType == RequestType.CombineOutfit 
                            ? "Нет изображений для обработки" 
                            : "Необходимо отправить изображение", 
                        cancellationToken: cancellationToken);
                    return;
                }

                var request = new FashionRequest
                {
                    Id = Guid.NewGuid(),
                    UserId = userState.UserId,
                    RequestType = userState.RequestType == RequestType.CombineOutfit 
                        ? FashionRequestType.CombineOutfit 
                        : FashionRequestType.MatchOutfit,
                    Images = new List<string>(userState.Images),
                    Prompt = userState.Prompt ?? string.Empty,
                    Status = FashionRequestStatus.Queued,
                    CreatedAt = DateTime.UtcNow
                };

                try
                {
                    await _databaseService.SaveRequestAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка сохранения запроса");
                    await _botClient.SendTextMessageAsync(chatId, "Ошибка обработки запроса", cancellationToken: cancellationToken);
                    return;
                }

                await _botClient.SendTextMessageAsync(chatId, 
                    userState.RequestType == RequestType.CombineOutfit 
                        ? "Создаю образ..." 
                        : "Подбираю образ...", 
                    cancellationToken: cancellationToken);

                await _queueService.EnqueueRequestAsync(async () =>
                {
                    try
                    {
                        string resultUrl;
                        if (userState.RequestType == RequestType.CombineOutfit)
                        {
                            var prompts = await _promptService.GetOutfitGenerationPromptsAsync();
                            resultUrl = await _openAIService.GenerateImageFromClothesAsync(
                                request.Images,
                                request.Prompt,
                                prompts.SystemPrompt,
                                prompts.UserPrompt);
                        }
                        else
                        {
                            var prompts = await _promptService.GetMatchingItemsPromptsAsync();
                            resultUrl = await _openAIService.GenerateMatchingOutfitAsync(
                                request.Images.First(),
                                request.Prompt,
                                prompts.SystemPrompt,
                                prompts.UserPrompt);
                        }

                        if (string.IsNullOrEmpty(resultUrl))
                            throw new Exception("Не удалось сгенерировать изображение");

                        request.ResultUrl = resultUrl;
                        request.Status = FashionRequestStatus.Completed;
                        request.ProcessedAt = DateTime.UtcNow;

                        await _databaseService.SaveRequestAsync(request);

                        await _responseQueue.EnqueueResponseAsync(async bot => 
                            await bot.SendPhotoAsync(
                                chatId: chatId,
                                photo: resultUrl,
                                caption: userState.RequestType == RequestType.CombineOutfit 
                                    ? "Ваш образ готов!" 
                                    : "Вот подобранный образ!",
                                cancellationToken: cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка обработки запроса");
                        request.Status = FashionRequestStatus.Failed;
                        await _databaseService.SaveRequestAsync(request);
                        
                        await _responseQueue.EnqueueResponseAsync(async bot => 
                            await bot.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Ошибка при создании образа",
                                cancellationToken: cancellationToken));
                    }
                    finally
                    {
                        _userStates.TryRemove(userState.UserId, out _);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Необработанная ошибка в ProcessFashionRequest");
                await _responseQueue.EnqueueResponseAsync(async bot => 
                    await bot.SendTextMessageAsync(chatId, "Произошла ошибка", cancellationToken: cancellationToken));
            }
        }

        private async Task HandleSettingsCommand(long chatId, string messageText, CancellationToken cancellationToken)
        {
            var parts = messageText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Используйте формат:\n/settings\n[параметр]=[значение]\n[параметр]=[значение]...",
                    cancellationToken: cancellationToken);
                return;
            }

            try
            {
                var settings = await _databaseService.GetAppSettingsAsync() ?? _appSettings;
                var prompts = await _databaseService.GetPromptsAsync() ?? _appSettings.Prompts;

                for (int i = 1; i < parts.Length; i++)
                {
                    var setting = parts[i].Split('=', 2);
                    if (setting.Length != 2) continue;

                    var key = setting[0].Trim().ToLower();
                    var value = setting[1].Trim();

                    if (key.StartsWith("openai_"))
                    {
                        switch (key)
                        {
                            case "openai_max_concurrent_requests":
                                settings.OpenAISettings.MaxConcurrentRequests = int.Parse(value);
                                break;
                            case "openai_image_size":
                                settings.OpenAISettings.ImageSize = value;
                                break;
                            case "openai_image_quality":
                                settings.OpenAISettings.ImageQuality = value;
                                break;
                            case "openai_image_style":
                                settings.OpenAISettings.ImageStyle = value;
                                break;
                        }
                    }
                    else if (key.StartsWith("prompt_"))
                    {
                        switch (key)
                        {
                            case "prompt_outfit_generation_system":
                                prompts.OutfitGenerationSystemPrompt = value;
                                break;
                            case "prompt_outfit_generation_user":
                                prompts.OutfitGenerationUserPrompt = value;
                                break;
                            case "prompt_matching_items_system":
                                prompts.MatchingItemsSystemPrompt = value;
                                break;
                            case "prompt_matching_items_user":
                                prompts.MatchingItemsUserPrompt = value;
                                break;
                        }
                    }
                }

                await _databaseService.UpdateAppSettingsAsync(settings);
                await _databaseService.UpdatePromptsAsync(prompts);

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Настройки успешно обновлены",
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating settings");
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"Ошибка при обновлении настроек: {ex.Message}",
                    cancellationToken: cancellationToken);
            }
        }

        private async Task HandlePromptInput(long userId, string messageText)
        {
            var userState = _userStates.GetOrAdd(userId, id => new UserState { UserId = id });

            if (string.IsNullOrWhiteSpace(messageText))
            {
                var cachedPrompt = await _databaseService.GetUserCachedPromptAsync(userId);
                if (!string.IsNullOrEmpty(cachedPrompt))
                {
                    userState.Prompt = cachedPrompt;
                }
            }
            else
            {
                userState.Prompt = messageText;
                await _databaseService.UpdateUserCachedPromptAsync(userId, messageText);
            }
        }

        private async Task HandleUpdatePromptCommand(long chatId, string messageText, CancellationToken cancellationToken)
        {
            var userId = chatId;
            var newPrompt = messageText.Replace("/updateprompt", "").Trim();
            
            if (string.IsNullOrWhiteSpace(newPrompt))
            {
                var currentPrompt = await _databaseService.GetUserCachedPromptAsync(userId);
                var message = string.IsNullOrEmpty(currentPrompt)
                    ? "Введите ваш промпт (например: 'деловой стиль', 'повседневный образ'):"
                    : $"Текущий промпт: {currentPrompt}\n\nВведите новый промпт:";
                
                // Устанавливаем состояние ожидания промпта
                if (_userStates.TryGetValue(userId, out var userState))
                {
                    userState.CurrentState = UserStateState.WaitingForPromptInput;
                }
                
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: message,
                    cancellationToken: cancellationToken);
                return;
            }

            await _databaseService.UpdateUserCachedPromptAsync(userId, newPrompt);
            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"✅ Промпт сохранен: {newPrompt}",
                cancellationToken: cancellationToken);
            
            await ShowMainMenu(chatId, cancellationToken);
        }
    }

    public class UserState
    {
        public long UserId { get; set; }
        public UserStateState CurrentState { get; set; }
        public RequestType RequestType { get; set; }
        public List<string> Images { get; set; } = new();
        public string Prompt { get; set; } = string.Empty;
    }

    public enum UserStateState
    {
        Idle,
        WaitingForOutfitPrompt,
        WaitingForOutfitImages,
        WaitingForMatchingPrompt,
        WaitingForMatchingImage,
        WaitingForPromptInput
    }

    public enum RequestType
    {
        CombineOutfit,
        MatchOutfit
    }
    public class BotBackgroundService : BackgroundService
    {
        private readonly ILogger<BotBackgroundService> _logger;
        private readonly ITelegramBotClient _botClient;
        private readonly TelegramBotHandler _botHandler;
        private readonly IRequestQueueService _queueService;
        private readonly ITelegramResponseQueue _responseQueue;

        public BotBackgroundService(
            ILogger<BotBackgroundService> logger,
            ITelegramBotClient botClient,
            TelegramBotHandler botHandler,
            IRequestQueueService queueService,
            ITelegramResponseQueue responseQueue)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _botHandler = botHandler ?? throw new ArgumentNullException(nameof(botHandler));
            _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
            _responseQueue = responseQueue ?? throw new ArgumentNullException(nameof(responseQueue));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _botClient.StartReceiving(
                updateHandler: _botHandler.HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions
                {
                    ThrowPendingUpdates = true,
                    AllowedUpdates = Array.Empty<UpdateType>()
                },
                cancellationToken: stoppingToken);

            _logger.LogInformation("Bot started receiving updates");

            var queueTasks = new[]
            {
                _queueService.ProcessQueueAsync(stoppingToken),
                _responseQueue.ProcessQueueAsync(stoppingToken)
            };

            await Task.WhenAll(queueTasks);
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "Telegram polling error");
            return Task.CompletedTask;
        }
    }
    

     
}