using System;
using System.Threading;
using System.Threading.Tasks;
using FashionBot.Handlers;
using FashionBot.Services;
using FashionBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
namespace FashionBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var host = Host.CreateDefaultBuilder(args)
                    .ConfigureAppConfiguration((hostingContext, config) =>
                    {
                        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                        config.AddEnvironmentVariables();
                        if (hostingContext.HostingEnvironment.IsDevelopment())
                        {
                            config.AddUserSecrets<Program>();
                        }
                    })
                    .ConfigureServices((context, services) =>
                    {
                        var configuration = context.Configuration;

                        var dbSettings = configuration.GetSection("DatabaseSettings").Get<DatabaseSettings>();
                        if (string.IsNullOrWhiteSpace(dbSettings?.ConnectionString))
                            throw new InvalidOperationException("Database connection string is not configured");

                        var openAiSettings = configuration.GetSection("OpenAISettings").Get<OpenAISettings>();
                        if (string.IsNullOrWhiteSpace(openAiSettings?.BaseUrl))
                            throw new InvalidOperationException("OpenAI BaseUrl is not configured");
                        if (string.IsNullOrWhiteSpace(openAiSettings.ApiKey))
                            throw new InvalidOperationException("OpenAI ApiKey is not configured");

                        services.Configure<AppSettings>(configuration);

                        services.AddSingleton<IDatabaseService>(provider =>
                            new DatabaseService(
                                Options.Create(dbSettings),
                                provider.GetRequiredService<ILogger<DatabaseService>>()));

                        services.AddSingleton<IOpenAIService>(provider =>
                            new OpenAIService(
                                Options.Create(openAiSettings),
                                provider.GetRequiredService<ILogger<OpenAIService>>()));

                        services.AddSingleton<IRequestQueueService, RequestQueueService>();
                        services.AddSingleton<ITelegramResponseQueue, TelegramResponseQueue>();
                        
                        services.AddSingleton<IPromptService>(provider => 
                            new PromptService(
                                provider.GetRequiredService<IDatabaseService>(),
                                provider.GetRequiredService<ILogger<PromptService>>(),
                                provider.GetRequiredService<IOptions<AppSettings>>()));
                        services.AddSingleton<TelegramBotHandler>();

                        services.AddHttpClient("telegram_bot_client")
                            .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
                            {
                                var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
                                if (string.IsNullOrWhiteSpace(settings.TelegramBotSettings.BotToken))
                                    throw new InvalidOperationException("Telegram bot token is not configured");
                                return new TelegramBotClient(settings.TelegramBotSettings.BotToken, httpClient);
                            });

                        services.AddHostedService<BotBackgroundService>();
                    })
                    .Build();

                var dbService = host.Services.GetRequiredService<IDatabaseService>();
                await dbService.InitializeDatabaseAsync();

                Console.WriteLine("Starting bot...");
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Application startup failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }
    }
}