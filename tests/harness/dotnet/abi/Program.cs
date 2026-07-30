// Would the built plugin actually run on a given Jellyfin 10.11.x?
//
// Two questions, because they fail differently:
//
//   1. Does every Jellyfin member the plugin's IL references still exist? A removed member is
//      not a build error — the plugin compiles against its pinned reference assemblies, the
//      manifest's targetAbi is a *minimum* so the server loads it anyway, and the failure
//      arrives when the code path is first executed. `IUserManager.Users` was removed in
//      10.11.9 and this check is what found it.
//   2. Does UserManagerCompat resolve an accessor and return users on this ABI? That is the
//      shim written for question 1, and it binds by name at runtime, so nothing at compile
//      time can tell you it works.
//
// Usage:
//   dotnet run                                  # against the default JfVersion in abi.csproj
//   dotnet run -p:JfVersion=10.11.6             # any 10.11.x
//   GRAVEYARD_DLL=/path/to/old/plugin.dll dotnet run -p:JfVersion=10.11.11   # non-vacuity
//
// Pointed at a pre-shim assembly, check 1 reports get_Users missing on 10.11.9+ and present on
// 10.11.8 and earlier, which is the finding in both directions.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MediaBrowser.Controller.Library;

var pluginPath = Environment.GetEnvironmentVariable("GRAVEYARD_DLL")
    ?? Path.Combine(AppContext.BaseDirectory,
        "../../../../../../../JellyfinGraveyardAnalytics/bin/Release/net9.0/JellyfinGraveyardAnalyticsPlugin.dll");
pluginPath = Path.GetFullPath(pluginPath);

if (!File.Exists(pluginPath))
{
    Console.WriteLine($"plugin assembly not found: {pluginPath}");
    Console.WriteLine("build the plugin first, or set GRAVEYARD_DLL");
    return 2;
}

// Every Jellyfin assembly the reference packages put next to us. These are what a server of
// this version would supply.
var jellyfin = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
{
    try
    {
        var loaded = Assembly.LoadFrom(dll);
        jellyfin[loaded.GetName().Name] = loaded;
    }
    catch (BadImageFormatException)
    {
        // Native or otherwise unmanaged; not something the plugin can reference.
    }
}

var controllerVersion = jellyfin.GetValueOrDefault("MediaBrowser.Controller")?.GetName().Version;
Console.WriteLine($"plugin  : {pluginPath}");
Console.WriteLine($"against : Jellyfin.Controller {controllerVersion}");
Console.WriteLine();

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "   <-- " + detail)}");
}

// ---- 1. Every Jellyfin member the IL references ------------------------------------------
using var stream = File.OpenRead(pluginPath);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();

string TypeName(EntityHandle handle)
{
    if (handle.Kind != HandleKind.TypeReference) return null;
    var reference = md.GetTypeReference((TypeReferenceHandle)handle);
    var ns = md.GetString(reference.Namespace);
    var name = md.GetString(reference.Name);
    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

string AssemblyOf(EntityHandle handle)
{
    if (handle.Kind != HandleKind.TypeReference) return null;
    var reference = md.GetTypeReference((TypeReferenceHandle)handle);
    return reference.ResolutionScope.Kind == HandleKind.AssemblyReference
        ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name)
        : null;
}

var referenced = new SortedSet<string>(StringComparer.Ordinal);
foreach (var handle in md.MemberReferences)
{
    var member = md.GetMemberReference(handle);
    var assembly = AssemblyOf(member.Parent);
    var type = TypeName(member.Parent);
    if (assembly is null || type is null) continue;
    if (!assembly.StartsWith("Jellyfin", StringComparison.Ordinal)
        && !assembly.StartsWith("MediaBrowser", StringComparison.Ordinal)
        && !assembly.StartsWith("Emby", StringComparison.Ordinal)) continue;
    referenced.Add($"{assembly}|{type}|{md.GetString(member.Name)}");
}

var missing = new List<string>();
var unknownType = new List<string>();

foreach (var entry in referenced)
{
    var parts = entry.Split('|');
    string assemblyName = parts[0], typeName = parts[1], memberName = parts[2];

    Type type = null;
    foreach (var candidate in jellyfin.Values)
    {
        type = candidate.GetType(typeName, false);
        if (type is not null) break;
    }

    if (type is null)
    {
        // Not in the reference packages at all — server-internal, so this check cannot speak
        // to it either way. Reported rather than counted.
        unknownType.Add($"{typeName} (from {assemblyName})");
        continue;
    }

    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    var found = memberName == ".ctor"
        ? type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length > 0
        : type.GetMember(memberName, Any).Length > 0
            || type.GetInterfaces().Any(i => i.GetMember(memberName, Any).Length > 0);

    if (!found) missing.Add($"{typeName}::{memberName}  (via {assemblyName})");
}

