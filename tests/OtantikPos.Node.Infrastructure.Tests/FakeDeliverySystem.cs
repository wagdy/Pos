using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Node.Infrastructure.DeliverySystem;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Node.Infrastructure.Tests;

// The delivery system's side of the pos-sync contract, served for real over HTTP and SignalR,
// so the till's client, listener and sync are exercised against a live server rather than
// mocks.
public sealed class FakeDeliverySystem : IAsyncDisposable
{
    public const string NodeKey = "test-node-key";
    public const int Burger = 1, Shawarma = 10, KiloTray = 101, Cheese = 7;

    private readonly WebApplication _app;

    // Answers 503 to everything while true: the cloud is "down".
    public volatile bool Down;

    public ConcurrentDictionary<Guid, string> PushedOrders { get; } = new();
    public ConcurrentDictionary<string, CustomerProfile> Customers { get; } = new();
    public ConcurrentDictionary<string, int> Balances { get; } = new();
    public ConcurrentDictionary<Guid, int> Redemptions { get; } = new();
    public ConcurrentDictionary<Guid, LoyaltyRefund> Refunds { get; } = new();
    public List<Order> ChangedOrders { get; } = new();

    // What GET reference-data serves: the menu, tax rate and staff. A test changes it to play an
    // admin editing the menu.
    public ReferenceData Reference { get; set; } = ReferenceData();

    // A free port unless told otherwise; the DevCloud tool runs it on a fixed one.
    public FakeDeliverySystem(string urls = "http://127.0.0.1:0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(urls);
        // The delivery system's REST API sends enum names; its hub sends numbers.
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<HubConnections>();
        _app = builder.Build();

        _app.Use(async (context, next) =>
        {
            if (Down)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            var key = context.Request.Headers[DeliverySystemApi.NodeKeyHeader].ToString();
            if (key != NodeKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

        _app.MapGet("/api/pos-sync/reference-data", () => Reference);
        _app.MapGet("/api/pos-sync/customers/by-phone/{phone}", (string phone) =>
            Customers.TryGetValue(phone, out var profile) ? Results.Ok(profile) : Results.NotFound());
        _app.MapGet("/api/pos-sync/customers/{userId}/points", (string userId) => Results.Ok(new { points = Balances.GetValueOrDefault(userId) }));
        _app.MapPost("/api/pos-sync/loyalty/redemptions", (Redemption request) =>
        {
            if (Redemptions.ContainsKey(request.OrderPublicId))
                return Results.NoContent();
            if (Balances.GetValueOrDefault(request.CustomerUserId) < request.Points)
                return Results.Conflict(new { errors = new[] { "The customer no longer has enough points." } });
            Balances[request.CustomerUserId] -= request.Points;
            Redemptions[request.OrderPublicId] = request.Points;
            return Results.NoContent();
        });
        _app.MapPost("/api/pos-sync/loyalty/refunds", (LoyaltyRefund refund) =>
        {
            // Once per refund id, however often the till's outbox sends it.
            if (Refunds.TryAdd(refund.RefundId, refund))
                Balances.AddOrUpdate(refund.CustomerUserId, refund.Points, (_, balance) => balance + refund.Points);
            return Results.NoContent();
        });
        _app.MapPut("/api/pos-sync/orders/{publicId:guid}", async (Guid publicId, HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            PushedOrders[publicId] = await reader.ReadToEndAsync();
            return Results.NoContent();
        });
        _app.MapGet("/api/pos-sync/orders", (DateTime changedSince) => ChangedOrders.Where(o => o.UpdatedAt >= changedSince).ToList());
        _app.MapHub<PosSyncHub>("/hubs/pos-sync");
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task StartAsync()
    {
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    // What a captain submitting an order in the delivery app would cause.
    public Task BroadcastAsync(Order order) =>
        _app.Services.GetRequiredService<IHubContext<PosSyncHub>>().Clients.All.SendAsync("OrderChanged", order);

    public int ConnectedTills => _app.Services.GetRequiredService<HubConnections>().Count;

    public static ReferenceData ReferenceData()
    {
        var cheese = new AddOn { Id = Cheese, Name = "Cheese", Price = 15 };
        return new ReferenceData(
            14m,
            [new Category { Id = 1, Name = "Mains" }],
            [new SubCategory { Id = 11, Name = "Grill", CategoryId = 1 }],
            [cheese],
            [
                new MenuItem
                {
                    Id = Burger, Name = "Burger", Price = 120, SubCategoryId = 11,
                    MenuItemAddOns = [new MenuItemAddOn { MenuItemId = Burger, AddOnId = Cheese, AddOn = cheese }],
                },
                new MenuItem
                {
                    Id = Shawarma, Name = "Shawarma", Price = 0, SubCategoryId = 11,
                    Variants = [new MenuItemVariant { Id = KiloTray, MenuItemId = Shawarma, Name = "Kilo tray", Price = 400 }],
                },
            ],
            [
                new StaffAccount("staff-cashier", "Sara", UserRole.Cashier, true),
                new StaffAccount("staff-manager", "Omar", UserRole.Manager, true),
                new StaffAccount("staff-captain", "Hany", UserRole.CaptainOrder, true),
            ]);
    }

    // The delivery system going away: every connection to it drops.
    public Task StopAsync() => _app.StopAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    public sealed record LoyaltyRefund(Guid RefundId, string CustomerUserId, Guid OrderPublicId, int Points, decimal RefundedAmount);

    private sealed record Redemption(string CustomerUserId, int Points, Guid OrderPublicId);

    // Per fake, not static: a static count carries a previous test's till that has not finished
    // disconnecting, and a test would broadcast before its own till is listening.
    private sealed class HubConnections
    {
        public int Count;
    }

    private sealed class PosSyncHub(HubConnections connections) : Hub
    {
        public override Task OnConnectedAsync()
        {
            Interlocked.Increment(ref connections.Count);
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            Interlocked.Decrement(ref connections.Count);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
