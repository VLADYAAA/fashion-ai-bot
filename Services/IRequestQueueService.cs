using System;
using System.Threading.Tasks;

namespace FashionBot.Services
{
public interface IRequestQueueService
    {
        Task EnqueueRequestAsync(Func<Task> requestTask);
        Task ProcessQueueAsync(CancellationToken cancellationToken);
    }
}