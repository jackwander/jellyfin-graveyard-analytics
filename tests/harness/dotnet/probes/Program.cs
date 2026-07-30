using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

// ---------------------------------------------------------------
// PROBE A — finding 3: does "Data Source=path" (no Mode=ReadOnly)
// create a missing SQLite file, and is the handle writable?
// ---------------------------------------------------------------
var dir = Path.Combine(Path.GetTempPath(), "gyprobe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "playback_reporting.db");

Console.WriteLine("=== PROBE A: Repository.cs:21 vs :28 connection strings ===");

// The string built at :21 (then discarded)
try
{
    using var ro = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
    ro.Open();
    Console.WriteLine("A1 Mode=ReadOnly on missing file : OPENED (unexpected)");
}
catch (SqliteException ex)
{
    Console.WriteLine($"A1 Mode=ReadOnly on missing file : THROWS ({ex.SqliteErrorCode}) -> {ex.Message.Split('\n')[0]}");
}
Console.WriteLine($"A1 file created?                 : {File.Exists(dbPath)}");

// The string actually used at :28
using (var rw = new SqliteConnection($"Data Source={dbPath}"))
{
    rw.Open();
    Console.WriteLine($"A2 no Mode= on missing file      : OPENED, file created = {File.Exists(dbPath)}");
    using var cmd = rw.CreateCommand();
    cmd.CommandText = "CREATE TABLE probe_write(x int); INSERT INTO probe_write VALUES(1);";
    cmd.ExecuteNonQuery();
    Console.WriteLine("A2 write via that handle         : SUCCEEDED (handle is read-write)");
}
Console.WriteLine($"A2 empty db left on disk         : {new FileInfo(dbPath).Length} bytes at {dbPath}");
Directory.Delete(dir, true);
Console.WriteLine();

// ---------------------------------------------------------------
// PROBE D — review finding F1: does a media row still serialize an
// absolute filesystem path? Reflects over the BUILT plugin assembly.
// ---------------------------------------------------------------
Console.WriteLine("=== PROBE D: LeastWatchedItem on the wire ===");
var pluginDll = FindPluginDll();
var asm = System.Reflection.Assembly.LoadFrom(pluginDll);
var itemType = asm.GetType("JellyfinGraveyardAnalytics.Models.LeastWatchedItem")!;
var names = itemType.GetProperties().Select(p => p.Name).ToArray();
Console.WriteLine($"    properties: {string.Join(", ", names)}");

var row = Activator.CreateInstance(itemType)!;
itemType.GetProperty("Name")!.SetValue(row, "Alien (1979)");
itemType.GetProperty("Type")!.SetValue(row, "Movie");
itemType.GetProperty("MediaId")!.SetValue(row, "1a2b3c4d-5e6f-4778-8899-aabbccddeeff");
var json = System.Text.Json.JsonSerializer.Serialize(row, itemType);
Console.WriteLine($"    serialized : {json}");
Console.WriteLine($"    Path property present : {names.Contains("Path")}");
Console.WriteLine($"    'Path' anywhere in JSON: {json.Contains("Path", StringComparison.OrdinalIgnoreCase)}");
Console.WriteLine();

// ---------------------------------------------------------------
// PROBE B — finding 5: what does [FromQuery] string token bind to
// for ?token= (empty), ?token=%20, and an absent token?
// ---------------------------------------------------------------
// Run twice: default MVC options, then with implicit-required suppressed
// (in case Jellyfin's own MvcOptions differ from the template default).
foreach (var suppress in new[] { false, true })
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddControllers().AddMvcOptions(o =>
        o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = suppress);
    var app = builder.Build();
    app.MapControllers();
    var port = suppress ? 5198 : 5199;
    _ = app.RunAsync($"http://127.0.0.1:{port}");
    await Task.Delay(1500);

    Console.WriteLine($"=== PROBE B (before): TracearrController.cs:46,49 token binding "
        + $"(SuppressImplicitRequired={suppress}) ===");
    Console.WriteLine("    server-side configured key = string.Empty (fresh install)");
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    foreach (var qs in new[] { "?token=", "?token=%20", "", "?token=abc", "?token=&token=" })
    {
        var body = new StringContent("{\"MediaId\":\"x\"}", System.Text.Encoding.UTF8, "application/json");
        var res = await http.PostAsync("/probe/webhook" + qs, body);
        var text = await res.Content.ReadAsStringAsync();
        var brief = text.Contains("\"status\":400") ? "400 required-validation-error" : $"{(int)res.StatusCode} {text}";
        Console.WriteLine($"    POST /probe/webhook{(qs == "" ? " (no query)" : qs),-18} -> {brief}");
    }

    // -----------------------------------------------------------
    // PROBE C — the Phase 1 replacement: header token, reject when
    // the key is unset, fixed-time compare, honest 501.
    // -----------------------------------------------------------
    if (!suppress)
    {
        Console.WriteLine();
        Console.WriteLine("=== PROBE C (after): header token + empty-key rejection + 501 ===");
        foreach (var (enabled, key, header, note) in new (bool, string, string?, string)[]
        {
            (true,  "",       null,       "fresh install, no header"),
            (true,  "",       "",         "fresh install, empty header"),
            (true,  "",       "anything", "fresh install, any header"),
            (true,  "s3cret", null,       "key set, no header"),
            (true,  "s3cret", "wrong",    "key set, wrong header"),
            (true,  "s3cret", "s3cre",    "key set, prefix of key"),
            (true,  "s3cret", "s3cret",   "key set, CORRECT header"),
            (false, "s3cret", "s3cret",   "ENGINE OFF, correct header"),
        })
        {
            ProbeController.Enabled = enabled;
            ProbeController.ConfiguredKey = key;
            var req = new HttpRequestMessage(HttpMethod.Post, "/probe/webhook-v2")
            {
                Content = new StringContent("{\"MediaId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"),
            };
            if (header is not null) req.Headers.TryAddWithoutValidation("X-Tracearr-Token", header);
            var res = await http.SendAsync(req);
            Console.WriteLine($"    {note,-28} -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        }

        // The old query-string vector must no longer authenticate.
        ProbeController.Enabled = true; ProbeController.ConfiguredKey = "s3cret";
        var qres = await http.PostAsync("/probe/webhook-v2?token=s3cret",
            new StringContent("{\"MediaId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
        Console.WriteLine($"    {"correct key in ?token= only",-28} -> {(int)qres.StatusCode} {await qres.Content.ReadAsStringAsync()}");
    }

    await app.StopAsync();
    Console.WriteLine();
}

static string FindPluginDll()
{
    var explicitPath = Environment.GetEnvironmentVariable("GRAVEYARD_DLL");
    if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var candidate = Path.Combine(
            dir.FullName, "JellyfinGraveyardAnalytics", "bin", "Release", "net9.0",
            "JellyfinGraveyardAnalyticsPlugin.dll");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }

    throw new FileNotFoundException(
        "Build the plugin first (dotnet build -c Release), or set GRAVEYARD_DLL.");
}

[ApiController]
public class ProbeController : ControllerBase
{
    // Mirrors TracearrController.ReceiveCondemnWebhook exactly.
    [HttpPost("/probe/webhook")]
    public IActionResult Webhook([FromBody] Payload payload, [FromQuery] string token)
    {
        var configuredKey = string.Empty; // PluginConfiguration default
        var bound = token is null ? "null" : $"\"{token}\"";
        if (token != configuredKey)
        {
            return Unauthorized($"bound={bound} -> 401 (no bypass)");
        }
        return Ok($"bound={bound} -> 200 BYPASS");
    }

    // Mirrors the Phase 1 ReceiveCondemnWebhook body verbatim, with
    // Plugin.Instance.Configuration.TracearrApiKey standing in as a static.
    public static string ConfiguredKey = string.Empty;
    public static bool Enabled = true;
    private const string WebhookTokenHeader = "X-Tracearr-Token";

    [HttpPost("/probe/webhook-v2")]
    public IActionResult WebhookV2([FromBody] Payload? payload)
    {
        if (!Enabled)
        {
            return Unauthorized("401 engine-disabled rejected");
        }

        if (string.IsNullOrWhiteSpace(ConfiguredKey))
        {
            return Unauthorized("401 empty-key rejected");
        }

        var configuredKey = ConfiguredKey;

        if (!Request.Headers.TryGetValue(WebhookTokenHeader, out var presented)
            || presented.Count != 1
            || !TokenMatches(presented[0], configuredKey))
        {
            return Unauthorized("401 unauthorized");
        }

        if (payload is null)
        {
            return BadRequest("400 payload required");
        }

        return StatusCode(StatusCodes.Status501NotImplemented, "501 not-implemented (honest)");
    }

    private static bool TokenMatches(string? presented, string configured)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(configured));
    }

    public class Payload
    {
        public string? MediaId { get; set; }
        public string? EventType { get; set; }
    }
}
