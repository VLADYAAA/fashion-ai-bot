using System.Collections.Concurrent;
using FashionBot.Handlers;
using FashionBot.Models;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
namespace FashionBot.Services
{
    public class TelegramResponseQueue : ITelegramResponseQueue, IDisposable
    {
        private readonly ConcurrentQueue<Func<ITelegramBotClient, Task>> _responseQueue = new();
        private readonly SemaphoreSlim _queueSemaphore = new(1);
        private readonly ILogger<TelegramResponseQueue> _logger;
        private readonly ITelegramBotClient _botClient;
        private bool _disposed;
        private readonly Timer _rateLimitTimer;
        private int _responsesThisSecond = 0;
        private readonly RateLimitSettings _rateLimitSettings = new RateLimitSettings();

        public TelegramResponseQueue(
            ITelegramBotClient botClient,
            ILogger<TelegramResponseQueue> logger)
        {
            _botClient = botClient;
            _logger = logger;
            _rateLimitTimer = new Timer(ResetRateLimitCounter, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private void ResetRateLimitCounter(object? state) => _responsesThisSecond = 0;

        public async Task EnqueueResponseAsync(Func<ITelegramBotClient, Task> responseTask)
        {
            await _queueSemaphore.WaitAsync();
            try
            {
                _responseQueue.Enqueue(responseTask);
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }

        public async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    if (_responsesThisSecond >= 20)
                    {
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    Func<ITelegramBotClient, Task> responseTask = null;
                    await _queueSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (_responseQueue.TryDequeue(out responseTask))
                        {
                            _responsesThisSecond++;
                        }
                    }
                    finally
                    {
                        _queueSemaphore.Release();
                    }

                    if (responseTask != null)
                    {
                        try
                        {
                            await responseTask(_botClient);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing Telegram response");
                        }
                    }

                    await Task.Delay(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in response queue processing");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rateLimitTimer?.Dispose();
            _queueSemaphore?.Dispose();
        }
    }
}
