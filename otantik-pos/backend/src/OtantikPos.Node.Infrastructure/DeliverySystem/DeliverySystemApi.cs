using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Catalog;
using Otantik.SharedKernel.Identity;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Node.Infrastructure.DeliverySystem;

public sealed class DeliverySystemOptions
{
    public const string Section = "DeliverySystem";

    // The delivery system's base URL. Empty means this till runs standalone: no customer
    // lookups, no points, no captain orders, and nothing synced.
    public string BaseUrl { get; set; } = string.Empty;

    // This machine's key, issued by the delivery system and sent as X-Pos-Node-Key on every
    // request and on the SignalR connection. Treat it as a password.
    public string NodeKey { get; set; } = string.Empty;

    public string HubPath { get; set; } = "/hubs/pos-sync";

    public int ReferenceDataRefreshMinutes { get; set; } = 5;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

// The menu, tax rate and staff list as the delivery system sends them. The menu travels as the
// shared catalog classes themselves.
public sealed record ReferenceData(
    decimal TaxPercentage,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<SubCategory> SubCategories,
    IReadOnlyList<AddOn> AddOns,
    IReadOnlyList<MenuItem> MenuItems,
    IReadOnlyList<StaffAccount> Staff,
    // LoyaltySettings.RedemptionValuePer100Points. Null from a delivery system that does not
    // send it yet, which the till reads as the default, Points / 10.
    decimal? RedemptionValuePer100Points = null);

public sealed record StaffAccount(string Id, string FullName, UserRole Role, bool IsActive);

// The till's whole conversation with the delivery system over HTTP. The endpoints below are the
// contract the delivery system implements (see shared/README.md):
//
//   GET  api/pos-sync/customers/by-phone/{phone}     CustomerProfile, or 404
//   GET  api/pos-sync/customers/{userId}/points      { "points": n }
//   POST api/pos-sync/loyalty/redemptions            { customerUserId, points, orderPublicId }:
//                                                    204, or 409 when the balance is short;
//                                                    idempotent per orderPublicId
//   POST api/pos-sync/loyalty/refunds                { refundId, customerUserId, orderPublicId,
//                                                      points, refundedAmount }: 204; gives
//                                                    back `points`; idempotent per refundId.
//                                                    What the refund earned is taken back
//                                                    when the refunded order is PUT
//   PUT  api/pos-sync/orders/{publicId}              the shared Order, upserted by PublicId
//   GET  api/pos-sync/orders?changedSince={utc}      Order[] changed since then
//   GET  api/pos-sync/reference-data                 ReferenceData
//
// Every failure to reach the cloud (no network, timeout, 5xx, circuit open) surfaces as
// DeliverySystemUnavailableException, which the Application layer already treats as "offline".
public sealed class DeliverySystemApi(HttpClient http, IOptions<DeliverySystemOptions> options) : ICustomerDirectory, ILoyaltyGateway
{
    public const string NodeKeyHeader = "X-Pos-Node-Key";

    // The delivery system sends enums as names, over REST and SignalR alike; its SignalR
    // messages used to send numbers. This converter reads both.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<CustomerProfile?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/pos-sync/customers/by-phone/{Uri.EscapeDataString(phoneNumber)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CustomerProfile>(Json, cancellationToken);
    }

    public async Task<int> GetBalanceAsync(string customerUserId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/pos-sync/customers/{Uri.EscapeDataString(customerUserId)}/points", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PointsBalance>(Json, cancellationToken))!.Points;
    }

    public async Task RedeemAsync(string customerUserId, int points, Guid orderPublicId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/pos-sync/loyalty/redemptions",
            new { customerUserId, points, orderPublicId }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RefundAsync(string customerUserId, Guid orderPublicId, Guid refundId, int points, decimal refundedAmount, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/pos-sync/loyalty/refunds",
            new { refundId, customerUserId, orderPublicId, points, refundedAmount }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task PushOrderAsync(Order order, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, $"api/pos-sync/orders/{order.PublicId}", order, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<Order>> GetOrdersChangedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var since = Uri.EscapeDataString(sinceUtc.ToString("O", CultureInfo.InvariantCulture));
        using var response = await SendAsync(HttpMethod.Get, $"api/pos-sync/orders?changedSince={since}", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<Order>>(Json, cancellationToken) ?? [];
    }

    public async Task<ReferenceData> GetReferenceDataAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/pos-sync/reference-data", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ReferenceData>(Json, cancellationToken))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
            throw new DeliverySystemUnavailableException("This till is not connected to a delivery system (DeliverySystem:BaseUrl is empty).");

        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Json),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new DeliverySystemUnavailableException("The delivery system cannot be reached.", ex);
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            response.Dispose();
            throw new DeliverySystemUnavailableException($"The delivery system is not responding properly ({(int)response.StatusCode}).");
        }

        // A wrong or revoked key is a setup problem, not an outage: worth a loud error rather
        // than a quiet "offline".
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new InvalidOperationException($"The delivery system refused this till's key ({DeliverySystemOptions.Section}:NodeKey).");
        }

        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = await ReadErrorAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new ConflictException(message ?? "The delivery system refused the change.");

        throw new InvalidOperationException($"The delivery system answered {(int)response.StatusCode}: {message}");
    }

    // The delivery system's own error shape: { "errors": ["..."] }.
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, cancellationToken);
            return body?.Errors?.FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PointsBalance(int Points);

    private sealed record ErrorBody(List<string>? Errors);
}
