// Independent DI-resolution probe for Phase 5 item 18.
// Mirrors how Jellyfin activates things:
//   * IPluginServiceRegistrator is instantiated with a parameterless ctor and handed the
//     IServiceCollection before the provider is built.
//   * Controllers are activated per request from the request scope (DefaultControllerActivator
//     -> ActivatorUtilities.CreateInstance(scopedProvider, type)), so they are registered
//     scoped here to make the container validate their whole graph.
//   * Plugins are created with ActivatorUtilities.CreateInstance(rootProvider, pluginType).
// Jellyfin-supplied services are DispatchProxy stubs; the plugin's own registrations are real.

using System.Reflection;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var results = new List<(string Name, bool Ok, string Detail)>();
void Check(string name, bool ok, string detail = "") => results.Add((name, ok, detail));

var dll = Environment.GetEnvironmentVariable("GRAVEYARD_DLL") ?? Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    "../../../../../../../JellyfinGraveyardAnalytics/bin/Release/net9.0/JellyfinGraveyardAnalyticsPlugin.dll"));

if (!File.Exists(dll))
{
    Console.Error.WriteLine($"Plugin assembly not found at {dll}. Build it first (dotnet publish -c Release) or set GRAVEYARD_DLL.");
    return 2;
}

var asm = Assembly.LoadFrom(dll);

Type T(string n) => asm.GetType(n) ?? throw new InvalidOperationException("missing " + n);

var registratorType = T("JellyfinGraveyardAnalytics.GraveyardServiceRegistrator");
var pluginType      = T("JellyfinGraveyardAnalytics.Plugin");
var repoType        = T("JellyfinGraveyardAnalytics.Database.Repository");
var analyticsType   = T("JellyfinGraveyardAnalytics.Services.AnalyticsService");
var providerType    = T("JellyfinGraveyardAnalytics.Services.PlaybackStatsProvider");
var tracearrType    = T("JellyfinGraveyardAnalytics.Services.TracearrService");
var cfgSourceIface  = T("JellyfinGraveyardAnalytics.Configuration.IPluginConfigurationSource");
var cfgSourceImpl   = T("JellyfinGraveyardAnalytics.Configuration.PluginConfigurationSource");
var statsType       = T("JellyfinGraveyardAnalytics.Services.PlaybackStats");
var ttlCacheType    = T("JellyfinGraveyardAnalytics.Services.TtlCache`1").MakeGenericType(statsType);
var ctrlType        = T("JellyfinGraveyardAnalytics.Controllers.GraveyardAnalyticsController");
var tracearrCtrl    = T("JellyfinGraveyardAnalytics.Api.TracearrController");

// ---- registrator shape Jellyfin requires
var regIface = registratorType.GetInterface("MediaBrowser.Controller.Plugins.IPluginServiceRegistrator");
Check("R1 registrator implements the 10.11.6 IPluginServiceRegistrator", regIface is not null);
Check("R2 registrator has a public parameterless ctor (Jellyfin Activator.CreateInstance)",
    registratorType.GetConstructor(Type.EmptyTypes) is not null);

var pluginCtors = pluginType.GetConstructors();
Check("R3 Plugin has exactly one public ctor", pluginCtors.Length == 1,
    string.Join(" / ", pluginCtors.Select(c => string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)))));
Check("R4 Plugin ctor params are (IApplicationPaths, IXmlSerializer)",
    pluginCtors[0].GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[] { "IApplicationPaths", "IXmlSerializer" }),
    string.Join(",", pluginCtors[0].GetParameters().Select(p => p.ParameterType.Name)));

// ---- container
var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));

// Jellyfin-side singletons, stubbed.
object Stub(Type iface) => typeof(DispatchProxy)
    .GetMethods(BindingFlags.Public | BindingFlags.Static)
    .First(m => m.Name == "Create" && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0)
    .MakeGenericMethod(iface, typeof(NullProxy))
    .Invoke(null, null)!;

Type J(string n)
{
    foreach (var a in new[] { "MediaBrowser.Controller", "MediaBrowser.Common", "MediaBrowser.Model", "Jellyfin.Data" })
    {
        var t = Assembly.Load(a).GetType(n);
        if (t is not null) return t;
    }
    throw new InvalidOperationException("missing jellyfin type " + n);
}

