using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Inventory.Application;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Node.Infrastructure.Tests;

// A restaurant machine, wired exactly as the API will wire it, with every hosted service
// running: outbox, print worker, cloud listener, reference-data sync. Each test gets its own
// database, created and dropped around it.
public sealed class TestNode : IAsyncDisposable
{
    private IHost _host = null!;
    private string _connectionString = null!;

    public FakeDeliverySystem Cloud { get; private set; } = new();
    public FakePrinter Kitchen { get; } = new();
    public FakePrinter Receipts { get; } = new();
    public MutableUser User { get; } = new() { UserId = "staff-cashier", Role = UserRole.Cashier };

    public static string? DatabaseServer => Environment.GetEnvironmentVariable("OTANTIKPOS_TEST_DB");

    // beforeNode runs once the fake cloud is up and before the node starts, for state the cloud
    // must already hold when the node first connects.
    public static async Task<TestNode> StartAsync(Action<FakeDeliverySystem>? beforeNode = null)
    {
        if (DatabaseServer is null)
            Assert.Skip("Set OTANTIKPOS_TEST_DB to a PostgreSQL 15+ connection string (without Database=) to run the integration tests.");

        var node = new TestNode();
        await node.Cloud.StartAsync();
        beforeNode?.Invoke(node.Cloud);
        node._connectionString = $"{DatabaseServer};Database=otantik_test_{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OtantikPos"] = node._connectionString,
            ["DeliverySystem:BaseUrl"] = node.Cloud.BaseUrl,
            ["DeliverySystem:NodeKey"] = FakeDeliverySystem.NodeKey,
            ["Printing:Printers:Kitchen:Host"] = "127.0.0.1",
            ["Printing:Printers:Kitchen:Port"] = node.Kitchen.Port.ToString(),
            ["Printing:Printers:Receipt:Host"] = "127.0.0.1",
            ["Printing:Printers:Receipt:Port"] = node.Receipts.Port.ToString(),
        });
        builder.Services.AddOrderingApplication();
        builder.Services.AddInventoryApplication();
        builder.Services.AddNodeInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<ICurrentUser>(node.User);
        builder.Services.AddSingleton<ITillNotifier, NoTills>();
        node._host = builder.Build();

        await using (var scope = node._host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NodeDbContext>().Database.MigrateAsync();

        await node._host.StartAsync();
        return node;
    }

    // The delivery system going away and coming back at the same address, as after an outage.
    // `beforeStart` sets up what the new one serves.
    public async Task RestartCloudAsync(Action<FakeDeliverySystem> beforeStart)
    {
        var address = Cloud.BaseUrl;
        await Cloud.DisposeAsync();
        Cloud = new FakeDeliverySystem(address);
        beforeStart(Cloud);
        await Cloud.StartAsync();
    }

    public TestNode As(string userId, UserRole role)
    {
        User.UserId = userId;
        User.Role = role;
        return this;
    }

    public async Task<T> Send<T>(IRequest<T> request)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    public async Task Send(IRequest request)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    public async Task<T> Db<T>(Func<NodeDbContext, Task<T>> query)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NodeDbContext>());
    }

    public T Service<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public IServiceScope Scope() => _host.Services.CreateScope();

    // Timed on the Stopwatch, not the wall clock: the wall clock jumps across a laptop's sleep,
    // and a wait that began before it would give up at once on waking.
    public static async Task<bool> WaitFor(Func<Task<bool>> condition, int seconds = 15)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await Cloud.DisposeAsync();
        Kitchen.Dispose();
        Receipts.Dispose();

        var options = new DbContextOptionsBuilder<NodeDbContext>().UseNpgsql(_connectionString).Options;
        await using var db = new NodeDbContext(options, new Common.OutboxSignal());
        await db.Database.EnsureDeletedAsync();
    }

    private sealed class NoTills : ITillNotifier
    {
        public Task OrderChangedAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class MutableUser : ICurrentUser
{
    public string UserId { get; set; } = string.Empty;
    public UserRole Role { get; set; }
}
