using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using OtantikPos.Inventory.Application;
using OtantikPos.Node.Api.Auth;
using OtantikPos.Node.Api.Controllers;
using OtantikPos.Node.Api.ErrorHandling;
using OtantikPos.Node.Api.Hosting;
using OtantikPos.Node.Api.Hubs;
using OtantikPos.Node.Infrastructure;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Ordering.Application;
using OtantikPos.Ordering.Application.Ports;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOrderingApplication();
builder.Services.AddInventoryApplication();
builder.Services.AddNodeInfrastructure(builder.Configuration);
builder.Services.AddTillAuthentication(builder.Configuration);
builder.Services.AddSingleton<ITillNotifier, TillNotifier>();
builder.Services.AddHostedService<DeliverySystemLinkBroadcaster>();
builder.Services.AddHostedService<PrinterStatusBroadcaster>();

// Enums travel as names ("DineIn", "Cash", "AfterPayment"), as the delivery system's API sends
// them, over REST and the hub alike.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ApplicationExceptionHandler.Customize);
builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();

// As in the delivery system: /health says the process is up, /health/ready that it can reach
// its database too.
builder.Services.AddHealthChecks().AddDbContextCheck<NodeDbContext>(name: "database", tags: ["ready"]);

// Per device, on top of StaffDirectory's per-account lockout: one tablet cannot cycle through
// PINs across every account on the sign-in screen. Behind Docker Desktop (a Windows or Mac till
// machine) every device reaches the till from the same address, so it is then one limit for the
// whole restaurant; Docker on Linux keeps them apart. 20, not 10, so a few mistyped PINs at shift
// change do not make every tablet wait; the per-account lockout is what stops PIN guessing.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { errors = new[] { "Too many sign-in attempts. Wait a minute and try again." } }, cancellationToken);
    };
    o.AddPolicy(AuthController.SignInRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Only needed when the Angular app is served from somewhere else, such as `ng serve` during
// development. Credentials are allowed because the SignalR client negotiates with them.
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

await app.PrepareDatabaseAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Before authentication: the page itself is public, and only the API behind it needs a
// sign-in.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = SpaHosting.SetCacheHeaders });

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.MapControllers();

// A till's connection closes when its token expires, so a push never outlives the sign-in.
app.MapHub<TillHub>(TillHub.Path, o => o.CloseOnAuthenticationExpiration = true);

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

app.MapFallback(SpaHosting.ServeIndexAsync).AllowAnonymous();

app.Run();
