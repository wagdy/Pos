using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OtantikPos.Node.Infrastructure.Common;

namespace OtantikPos.Node.Infrastructure.Persistence;

// Used only by `dotnet ef`, so migrations can be added before the API exists:
//   dotnet ef migrations add <Name> --project otantik-pos/backend/src/OtantikPos.Node.Infrastructure --output-dir Persistence/Migrations
// Adding a migration never connects to the database. Applying one reads the connection string
// from OTANTIKPOS_CONNECTION.
internal sealed class NodeDbContextFactory : IDesignTimeDbContextFactory<NodeDbContext>
{
    public NodeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("OTANTIKPOS_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=otantik_pos_node;Username=postgres;Password=postgres";

        return new NodeDbContext(new DbContextOptionsBuilder<NodeDbContext>().UseNpgsql(connectionString).Options, new OutboxSignal());
    }
}
