using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Api.Controllers;
using OtantikPos.Node.Infrastructure.Common;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Node.Infrastructure.Tests;

namespace OtantikPos.Node.Api.Tests;

// The restaurant machine's API exactly as Program.cs builds it, in process, on its own
// database, connected to a fake delivery system and two fake printers. On start it has
// migrated, created the bootstrap manager "Boss", and synced the menu and staff (Sara the
// cashier, Omar the manager, Hany the captain; none with a PIN yet).
public sealed class TestApi : IAsyncDisposable
{
    public const string BossPin = "9999";
    public const string CashierId = "staff-cashier", CashierPin = "2468";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private WebApplicationFactory<Program> _factory = null!;
    private string _connectionString = null!;

    public FakeDeliverySystem Cloud { get; } = new();
    public FakePrinter Kitchen { get; } = new();
    public FakePrinter Receipts { get; } = new();

    public static string? DatabaseServer => Environment.GetEnvironmentVariable("OTANTIKPOS_TEST_DB");

    public IServiceProvider Services => _factory.Services;

    public static async Task<TestApi> StartAsync()
    {
        if (DatabaseServer is null)
            Assert.Skip("Set OTANTIKPOS_TEST_DB to a PostgreSQL 15+ connection string (without Database=) to run the API tests.");

        var api = new TestApi();
        await api.Cloud.StartAsync();
        api._connectionString = $"{DatabaseServer};Database=otantik_api_test_{Guid.NewGuid():N}";

        // UseSetting, not ConfigureAppConfiguration: Program reads the connection string and the
        // signing key while building, before anything added later would be seen. "Testing", so
        // appsettings.Development.json and its real database stay out of it.
        api._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:OtantikPos", api._connectionString);
            web.UseSetting("Auth:SigningKey", "test-only-signing-key-0123456789-0123456789");
            web.UseSetting("Auth:BootstrapManager:Name", "Boss");
            web.UseSetting("Auth:BootstrapManager:Pin", BossPin);
            web.UseSetting("DeliverySystem:BaseUrl", api.Cloud.BaseUrl);
            web.UseSetting("DeliverySystem:NodeKey", FakeDeliverySystem.NodeKey);
            web.UseSetting("Printing:Printers:Kitchen:Host", "127.0.0.1");
            web.UseSetting("Printing:Printers:Kitchen:Port", api.Kitchen.Port.ToString());
            web.UseSetting("Printing:Printers:Receipt:Host", "127.0.0.1");
            web.UseSetting("Printing:Printers:Receipt:Port", api.Receipts.Port.ToString());
            web.ConfigureLogging(logging => logging.ClearProviders());
        });

        // Starts the host: migration, the bootstrap manager, then every background worker.
        _ = api._factory.Server;

        Assert.True(await WaitFor(() => api.Db(db => db.Staff.AnyAsync(s => s.Id == CashierId))), "The menu and staff never arrived from the fake delivery system.");
        return api;
    }

    public HttpClient Anonymous() => _factory.CreateClient();

    public async Task<HttpClient> SignInAsync(string staffId, string pin)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest(staffId, pin));
        Assert.True(response.IsSuccessStatusCode, $"Sign-in as {staffId} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var signedIn = (await response.Content.ReadFromJsonAsync<SignInResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.Token);
        return client;
    }

    public async Task<HttpClient> ManagerAsync()
    {
        var staff = await Anonymous().GetFromJsonAsync<List<SignInOption>>("/api/auth/staff", Json);
        return await SignInAsync(staff!.Single(s => s.FullName == "Boss").Id, BossPin);
    }

    // Sara, given a PIN by the manager the way a restaurant would.
    public async Task<HttpClient> CashierAsync(HttpClient manager)
    {
        var response = await manager.PutAsJsonAsync($"/api/staff/{CashierId}/pin", new SetPinRequest(CashierPin));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await SignInAsync(CashierId, CashierPin);
    }

    // A till's live connection, as the Angular app opens it: a WebSocket with the token in the
    // query string, since a browser cannot put it in a header there.
    public async Task<HubConnection> ConnectTillAsync(HttpClient signedIn, ConcurrentQueue<Order> received, ConcurrentQueue<DeliverySystemLinkStatus>? links = null)
    {
        var token = signedIn.DefaultRequestHeaders.Authorization!.Parameter;
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, $"/hubs/till?access_token={token}"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (context, cancellationToken) => await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        connection.On<Order>("OrderChanged", received.Enqueue);
        if (links is not null)
            connection.On<DeliverySystemLinkStatus>("DeliverySystemLinkChanged", links.Enqueue);
        await connection.StartAsync();
        return connection;
    }

    // What the reference-data sync does when the delivery system's staff or menu change.
    public async Task ApplyReferenceDataAsync(ReferenceData data)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReferenceDataApplier>().ApplyAsync(data, DateTime.UtcNow, CancellationToken.None);
    }

    public async Task<T> Db<T>(Func<NodeDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NodeDbContext>());
    }

    // On the Stopwatch, which a laptop's sleep does not move; see the Infrastructure tests.
    public static async Task<bool> WaitFor(Func<Task<bool>> condition, int seconds = 15)
    {
        var waited = Stopwatch.StartNew();
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
        await _factory.DisposeAsync();
        await Cloud.DisposeAsync();
        Kitchen.Dispose();
        Receipts.Dispose();

        var options = new DbContextOptionsBuilder<NodeDbContext>().UseNpgsql(_connectionString).Options;
        await using var db = new NodeDbContext(options, new OutboxSignal());
        await db.Database.EnsureDeletedAsync();
    }
}

public static class HttpAssertions
{
    // The response as the shared Order, or a failure that shows what the API said instead.
    public static async Task<Order> OrderFrom(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<Order>(TestApi.Json))!;
    }

    // What the till would show: errors[0] of the ProblemDetails.
    public static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? errors[0].GetString()
            : null;
    }
}
