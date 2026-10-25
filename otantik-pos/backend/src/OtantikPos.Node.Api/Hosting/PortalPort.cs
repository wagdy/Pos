namespace OtantikPos.Node.Api.Hosting;

// The employee portal's own port (Portal:Port), the one the restaurant's Wi-Fi reaches: phones
// sign in there and read their own portal, and nothing else of the till answers. The rest of the
// API, the hub, the attendance device's paths and the health checks are not found there; the
// app's files are, for the portal's screens, and any other screen of the app opens the portal's
// sign-in instead.
internal static class PortalPort
{
    public const string Setting = "Portal:Port";

    private static readonly string[] Screens = ["/portal", "/my-profile"];

    public static int? Of(IConfiguration configuration) => configuration.GetValue<int?>(Setting);

    public static bool Allows(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/api"))
            return path.StartsWithSegments("/api/portal") || path.Equals("/api/auth/employee-login", StringComparison.OrdinalIgnoreCase);
        if (path.StartsWithSegments("/hubs") || path.StartsWithSegments("/iclock") || path.StartsWithSegments("/health") || path.StartsWithSegments("/openapi"))
            return false;
        return HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
    }

    // A screen of the app other than the portal's (its address alone, /costing/waste): not a file,
    // which has an extension, and not the API.
    public static bool IsOtherScreen(HttpRequest request)
    {
        var path = request.Path;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || path.StartsWithSegments("/api"))
            return false;
        if (Path.HasExtension(path.Value))
            return false;
        return !Screens.Any(screen => path.StartsWithSegments(screen, StringComparison.OrdinalIgnoreCase));
    }

    public static IApplicationBuilder UsePortalPort(this IApplicationBuilder app, int? port)
    {
        if (port is not { } portalPort)
            return app;
        return app.Use(async (context, next) =>
        {
            // Its address alone, or any screen of the till's, opens the portal's sign-in.
            if (context.Connection.LocalPort == portalPort && IsOtherScreen(context.Request) && Allows(context.Request))
            {
                context.Response.Redirect("/portal");
                return;
            }

            if (context.Connection.LocalPort == portalPort && !Allows(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
    }
}
