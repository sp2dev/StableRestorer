using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using StableRestorer.Cli;
using StableRestorer.Engine;
using StableRestorer.IO;
using StableRestorer.Lazer;

namespace StableRestorer;

public static class Program
{
    private static readonly JsonSerializerOptions json_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            // Launched by double-clicking the executable (no arguments) and attached to a real
            // terminal: start the interactive prompt. A redirected stdin means this is a scripted
            // invocation, where printing usage is the only safe thing to do.
            if (args.Length == 0)
            {
                if (Console.IsInputRedirected)
                {
                    Console.WriteLine(CommandLine.Usage);
                    return 1;
                }

                return Wizard.Run();
            }

            string command = args[0].ToLowerInvariant();

            if (args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(CommandLine.Usage);
                return 0;
            }

            if (args.Any(a => a is "-h" or "--help"))
            {
                Console.WriteLine(CommandLine.Usage);
                return 0;
            }

            // Asking for it explicitly always honours the request, even with a piped stdin
            // (which is how the interactive flow is scripted in tests).
            if (command is "interactive" or "wizard" or "i")
                return Wizard.Run();

            return command switch
            {
                "restore" => RunRestore(args[1..]),
                "scan" => RunScan(args[1..]),
                "schemas" or "schema" => SchemaProbe.Run(args[1..]),
                "osudb" => RunOsuDb(args[1..]),
                _ => Fail($"unknown command '{args[0]}'."),
            };
        }
        catch (CommandLineException ex)
        {
            return Fail(ex.Message);
        }
        catch (LazerSchemaMismatchException ex)
        {
            Console.Error.WriteLine("error: could not open client.realm.");
            Console.Error.WriteLine("       " + ex.Message.Replace("\n", "\n       "));
            Console.Error.WriteLine();
            Console.Error.WriteLine("       The declared Realm schema must match the schema stored inside the file.");
            Console.Error.WriteLine("       Use --schema-version <n> if you know the version, or point --lazer at a");
            Console.Error.WriteLine("       database written by the osu!lazer version this tool supports.");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine($"run '{CommandLine.ToolName} --help' for usage.");
        return 2;
    }

    /// <summary>
    /// Reads osu!stable's <c>osu!.db</c> and reports the folder names it recorded. Useful for
    /// cross-checking the set-id based folder reuse, and for the few folders whose names carry no
    /// set id.
    /// </summary>
    private static int RunOsuDb(string[] args)
    {
        string stableRoot = TryGet(args, "--stable", out string? stableArg) && !string.IsNullOrWhiteSpace(stableArg)
            ? Path.GetFullPath(stableArg!)
            : throw new CommandLineException("missing required option '--stable'.");

        string dbPath = Path.Combine(stableRoot, "osu!.db");

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"error: no osu!.db found at {dbPath}");
            return 1;
        }

        OsuDb.Summary summary;

        try
        {
            summary = OsuDb.Read(dbPath);
        }
        catch (OsuDb.FormatNotUnderstoodException ex)
        {
            Console.Error.WriteLine($"osu!.db at {dbPath} could not be decoded: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("This reader is experimental and refuses to report folder names it cannot verify.");
            Console.Error.WriteLine("The restore itself does not depend on it: folder matching uses the set id prefix.");
            return 6;
        }

        var folders = summary.FolderNames.ToList();

        Console.WriteLine($"osu!.db     : {dbPath}");
        Console.WriteLine($"version     : {summary.Version}");
        Console.WriteLine($"player      : {summary.PlayerName}");
        Console.WriteLine($"declared folders : {summary.FolderCount}");
        Console.WriteLine($"beatmaps    : {summary.BeatmapCount}");
        Console.WriteLine($"folders     : {folders.Count}");

        int onDisk = folders.Count(f => Directory.Exists(Path.Combine(stableRoot, "Songs", f)));
        Console.WriteLine($"folders present on disk : {onDisk}");
        Console.WriteLine($"folders missing on disk : {folders.Count - onDisk}");

        if (TryGet(args, "--set-id", out string? setIdArg) && int.TryParse(setIdArg, out int wanted))
        {
            Console.WriteLine();
            Console.WriteLine($"entries with set id {wanted}:");

            foreach (var entry in summary.Entries.Where(e => e.SetId == wanted))
                Console.WriteLine($"  folder='{entry.FolderName}'  mapId={entry.BeatmapId}  diff='{entry.DifficultyName}'");
        }

        Console.WriteLine();
        Console.WriteLine("first 10 recorded folder names:");

        foreach (string folder in folders.Take(10))
            Console.WriteLine($"  {folder}");

        return 0;
    }

    private static int RunScan(string[] args)    {
        string lazerDir = LazerDatabase.ResolveDataDirectory(Require(args, "--lazer"));
        int schema = ResolveSchema(lazerDir, args);

        Console.WriteLine($"osu!lazer data : {lazerDir}");
        Console.WriteLine($"schema version : {schema}");

        return Scan(lazerDir, schema);
    }

    /// <summary>
    /// Read-only survey of a lazer database: what exists, what a restore would need, and whether
    /// every referenced hash resolves. Shared with the interactive mode.
    /// </summary>
    public static int Scan(string lazerDir, int schema)
    {
        var snapshot = LazerDatabase.Read(lazerDir, schema);

        Console.WriteLine();
        Console.WriteLine($"beatmap sets to restore : {snapshot.SongSets.Count}");
        Console.WriteLine($"  skipped (delete pending): {snapshot.DeletedSetCount}");
        Console.WriteLine($"  skipped (no files)      : {snapshot.EmptySetCount}");
        Console.WriteLine($"  protected/internal sets : {snapshot.ProtectedSetCount} (bundled with osu!lazer)");
        Console.WriteLine($"  sets without set id     : {snapshot.SongSets.Count(s => s.SetId <= 0)}  (skipped by default; see --keep-unsubmitted)");
        Console.WriteLine($"distinct set ids        : {snapshot.DistinctSetIds}  (BeatmapSetInfo.OnlineID)");
        if (snapshot.DuplicateSetIds > 0)
            Console.WriteLine($"  !! duplicate set ids   : {snapshot.DuplicateSetIds} -> {string.Join(", ", snapshot.DuplicateSetIdList.Take(10))}");
        Console.WriteLine($"distinct map ids        : {snapshot.DistinctMapIds}  (BeatmapInfo.OnlineID)");
        Console.WriteLine($"named files             : {snapshot.TotalNamedFiles}");
        Console.WriteLine($"distinct hashes         : {snapshot.DistinctHashes}");

        // Validate that every referenced hash resolves to a real file in the lazer store.
        var missing = new List<(string Hash, string Folder)>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        int present = 0;
        long bytes = 0;
        long distinctBytes = 0;

        foreach (var set in snapshot.SongSets)
        {
            string folder = StableNaming.BeatmapFolderName(set.SetId, set.Artist, set.Title);

            foreach (var file in set.Files)
            {
                string path = LazerDatabase.HashPath(lazerDir, file.Hash);

                if (File.Exists(path))
                {
                    present++;
                    long length = new FileInfo(path).Length;
                    bytes += length;

                    if (distinct.Add(file.Hash))
                        distinctBytes += length;
                }
                else
                {
                    missing.Add((file.Hash, folder));
                }
            }
        }

        Console.WriteLine($"hashed files present    : {present}");
        Console.WriteLine($"hashed files missing    : {missing.Count}");
        Console.WriteLine($"bytes referenced        : {bytes:N0} ({bytes / 1024.0 / 1024 / 1024:N2} GiB)");
        Console.WriteLine($"distinct bytes on disk  : {distinctBytes:N0} ({distinctBytes / 1024.0 / 1024 / 1024:N2} GiB)");
        Console.WriteLine("                          (this is what a hard-link restore really costs)");

        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("first missing hashes:");

            foreach (var (hash, folder) in missing.Take(10))
                Console.WriteLine($"  {hash}  ({folder})");
        }

        Console.WriteLine();
        Console.WriteLine("sample of the folder layout that a restore would create:");

        foreach (var set in snapshot.SongSets.Take(5))
            Console.WriteLine($"  Songs/{StableNaming.BeatmapFolderName(set.SetId, set.Artist, set.Title)}/  ({set.Files.Count} files)");

        return missing.Count > 0 ? 4 : 0;
    }

    private static int RunRestore(string[] args)
    {
        string lazerDir = LazerDatabase.ResolveDataDirectory(Require(args, "--lazer"));
        string outputDir = Path.GetFullPath(Require(args, "--out"));

        var mode = RestoreMode.HardLink;

        if (TryGet(args, "--mode", out string? modeArg))
        {
            mode = modeArg!.ToLowerInvariant() switch
            {
                "hardlink" or "hard-link" or "link" => RestoreMode.HardLink,
                "copy" => RestoreMode.Copy,
                _ => throw new CommandLineException($"--mode must be 'hardlink' or 'copy' (got '{modeArg}')."),
            };
        }

        // An existing osu!stable install whose folder names should be adopted. Defaults to the
        // output directory so a dry run against a populated --out still detects what is there.
        string stableDir = TryGet(args, "--stable", out string? stableArg) && !string.IsNullOrWhiteSpace(stableArg)
            ? Path.GetFullPath(stableArg!)
            : outputDir;

        var options = new RestoreOptions
        {
            LazerDataDirectory = lazerDir,
            OutputDirectory = outputDir,
            StableDirectory = stableDir,
            ReuseExistingFolders = !Has(args, "--no-reuse-existing"),

            // Maps without an online id have no stable counterpart, so they are skipped by default.
            // --keep-unsubmitted imports them anyway.
            SkipUnsubmitted = !Has(args, "--keep-unsubmitted"),
            Mode = mode,
            DryRun = Has(args, "--dry-run"),
            Overwrite = Has(args, "--overwrite"),
            VerifyHashes = !Has(args, "--no-verify-hash"),
            Verbose = Has(args, "--verbose"),
            Quiet = Has(args, "--quiet"),
        };

        // Fail before touching the database if the output could reach into the lazer data directory.
        options.ValidateDirectories();

        int schema = ResolveSchema(lazerDir, args);

        Console.WriteLine($"{CommandLine.ToolName} {CommandLine.Version}");
        Console.WriteLine($"lazer data : {lazerDir}  (read-only)");
        Console.WriteLine($"output     : {outputDir}{(options.DryRun ? "  [dry run]" : string.Empty)}");
        Console.WriteLine($"stable     : {stableDir}{(options.ReuseExistingFolders ? "  (set ids are matched against existing Songs/)" : "  (folder matching disabled)")}");
        Console.WriteLine($"mode       : {options.Mode.ToString().ToLowerInvariant()}");
        Console.WriteLine($"protected  : {(options.SkipUnsubmitted ? "unsubmitted maps skipped" : "unsubmitted maps kept")}");
        Console.WriteLine($"schema     : {schema}");
        Console.WriteLine();

        string reportPath = TryGet(args, "--report", out string? reportArg)
            ? Path.GetFullPath(reportArg!)
            : Path.Combine(outputDir, "stablerestorer-report.json");

        return ExecuteRestore(options, schema, reportPath);
    }

    /// <summary>
    /// Shared by the CLI and the interactive mode: reads the database, runs the restore and writes
    /// the JSON report. Returns the process exit code.
    /// </summary>
    public static int ExecuteRestore(RestoreOptions options, int schema, string reportPath)
    {
        var readWatch = Stopwatch.StartNew();
        var snapshot = LazerDatabase.Read(options.LazerDataDirectory, schema);
        readWatch.Stop();

        // Create the output tree up front so the JSON report can always be written, even on a dry run.
        Directory.CreateDirectory(options.OutputDirectory);

        Console.WriteLine($"read {snapshot.SongSets.Count} beatmap sets / {snapshot.TotalNamedFiles} named files " +
                          $"from Realm in {readWatch.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine();

        var engine = new RestoreEngine(options, snapshot);
        var report = engine.Run();

        if (options.Progress == null)
            Console.Error.WriteLine();

        PrintSummary(report);

        try
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json_options));
            Console.WriteLine($"JSON report: {reportPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not write report to '{reportPath}': {ex.Message}");
        }

        if (report.HasProblems)
        {
            Console.WriteLine();
            Console.WriteLine("problems were recorded - review the 'notices' section of the report.");
            return 5;
        }

        return 0;
    }

    /// <summary>Also used by the interactive mode.</summary>
    public static void PrintSummary(RestoreReport report)
    {
        Console.WriteLine("summary");
        Console.WriteLine($"  packages planned        : {report.PackagesPlanned}");
        Console.WriteLine($"  packages created        : {report.PackagesCreated}");
        Console.WriteLine($"  merged into existing    : {report.PackagesReusedExistingFolder}   (matched by set id)");
        Console.WriteLine($"  skipped (unsubmitted)   : {report.PackagesSkippedUnsubmitted}   (maps without an online id)");
        Console.WriteLine($"  files planned           : {report.FilesPlanned}");
        Console.WriteLine($"  hard links created      : {report.FilesLinked}");
        Console.WriteLine($"  copied                  : {report.FilesCopied}");
        Console.WriteLine($"  copied after link fail  : {report.FilesCopiedAfterLinkFailure}");
        Console.WriteLine($"  already hard-linked     : {report.FilesAlreadyLinked}");
        Console.WriteLine($"  skipped (existing)      : {report.FilesSkippedExisting}");
        Console.WriteLine($"  planned only (dry run)  : {report.FilesPlannedOnly}");
        Console.WriteLine($"  missing source          : {report.FilesSourceMissing}");
        Console.WriteLine($"  source hash mismatch    : {report.FilesSourceHashMismatch}");
        Console.WriteLine($"  unsafe paths            : {report.FilesUnsafePath}");
        Console.WriteLine($"  failures                : {report.FilesFailed}");
        Console.WriteLine($"  bytes referenced        : {report.Bytes:N0} ({report.Bytes / 1024.0 / 1024 / 1024:N2} GiB)");
        Console.WriteLine($"  bytes actually consumed : {report.BytesWritten:N0} ({report.BytesWritten / 1024.0 / 1024 / 1024:N2} GiB)");
        Console.WriteLine($"  elapsed                 : {report.DurationSeconds:N1}s");
    }

    /// <summary>
    /// Determines which Realm schema version to declare. The version is stored inside the file, so
    /// an explicit value is used verbatim and otherwise the candidates are probed in order.
    /// </summary>
    public static int ResolveSchema(string lazerDir, string[] args)
    {
        if (TryGet(args, "--schema-version", out string? explicitArg))
        {
            if (!int.TryParse(explicitArg, out int parsed) || parsed <= 0)
                throw new CommandLineException($"--schema-version must be a positive integer (got '{explicitArg}').");

            return parsed;
        }

        int[] candidates = { LazerDatabase.SupportedSchemaVersion, 51, 50, 49, 48, 47, 46, 45, 44, 43, 42 };

        var failures = new List<string>();

        foreach (int candidate in candidates)
        {
            try
            {
                // The snapshot is a plain records graph with no unmanaged handles; the Realm
                // instance itself is disposed inside Read().
                _ = LazerDatabase.Read(lazerDir, candidate);
                return candidate;
            }
            catch (LazerSchemaMismatchException ex)
            {
                failures.Add($"  v{candidate}: {FirstLine(ex.Message)}");
            }
        }

        if (Has(args, "--verbose"))
        {
            Console.Error.WriteLine("schema probe results:");
            failures.ForEach(f => Console.Error.WriteLine(f));
        }

        throw new LazerSchemaMismatchException(
            $"none of the known schema versions ({string.Join(", ", candidates)}) match this database. " +
            "Pass --schema-version with the version written by the osu!lazer build that created it. " +
            "Re-run with --verbose to see why each candidate was rejected.");
    }

    private static string FirstLine(string message)
    {
        int index = message.IndexOf('\n');
        return (index < 0 ? message : message[..index]).Trim();
    }

    private static string Require(string[] args, string name)
        => TryGet(args, name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value!
            : throw new CommandLineException($"missing required option '{name}'.");

    private static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool TryGet(string[] args, string name, out string? value)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
            return true;
        }

        value = null;
        return false;
    }
}
