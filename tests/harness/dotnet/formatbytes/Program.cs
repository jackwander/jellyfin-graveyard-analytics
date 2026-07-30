// Compares the OLD FormatBytes (copied from 71a01f7) with the NEW one loaded
// from the built plugin assembly.
using System;
using System.IO;
using System.Reflection;

class P
{
    static string Old(long bytes)
    {
        string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }
        return $"{dblSByte:0.##} {Suffix[i]}";
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

    static void Main()
    {
        var dll = FindPluginDll();
        var m = Assembly.LoadFrom(dll)
            .GetType("JellyfinGraveyardAnalytics.Services.AnalyticsService")!
            .GetMethod("FormatBytes", BindingFlags.Public | BindingFlags.Static)!;
        Func<long, string> New = b => (string)m.Invoke(null, new object[] { b })!;

        (long v, string label)[] cases = {
            (0, "zero"), (1023, "1023 B"), (1024, "1 KB"), (1536, "1.5 KB"),
            (1048576, "1 MB"), (1073741824, "1 GB"),
            (1099511627776, "1 TB"),
            (1125899906842624, "1 PB  <- finding 10"),
            (1152921504606846976, "1 EB"),
            (long.MaxValue, "long.MaxValue"),
            (5L * 1024 * 1024 * 1024 * 1024, "5 TB"),
            (-2048, "negative"),
        };

        Console.WriteLine($"{"input",-24} {"OLD (71a01f7)",-28} NEW");
        foreach (var (v, label) in cases)
        {
            string oldR;
            try { oldR = Old(v); }
            catch (Exception ex) { oldR = "THROWS " + ex.GetType().Name; }

            string newR;
            try { newR = New(v); }
            catch (Exception ex) { newR = "THROWS " + (ex.InnerException ?? ex).GetType().Name; }

            Console.WriteLine($"{label,-24} {oldR,-28} {newR}");
        }
    }
}
