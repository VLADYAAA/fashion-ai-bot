using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FashionBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FashionBot.Services
{

    public class RequestQueueService : IRequestQueueService, IDisposable
    {
        private readonly ConcurrentQueue<Func<Task>> _requestQueue = new();
        private readonly SemaphoreSlim _queueSemaphore = new(1);
        private readonly ILogger<RequestQueueService> _logger;
        private readonly IDatabaseService _databaseService;

        private RateLimitSettings _rateLimitSettings;
        private DateTime _lastRequestTime = DateTime.MinValue;
        private int _requestsThisMinute = 0;
        private readonly Timer _rateLimitTimer;
        private bool _disposed;

        public RequestQueueService(
            IDatabaseService databaseService,
            ILogger<RequestQueueService> logger)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _rateLimitTimer = new Timer(ResetRateLimitCounter, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                var settings = await _databaseService.GetAppSettingsAsync();
                _rateLimitSettings = settings?.RateLimitSettings ?? new RateLimitSettings();
                _logger.LogInformation("Rate limit initialized: {RequestsPerMinute} req/min, {MaxConcurrent} concurrent",
                    _rateLimitSettings.RequestsPerMinute, _rateLimitSettings.MaxConcurrentRequests);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize rate limit settings");
                _rateLimitSettings = new RateLimitSettings
                {
                    RequestsPerMinute = 20,
                    MaxConcurrentRequests = 5
                };
            }
        }

        private void ResetRateLimitCounter(object? state)
        {
            _requestsThisMinute = 0;
            _logger.LogDebug("Rate limit counter reset");
        }

        public async Task EnqueueRequestAsync(Func<Task> requestTask)
        {
            if (requestTask == null) throw new ArgumentNullException(nameof(requestTask));

            await _queueSemaphore.WaitAsync();
            try
            {
                _requestQueue.Enqueue(requestTask);
                _logger.LogDebug("Request enqueued. Queue size: {QueueSize}", _requestQueue.Count);
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }

        public async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting queue processing");

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    if (_requestsThisMinute >= _rateLimitSettings.RequestsPerMinute)
                    {
                        var delay = TimeSpan.FromMinutes(1) - (DateTime.UtcNow - _lastRequestTime);
                        if (delay > TimeSpan.Zero)
                        {
                            _logger.LogDebug("Rate limit reached. Waiting {Delay}...", delay);
                            await Task.Delay(delay, cancellationToken);
                        }
                        continue;
                    }

                    Func<Task>? requestTask = null;
                    await _queueSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (_requestQueue.TryDequeue(out requestTask))
                        {
                            _requestsThisMinute++;
                            _lastRequestTime = DateTime.UtcNow;
                        }
                    }
                    finally
                    {
                        _queueSemaphore.Release();
                    }

                    if (requestTask != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogDebug("Processing request...");
                                await requestTask();
                                _logger.LogDebug("Request processed successfully");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error processing request");
                            }
                        }, cancellationToken);
                    }

                    var requestDelay = TimeSpan.FromMilliseconds(
                        60000.0 / Math.Max(1, _rateLimitSettings.RequestsPerMinute));
                    await Task.Delay(requestDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Queue processing cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in queue processing");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            _logger.LogInformation("Queue processing stopped");
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _rateLimitTimer?.Dispose();
            _queueSemaphore?.Dispose();
            _logger.LogInformation("RequestQueueService disposed");
        }
    }
}