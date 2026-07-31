using System;
using System.IO;
using System.Threading.Tasks;
using JellyfinGraveyardAnalytics.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellyfinGraveyardAnalytics.Services
{
    /// <summary>
    /// Adds one <c>&lt;script&gt;</c> tag to the web client's <c>index.html</c>, which is what
    /// puts a "Leaving Soon" row on the home screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin offers no supported hook for this. <c>HomeSectionType</c> is a closed enum,
    /// <c>PluginPageInfo</c> only adds admin pages, and <c>BrandingOptions</c> has
    /// <c>CustomCss</c> but no JavaScript equivalent. What *is* public API is
    /// <c>IPluginServiceRegistrator</c>, and plugin registrations land in the same container
    /// the web host is built from — <c>Program.cs</c> calls <c>appHost.Init(services)</c>
    /// inside <c>ConfigureServices</c>, and there is no child container. So an
    /// <see cref="IStartupFilter"/> registered by a plugin is applied to the real pipeline.
    /// </para>
    /// <para>
    /// This deliberately does *not* do what the community plugins do. Those Harmony-patch
    /// Jellyfin's private <c>Startup.Configure</c> and reimplement the pipeline, which is why
    /// they need one build per Jellyfin patch and why a version mismatch has taken whole
    /// servers down rather than just losing a row. A startup filter uses only public contracts.
    /// </para>
    /// <para>
    /// Two things here are not stylistic, and both were established by experiment:
    /// </para>
    /// <para>
    /// It **short-circuits instead of wrapping the response body**. Jellyfin's
    /// <c>UseResponseCompression()</c> sits *inside* this middleware, so a filter that wrapped
    /// the stream and string-replaced would be handed gzip and would silently do nothing.
    /// Reading the file and writing the response is the only reliable shape.
    /// </para>
    /// <para>
    /// It matches on the *end* of the path. This middleware runs outside
    /// <c>app.Map(BaseUrl, …)</c>, so on a server with a configured base URL the request path
    /// still carries that prefix — and the injected script URL has to carry it too.
    /// </para>
    /// </remarks>
    public sealed class HomeSectionStartupFilter : IStartupFilter
    {
        private readonly IApplicationPaths _paths;
        private readonly IPluginConfigurationSource _configSource;
        private readonly ILogger<HomeSectionStartupFilter> _logger;

        public HomeSectionStartupFilter(
            IApplicationPaths paths,
            IPluginConfigurationSource configSource,
            ILogger<HomeSectionStartupFilter> logger)
        {
            _paths = paths;
            _configSource = configSource;
            _logger = logger;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(Invoke);
                next(app);
            };

        private async Task Invoke(HttpContext context, RequestDelegate next)
        {
            if (!ShouldInject(context, out var prefix))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            string html;
            try
            {
                var indexPath = Path.Combine(_paths.WebPath, "index.html");
                html = await File.ReadAllTextAsync(indexPath, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Serving the page unmodified is always better than not serving it. The row is
                // the expendable half of this feature; the web client is not.
                _logger.LogWarning(ex, "Could not read index.html to add the Leaving Soon row; serving it unmodified.");
                await next(context).ConfigureAwait(false);
                return;
            }

            var marker = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                _logger.LogWarning("index.html has no </body>; skipping the Leaving Soon row.");
                await next(context).ConfigureAwait(false);
                return;
            }

            var tag = $"<script defer src=\"{prefix}/GraveyardAnalytics/home.js\"></script>";
            var injected = html.Insert(marker, tag);

            context.Response.ContentType = "text/html; charset=utf-8";

            // Same header Jellyfin's own static handler sets for index.html. Without it a proxy
            // could cache the injected page and keep serving it after the toggle is turned off.
            context.Response.Headers.CacheControl = "no-cache";

            await context.Response.WriteAsync(injected, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether this request is the web client's entry page and the feature is switched on.
        /// </summary>
        /// <param name="context">The request.</param>
        /// <param name="prefix">Everything before <c>/web</c>, so the script URL survives a configured base URL.</param>
        private bool ShouldInject(HttpContext context, out string prefix)
        {
            prefix = string.Empty;

            if (!HttpMethods.IsGet(context.Request.Method))
            {
                return false;
            }

            var path = context.Request.Path.Value;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // /web, /web/ and /web/index.html all render the client. UseDefaultFiles maps the
            // first two, and it runs after this, so all three have to be recognised here.
            var trimmed = path.TrimEnd('/');
            if (trimmed.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
            {
                prefix = trimmed[..^"/web/index.html".Length];
            }
            else if (trimmed.EndsWith("/web", StringComparison.OrdinalIgnoreCase))
            {
                prefix = trimmed[..^"/web".Length];
            }
            else
            {
                return false;
            }

            // Read last, and inside a try: the configuration source reaches through
            // Plugin.Instance, which is null until Jellyfin has constructed the plugin — and a
            // request can arrive before that. Read per request so the toggle takes effect
            // without a restart once the plugin is up.
            try
            {
                return _configSource.Current.EnableHomeSection;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Plugin configuration not available yet; not adding the Leaving Soon row.");
                return false;
            }
        }
    }
}