Check($"all {referenced.Count} Jellyfin members referenced by the plugin's IL still exist",
    missing.Count == 0, $"{missing.Count} missing");
foreach (var entry in missing) Console.WriteLine($"        !! {entry}");

if (unknownType.Count > 0)
{
    Console.WriteLine($"      note: {unknownType.Distinct().Count()} referenced type(s) are not in the "
        + "reference packages, so this check says nothing about them:");
    foreach (var entry in unknownType.Distinct()) Console.WriteLine($"        ?  {entry}");
}

// ---- 2. The shim, driven on this ABI ------------------------------------------------------
var pluginAssembly = Assembly.LoadFrom(pluginPath);
var compat = pluginAssembly.GetType("JellyfinGraveyardAnalytics.Services.UserManagerCompat");
Check("UserManagerCompat is present in the assembly", compat is not null);

if (compat is not null)
{
    var allUsers = compat.GetMethod("AllUsers", BindingFlags.Public | BindingFlags.Static);
    Check("UserManagerCompat.AllUsers is callable", allUsers is not null);

    if (allUsers is not null)
    {
        var expected = new[] { "visitor0", "visitor1" };
        try
        {
            var users = (IEnumerable)allUsers.Invoke(null, new object[] { UserManagerStub.Create(expected) });
            var names = users.Cast<object>()
                .Select(u => u.GetType().GetProperty("Username")?.GetValue(u) as string)
                .ToList();

            Check($"the shim returns {expected.Length} user(s) on Jellyfin.Controller {controllerVersion}",
                names.SequenceEqual(expected), $"got [{string.Join(", ", names)}]");
            Check("and it asked for the accessor this version actually has",
                UserManagerStub.LastAsked is "GetUsers" or "get_Users",
                $"asked for {UserManagerStub.LastAsked ?? "(nothing)"}");
        }
        catch (Exception ex)
        {
            // Deliberately broad: this harness reports, and a crash here would look like the
            // run simply stopping partway.
            var cause = (ex as TargetInvocationException)?.InnerException ?? ex;

            // The plugin's assembly references name the version it was built against, and .NET
            // will roll *forward* to a newer assembly but never back to an older one. So this
            // is not a harness artifact — it is the plugin's compiled floor, and it is why the
            // manifest's targetAbi has to be the version the csproj pins rather than the
            // earliest 10.11 that happens to have the right members.
            var isFloor = cause is FileLoadException
                || cause.Message.Contains("manifest definition does not match", StringComparison.Ordinal);

            Check("the shim resolves an accessor on this ABI", false,
                isFloor
                    ? $"below the plugin's compiled floor — it references Jellyfin assemblies "
                      + $"newer than {controllerVersion}, and .NET does not bind downwards"
                    : cause.Message);
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"OK — the built plugin is ABI-compatible with Jellyfin.Controller {controllerVersion}"
    : $"{failures} check(s) failed against Jellyfin.Controller {controllerVersion}");
return failures == 0 ? 0 : 1;

/// <summary>
/// Stands in for the server's user manager. Answers only the users accessor — anything else
/// would mean the shim reached for something it should not.
/// </summary>
/// <remarks>Not sealed: DispatchProxy generates a subclass of it.</remarks>
internal class UserManagerStub : DispatchProxy
{
    private IList _users;

    public static string LastAsked { get; private set; }

    public static IUserManager Create(string[] usernames)
    {
        // The User entity type, taken from whichever assembly this ABI declares it in, so the
        // list is the exact IEnumerable<User> the shim's delegate expects.
        var userType = typeof(IUserManager).GetMethod("GetUserById").ReturnType;
        var users = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(userType));
        foreach (var name in usernames)
        {
            users.Add(Activator.CreateInstance(userType, name, "Default", "Default"));
        }

        // The proxy is both, but the compiler knows of no relation between them, so the hop
        // through object is required.
        var proxy = Create<IUserManager, UserManagerStub>();
        ((UserManagerStub)(object)proxy)._users = users;
        LastAsked = null;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        LastAsked = targetMethod.Name;
        return targetMethod.Name is "GetUsers" or "get_Users"
            ? _users
            : throw new NotSupportedException($"the shim called {targetMethod.Name}");
    }
}
