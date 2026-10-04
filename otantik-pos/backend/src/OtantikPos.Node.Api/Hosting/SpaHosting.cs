using Microsoft.AspNetCore.StaticFiles;

namespace OtantikPos.Node.Api.Hosting;

// The Angular app, built into wwwroot. Served from here, the tills load the UI and call the API
// on one origin, from the one machine that has to be running anyway, with or without internet.
internal static class SpaHosting
{
    // Deep links such as /inventory are Angular routes, not files, so any path nothing else
    // matched gets index.html and the app's router takes it from there. Not under /api or
    // /hubs: an unknown path there is a genuine 404, and answering it with a web page would
    // hand the caller HTML where it expects JSON.
    public static async Task ServeIndexAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var index = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo("index.html");

        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || !index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(index);
    }

    // index.html is always revalidated, so a till picks up a new build on its next reload.
    // Everything else the Angular build emits has a content hash in its name and can be cached
    // for good.
    public static void SetCacheHeaders(StaticFileResponseContext context) =>
        context.Context.Response.Headers.CacheControl = context.File.Name == "index.html"
            ? "no-cache"
            : "public, max-age=31536000, immutable";
}
