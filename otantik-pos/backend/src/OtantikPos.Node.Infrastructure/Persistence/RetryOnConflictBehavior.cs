using MediatR;
using Otantik.BuildingBlocks;

namespace OtantikPos.Node.Infrastructure.Persistence;

// Runs an IRetryOnConflict request again, from a clean change tracker, when its save collides
// with another; a few times, a moment apart. Anything else, and a conflict that persists, goes
// back to the caller as before.
internal sealed class RetryOnConflictBehavior<TRequest, TResponse>(NodeDbContext db) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const int Attempts = 3;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IRetryOnConflict)
            return await next(cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await next(cancellationToken);
            }
            catch (ConflictException) when (attempt < Attempts)
            {
                // What was loaded is stale: the handler reads it all again.
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }
}
