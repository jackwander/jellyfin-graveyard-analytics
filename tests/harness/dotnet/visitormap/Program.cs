// Drives TracearrService.MapSession (Phase 2 item 7) from the BUILT plugin assembly
// against a verbatim Tracearr history row, captured from the live server at
// 10.10.1.201:3000 on 2026-07-30 and recorded in PLAN.md.
//
// The row matters more than the assertions: `durationMs` arrives as a JSON number
// while `progressMs` and `totalDurationMs` arrive as JSON *strings* on the same row,
// so a mapper calling GetInt64() on all three throws on two of them. That is what
// this harness exists to catch.
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

class P
{
    static int failures;

    static void Check(string label, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}{(ok || detail.Length == 0 ? "" : $"  -> {detail}")}");
        if (!ok)
        {
            failures++;
        }
    }

    static string FindPluginDll()
    {
        var explicitPath = Environment.GetEnvironmentVariable("GRAVEYARD_DLL");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "JellyfinGraveyardAnalytics", "bin", "Release", "net9.0",
                "JellyfinGraveyardAnalyticsPlugin.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Build the plugin first (dotnet build -c Release), or set GRAVEYARD_DLL.");
    }

    // Verbatim shape from GET /api/v1/public/history?weeksBack=1&page=1.
    const string LiveRow = """
    {
      "user": { "id": 7, "username": "jerwin", "thumbUrl": null, "avatarUrl": null },
      "mediaTitle": "The Conjugal Conjecture",
      "showTitle": "The Big Bang Theory",
      "mediaType": "episode",
      "thumbPath": "/Items/426b0f74a4a4f19e65783d9e7b5ff4ea/Images/Primary",
      "startedAt": "2026-07-29T22:33:30.278Z",
      "stoppedAt": "2026-07-29T22:45:00.210Z",
      "durationMs": 689932,
      "progressMs": "674554",
      "totalDurationMs": "1312416",
      "watched": false,
      "isTranscode": false,
      "videoDecision": "directplay",
      "device": "Living Room TV",
      "player": "Android TV",
      "product": "Jellyfin Android TV",
      "platform": "Android",
      "state": "stopped"
    }
    """;

    /// <summary>
    /// Fires the endpoint the built assembly produced at a real Tracearr and checks the
    /// server accepts it. Skipped unless TRACEARR_URL and TRACEARR_KEY are exported — the
    /// key is never stored in the repo.
    /// </summary>
    static void LiveProbe(string endpoint)
    {
        var baseUrl = Environment.GetEnvironmentVariable("TRACEARR_URL");
        var key = Environment.GetEnvironmentVariable("TRACEARR_KEY");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("SKIP  live probe (set TRACEARR_URL and TRACEARR_KEY to run it)");
            return;
        }

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v1/public/{endpoint}");
        request.Headers.Add("Authorization", $"Bearer {key}");

        using var response = http.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        Check($"live: the generated query is accepted ({(int)response.StatusCode})",
            response.IsSuccessStatusCode, body.Length > 200 ? body[..200] : body);

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var meta = doc.RootElement.GetProperty("meta");
        var total = meta.GetProperty("total").GetInt32();
        var rows = doc.RootElement.GetProperty("data").GetArrayLength();

        Console.WriteLine($"  live meta: total={total} rows={rows} pageSize={meta.GetProperty("pageSize").GetInt32()}");
        Check("live: the date window actually filters (a week is far short of all-time)",
            total < 100, $"total={total}");
        Check("live: every row falls inside the requested window",
            AllRowsWithin(doc.RootElement, new DateTime(2026, 7, 23), new DateTime(2026, 7, 31)));
    }

    static bool AllRowsWithin(JsonElement root, DateTime start, DateTime end)
    {
        foreach (var row in root.GetProperty("data").EnumerateArray())
        {
            if (!row.TryGetProperty("startedAt", out var startedAt)
                || !DateTime.TryParse(startedAt.GetString(), out var when))
            {
                continue;
            }

            if (when < start || when > end)
            {
                Console.WriteLine($"  out of window: {startedAt.GetString()}");
                return false;
            }
        }

        return true;
    }

    static void Main()
    {
        var asm = Assembly.LoadFrom(FindPluginDll());
        var map = asm.GetType("JellyfinGraveyardAnalytics.Services.TracearrService")!
            .GetMethod("MapSession", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? Get(object session, string property)
            => session.GetType().GetProperty(property)!.GetValue(session);

        (object session, string visitor, long seconds) Map(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var args = new object?[] { doc.RootElement, null, null };
            var session = map.Invoke(null, args)!;
            return (session, (string)args[1]!, (long)args[2]!);
        }

        // --- 1. the live row, unmodified ---
        var (row, visitor, seconds) = Map(LiveRow);

        Check("string progressMs / numeric durationMs both parse (no GetInt64 throw)", true);
        Check("visitor comes from user.username", visitor == "jerwin", visitor);
        Check("duration is durationMs/1000", seconds == 689, seconds.ToString());
        Check("Duration formats as HH:MM:SS", (string)Get(row, "Duration")! == "00:11:29",
            (string)Get(row, "Duration")!);
        Check("episode Subject is 'show - episode'",
            (string)Get(row, "Subject")! == "The Big Bang Theory - The Conjugal Conjecture",
            (string)Get(row, "Subject")!);
        Check("lowercase mediaType is title-cased to match the local engine",
            (string)Get(row, "Type")! == "Episode", (string)Get(row, "Type")!);
        Check("videoDecision is upper-cased for the Method cell",
            (string)Get(row, "Method")! == "DIRECTPLAY", (string)Get(row, "Method")!);
        Check("Device / Player split across the Vessel cell",
            (string)Get(row, "Device")! == "Living Room TV" && (string)Get(row, "Player")! == "Android TV");
        Check("ProgressPercent = progressMs/totalDurationMs, from two STRING fields",
            Math.Abs((double)Get(row, "ProgressPercent")! - 51.398) < 0.01,
            Get(row, "ProgressPercent")!.ToString()!);
        Check("Watched false is carried through as false, not null",
            (bool?)Get(row, "Watched") == false);
        Check("startedAt parses (Time is not the unknown-date literal)",
            (string)Get(row, "Time")! != "Unknown Date", (string)Get(row, "Time")!);

        // --- 2. movies: showTitle is null on the live server ---
        var (movie, _, _) = Map(LiveRow
            .Replace("\"showTitle\": \"The Big Bang Theory\"", "\"showTitle\": null")
            .Replace("\"mediaType\": \"episode\"", "\"mediaType\": \"movie\""));
        Check("null showTitle yields the bare media title, not ' - title'",
            (string)Get(movie, "Subject")! == "The Conjugal Conjecture",
            (string)Get(movie, "Subject")!);

        // --- 3. degenerate rows must not throw or divide by zero ---
        var (empty, emptyVisitor, emptySeconds) = Map("{}");
        Check("empty row maps without throwing", true);
        Check("missing user falls back to a label, not null", emptyVisitor == "Unknown Entity", emptyVisitor);
        Check("missing durationMs is 0, not a throw", emptySeconds == 0, emptySeconds.ToString());
        Check("missing totalDurationMs leaves ProgressPercent null (no divide-by-zero)",
            Get(empty, "ProgressPercent") is null, $"{Get(empty, "ProgressPercent")}");
        Check("missing watched leaves Watched null, so the UI shows a dash",
            Get(empty, "Watched") is null);

        var (zero, _, _) = Map(LiveRow.Replace("\"totalDurationMs\": \"1312416\"", "\"totalDurationMs\": \"0\""));
        Check("zero totalDurationMs leaves ProgressPercent null",
            Get(zero, "ProgressPercent") is null, $"{Get(zero, "ProgressPercent")}");

        var (junk, _, junkSeconds) = Map(LiveRow
            .Replace("\"durationMs\": 689932", "\"durationMs\": \"not-a-number\"")
            .Replace("\"progressMs\": \"674554\"", "\"progressMs\": null"));
        Check("unparseable durationMs degrades to 0", junkSeconds == 0, junkSeconds.ToString());
        Check("null progressMs reads as 0%, not a crash",
            (double?)Get(junk, "ProgressPercent") == 0d, $"{Get(junk, "ProgressPercent")}");

        // --- 4. progress can exceed 100 on the wire; the Fate cell must not ---
        var (over, _, _) = Map(LiveRow.Replace("\"progressMs\": \"674554\"", "\"progressMs\": \"9999999\""));
        Check("progress beyond the runtime clamps to 100",
            (double?)Get(over, "ProgressPercent") == 100d, $"{Get(over, "ProgressPercent")}");

        // --- 5. the query the plugin actually sends ---
        var build = asm.GetType("JellyfinGraveyardAnalytics.Services.TracearrService")!
            .GetMethod("BuildHistoryEndpoint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var endpoint = (string)build.Invoke(null,
            new object[] { new DateTime(2026, 7, 23), new DateTime(2026, 7, 30), 1 })!;

        Console.WriteLine();
        Console.WriteLine($"built endpoint: {endpoint}");
        Check("no weeksBack: Tracearr has no such parameter and silently ignores it",
            !endpoint.Contains("weeksBack", StringComparison.OrdinalIgnoreCase), endpoint);
        Check("sends startDate AND endDate — endDate alone leaves the window unbounded",
            endpoint.Contains("startDate=2026-07-23") && endpoint.Contains("endDate=2026-07-30"), endpoint);
        Check("pins the timezone so both engines bound the window the same way",
            endpoint.Contains("timezone=UTC"), endpoint);
        Check("requests the documented maximum page size (101+ is a 400)",
            endpoint.Contains("pageSize=100"), endpoint);

        LiveProbe(endpoint);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "all checks passed"
            : $"{failures} FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