var jellyfinIfaces = new[]
{
    "MediaBrowser.Controller.Library.ILibraryManager",
    "MediaBrowser.Controller.Library.IUserManager",
    "MediaBrowser.Controller.Library.IUserDataManager",
    "MediaBrowser.Controller.Collections.ICollectionManager",
    "MediaBrowser.Controller.Providers.IProviderManager",
    "MediaBrowser.Controller.IServerApplicationHost",
    "MediaBrowser.Model.Serialization.IXmlSerializer",
};
foreach (var name in jellyfinIfaces)
{
    var t = J(name);
    services.AddSingleton(t, _ => Stub(t));
}

var paths = new HarnessPaths(Path.Combine(Path.GetTempPath(), "di-probe-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(paths.DataPath);
services.AddSingleton<IApplicationPaths>(paths);

// ---- the plugin's own registrations, verbatim
var registrator = Activator.CreateInstance(registratorType)!;
var appHost = services.BuildServiceProvider().GetRequiredService(J("MediaBrowser.Controller.IServerApplicationHost"));
registratorType.GetMethod("RegisterServices")!.Invoke(registrator, new object[] { services, appHost });

// MVC activates controllers from the request scope; registering them scoped makes the
// container validate the same graph ActivatorUtilities would walk.
services.AddScoped(ctrlType);
services.AddScoped(tracearrCtrl);

Exception buildError = null;
ServiceProvider provider = null;
try
{
    provider = services.BuildServiceProvider(new ServiceProviderOptions
    {
        ValidateScopes = true,
        ValidateOnBuild = true,
    });
}
catch (Exception ex)
{
    buildError = ex;
}

Check("V1 provider builds with ValidateOnBuild+ValidateScopes (no missing service, no captive dependency)",
    buildError is null, buildError?.Message ?? "");

if (provider is not null)
{
    // ---- resolution from a request scope
    Exception ctrlError = null;
    object ctrl = null;
    using (var scope = provider.CreateScope())
    {
        try { ctrl = scope.ServiceProvider.GetRequiredService(ctrlType); }
        catch (Exception ex) { ctrlError = ex; }
    }
    Check("V2 GraveyardAnalyticsController resolves from a request scope", ctrlError is null, ctrlError?.Message ?? "");

    Exception tcError = null;
    using (var scope = provider.CreateScope())
    {
        try { scope.ServiceProvider.GetRequiredService(tracearrCtrl); }
        catch (Exception ex) { tcError = ex; }
    }
    Check("V3 TracearrController resolves from a request scope", tcError is null, tcError?.Message ?? "");

    // MVC's real path: ActivatorUtilities, not the container registration.
    Exception auError = null;
    using (var scope = provider.CreateScope())
    {
        try { ActivatorUtilities.CreateInstance(scope.ServiceProvider, ctrlType); }
        catch (Exception ex) { auError = ex; }
    }
    Check("V4 controller also builds via ActivatorUtilities (what DefaultControllerActivator uses)",
        auError is null, auError?.Message ?? "");

    // ---- lifetimes
    using var s1 = provider.CreateScope();
    using var s2 = provider.CreateScope();

    var repo1 = s1.ServiceProvider.GetRequiredService(repoType);
    var repo2 = s2.ServiceProvider.GetRequiredService(repoType);
    Check("L1 Repository is one instance server-wide", ReferenceEquals(repo1, repo2));

    var cache1 = s1.ServiceProvider.GetRequiredService(ttlCacheType);
    var cache2 = s2.ServiceProvider.GetRequiredService(ttlCacheType);
    Check("L2 TtlCache<PlaybackStats> is one instance server-wide (otherwise it caches nothing)",
        ReferenceEquals(cache1, cache2));

    var an1a = s1.ServiceProvider.GetRequiredService(analyticsType);
    var an1b = s1.ServiceProvider.GetRequiredService(analyticsType);
    var an2  = s2.ServiceProvider.GetRequiredService(analyticsType);
    Check("L3 AnalyticsService is one per request, shared within it (episode index memoized once)",
        ReferenceEquals(an1a, an1b) && !ReferenceEquals(an1a, an2));

    var pr1a = s1.ServiceProvider.GetRequiredService(providerType);
    var pr1b = s1.ServiceProvider.GetRequiredService(providerType);
    var pr2  = s2.ServiceProvider.GetRequiredService(providerType);
    Check("L4 PlaybackStatsProvider is scoped", ReferenceEquals(pr1a, pr1b) && !ReferenceEquals(pr1a, pr2));

    var tr1 = s1.ServiceProvider.GetRequiredService(tracearrType);
    var tr2 = s1.ServiceProvider.GetRequiredService(tracearrType);
    Check("L5 TracearrService is transient (AddHttpClient), so no HttpClient is pinned",
        !ReferenceEquals(tr1, tr2));

    var cfg1 = s1.ServiceProvider.GetRequiredService(cfgSourceIface);
    var cfg2 = s2.ServiceProvider.GetRequiredService(cfgSourceIface);
    Check("L6 IPluginConfigurationSource is a singleton of PluginConfigurationSource",
        ReferenceEquals(cfg1, cfg2) && cfg1.GetType() == cfgSourceImpl);

    // The provider inside the controller and the one AnalyticsService got must be the SAME
    // Repository, or the two would disagree about whether the database exists.
    var repoFieldOfAnalytics = analyticsType
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .First(f => f.FieldType == repoType);
    Check("L7 AnalyticsService and PlaybackStatsProvider share the one Repository",
        ReferenceEquals(repoFieldOfAnalytics.GetValue(an1a), repo1));

    // ---- the remaining static: resolving must not touch Plugin.Instance
    var instanceProp = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
    Check("S1 Plugin.Instance is still null after building and resolving everything",
        instanceProp!.GetValue(null) is null);

    // ...but reading configuration without a plugin instance is a NullReferenceException.
    Exception cfgError = null;
    try { cfgSourceIface.GetProperty("Current")!.GetValue(cfg1); }
    catch (TargetInvocationException ex) { cfgError = ex.InnerException; }
    Check("S2 PluginConfigurationSource.Current throws NRE when Plugin.Instance is unset",
        cfgError is NullReferenceException, cfgError?.GetType().Name ?? "no throw");

    // Does the Plugin type still construct the way ActivatorUtilities would?
    Exception plugError = null;
    try { ActivatorUtilities.CreateInstance(provider, pluginType); }
    catch (Exception ex) { plugError = ex; }
    Check("S3 Plugin constructs via ActivatorUtilities from the container (trimmed ctor is resolvable)",
        plugError is null, plugError?.Message ?? "");
    Check("S4 constructing the plugin sets Plugin.Instance", instanceProp.GetValue(null) is not null);
}

var failed = results.Count(r => !r.Ok);
foreach (var r in results)
{
    Console.WriteLine($"{(r.Ok ? "PASS" : "FAIL")}  {r.Name}{(r.Ok || r.Detail.Length == 0 ? "" : "\n        <-- " + r.Detail)}");
}
Console.WriteLine($"\n{results.Count - failed}/{results.Count} passed");
return failed == 0 ? 0 : 1;

public class NullProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args)
        => targetMethod!.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
}

internal sealed class HarnessPaths(string dataPath) : IApplicationPaths
{
    public string DataPath { get; } = dataPath;
    public string ProgramDataPath => DataPath;
    public string WebPath => DataPath;
    public string ProgramSystemPath => DataPath;
    public string ImageCachePath => DataPath;
    public string PluginsPath => DataPath;
    public string PluginConfigurationsPath => DataPath;
    public string LogDirectoryPath => DataPath;
    public string ConfigurationDirectoryPath => DataPath;
    public string SystemConfigurationFilePath => Path.Combine(DataPath, "system.xml");
    public string CachePath => DataPath;
    public string TempDirectory => DataPath;
    public string VirtualDataPath => DataPath;
    public string TrickplayPath => DataPath;
    public string BackupPath => DataPath;
    public void MakeSanityCheckOrThrow() { }
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
}
