using StableRestorer.Lazer;

namespace StableRestorer.Cli;

/// <summary>
/// Diagnostics for "Realm will not open this database": probes candidate schema versions and
/// reports exactly which declared properties do not match the file.
/// </summary>
public static class SchemaProbe
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                Lazerschema probe - find the Realm schema version of an osu!lazer client.realm.

                USAGE
                  stablerestorer schemas --lazer <dir> [--schema-version <n>]

                Without --schema-version every known candidate is tried, and the full mismatch
                report is printed for each rejection. This is the tool to reach for after
                updating osu!lazer: the errors name the properties that changed.
                """);
            return args.Length == 0 ? 1 : 0;
        }

        string lazerDir = LazerDatabase.ResolveDataDirectory(RequireLazer(args));

        int? only = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--schema-version" && int.TryParse(args[i + 1], out int parsed))
                only = parsed;
        }

        int[] candidates = only is { } single ? new[] { single } : BuildCandidates();

        Console.WriteLine($"client.realm : {Path.Combine(lazerDir, "client.realm")}");
        Console.WriteLine($"compiled for : schema {LazerDatabase.SupportedSchemaVersion}");
        Console.WriteLine();

        foreach (int candidate in candidates)
        {
            try
            {
                var snapshot = LazerDatabase.Read(lazerDir, candidate);

                Console.WriteLine($"v{candidate}: MATCH");
                Console.WriteLine($"  beatmap sets : {snapshot.SongSets.Count}");
                Console.WriteLine($"  named files  : {snapshot.TotalNamedFiles}");
                Console.WriteLine($"  distinct hash: {snapshot.DistinctHashes}");
                return 0;
            }
            catch (LazerSchemaMismatchException ex)
            {
                Console.WriteLine($"v{candidate}: rejected");
                Console.WriteLine(Indent(ex.Message));
            }
        }

        return 3;
    }

    private static int[] BuildCandidates()
    {
        var versions = new List<int> { LazerDatabase.SupportedSchemaVersion };

        for (int v = LazerDatabase.SupportedSchemaVersion - 1; v >= 40; v--)
            versions.Add(v);

        return versions.ToArray();
    }

    private static string RequireLazer(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--lazer")
                return args[i + 1];
        }

        throw new CommandLineException("missing required option '--lazer'.");
    }

    private static string Indent(string text)
        => string.Join("\n", text.Split('\n').Select(l => "  " + l.TrimEnd()));
}
