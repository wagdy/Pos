using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Infrastructure.Identity;
using OtantikPos.Node.Infrastructure.Persistence;

namespace OtantikPos.Node.Api.Hosting;

internal static class DatabaseStartup
{
    // Runs before the app listens, and before the outbox, printers and cloud sync start.
    //
    // Migrating on startup suits this deployment: one API and one database on the restaurant's
    // own machine, updated by installing a new build, with nobody on hand to run a migration
    // step separately. Set Database:MigrateOnStartup to false where something else applies
    // them.
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();

        if (app.Configuration.GetValue("Database:MigrateOnStartup", defaultValue: true))
            await scope.ServiceProvider.GetRequiredService<NodeDbContext>().Database.MigrateAsync();

        var bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value.BootstrapManager;
        await scope.ServiceProvider.GetRequiredService<StaffDirectory>()
            .EnsureBootstrapManagerAsync(bootstrap.Name, bootstrap.Pin, CancellationToken.None);
    }
}
