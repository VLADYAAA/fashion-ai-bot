using Telegram.Bot;

namespace FashionBot.Services
{
    public interface ITelegramResponseQueue
    {
        Task EnqueueResponseAsync(Func<ITelegramBotClient, Task> responseTask);
        Task ProcessQueueAsync(CancellationToken cancellationToken);
    }
}