using System.Diagnostics;
using StableRestorer.IO;
using StableRestorer.Lazer;

namespace StableRestorer.Engine;

/// <summary>
/// Turns a <see cref="LazerSnapshot"/> into an osu!stable <c>Songs/</c> tree.
///
/// Guarantees:
/// <list type="bullet">
/// <item>the lazer data directory is only ever read;</item>
/// <item>nothing is written outside the output directory;</item>
/// <item>an existing destination that is already the same file (an existing hard link) is left untouched.</item>
/// </list>
/// </summary>
public sealed class RestoreEngine
{
    private readonly RestoreOptions _options;
    private readonly LazerSnapshot _snapshot;
    private readonly FileRestorer _restorer;
    private readonly List<PackageSummary> _packages = new();
    private readonly List<FileResult> _notices = new();
    private readonly List<SkippedUnsubmittedSet> _skippedUnsubmitted = new();
    private readonly HashSet<string> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Existing stable folders, keyed by set id.</summary>
    private readonly Dictionary<int, ExistingFolder> _existingBySetId = new();

    private readonly List<ExistingFolder> _existingFolders = new();

    private int _filesLinked;
    private int _filesCopied;
    private int _filesCopiedAfterLinkFailure;
    private int _filesAlreadyLinked;
    private int _filesSkippedExisting;
    private int _filesReplaced;
    private int _filesPlannedOnly;
    private int _filesSourceMissing;
    private int _filesSourceHashMismatch;
    private int _filesFailed;
    private int _filesUnsafePath;
    private long _bytes;
    private long _bytesWritten;
    private int _processed;
    private int _packagesReusedExistingFolder;

    private readonly string _songsDirectory;
    private readonly string _stableSongsDirectory;

    public RestoreEngine(RestoreOptions options, LazerSnapshot snapshot)
    {
        _options = options;
        _snapshot = snapshot;
        _restorer = new FileRestorer(options);
        _songsDirectory = Path.Combine(options.OutputDirectory, "Songs");
        _stableSongsDirectory = Path.Combine(options.StableDirectory ?? options.OutputDirectory, "Songs");

        options.ValidateDirectories();
        ScanExistingFolders();
    }

    /// <summary>
    /// Indexes the songs already present in the stable install. osu!stable names a beatmap folder
    /// <c>{setId} {artist} - {title}</c>, so the leading integer identifies the set. This is how a
    /// set is recognised without duplicating a folder that is already there under a different name.
    /// </summary>
    private void ScanExistingFolders()
    {
        if (!_options.ReuseExistingFolders)
            return;

        if (!Directory.Exists(_stableSongsDirectory))
            return;

        foreach (string directory in Directory.EnumerateDirectories(_stableSongsDirectory))
        {
            string name = Path.GetFileName(directory);

            int? setId = StableNaming.TryParseSetIdFromFolderName(name);

            if (setId is not > 0)
                continue;

            if (_existingBySetId.ContainsKey(setId.Value))
            {
                _notices.Add(new FileResult(name, string.Empty, string.Empty, FileOutcome.SkippedExisting,
                    $"two existing stable folders claim set id {setId}; keeping the first"));
                continue;
            }

            var folder = new ExistingFolder(name, setId.Value, CountFiles(directory), MatchedById: true);

            _existingBySetId[setId.Value] = folder;
            _existingFolders.Add(folder);
        }
    }

    private static int CountFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return -1;
        }
    }

    public RestoreReport Run()
    {
        var stopwatch = Stopwatch.StartNew();
        string startedAt = DateTimeOffset.Now.ToString("O");

        if (!_options.DryRun)
            Directory.CreateDirectory(_songsDirectory);

        // Folder names in use, seeded with the stable install's own folders so nothing is duplicated.
        var usedFolders = new HashSet<string>(_existingFolders.Select(f => f.Folder), StringComparer.OrdinalIgnoreCase);

        foreach (var set in _snapshot.SongSets)
        {
            if (ShouldSkipUnsubmitted(set))
                continue;

            string folder = ResolveFolder(set, usedFolders);
            RestorePackage(set, folder);
        }

        stopwatch.Stop();

        return new RestoreReport
        {
            LazerDataDirectory = _options.LazerDataDirectory,
            OutputDirectory = _options.OutputDirectory,
            Mode = _options.Mode.ToString(),
            DryRun = _options.DryRun,
            SchemaVersion = _snapshot.SchemaVersion,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now.ToString("O"),
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            PackagesPlanned = _snapshot.SongSets.Count,
            PackagesCreated = _packages.Count,
            PackagesReusedExistingFolder = _packagesReusedExistingFolder,
            PackagesSkippedUnsubmitted = _skippedUnsubmitted.Count,
            FilesPlanned = _snapshot.TotalNamedFiles,
            FilesLinked = _filesLinked,
            FilesCopied = _filesCopied,
            FilesCopiedAfterLinkFailure = _filesCopiedAfterLinkFailure,
            FilesAlreadyLinked = _filesAlreadyLinked,
            FilesSkippedExisting = _filesSkippedExisting,
            FilesReplaced = _filesReplaced,
            FilesPlannedOnly = _filesPlannedOnly,
            FilesSourceMissing = _filesSourceMissing,
            FilesSourceHashMismatch = _filesSourceHashMismatch,
            FilesFailed = _filesFailed,
            FilesUnsafePath = _filesUnsafePath,
            Bytes = _bytes,
            BytesWritten = _bytesWritten,
            Packages = _packages,
            ExistingFolders = _existingFolders,
            SkippedUnsubmitted = _skippedUnsubmitted,
            Notices = _notices,
        };
    }

    /// <summary>
    /// A set is "unsubmitted" when it has no beatmap set id, i.e. it was never uploaded to osu!.
    /// Those have no stable counterpart (this covers the maps osu!lazer bundles with the game).
    /// </summary>
    private bool ShouldSkipUnsubmitted(SongSnapshot set)
    {
        if (!_options.SkipUnsubmitted || set.SetId > 0)
            return false;

        // Note: _existingBySetId only ever holds positive ids, so an unsubmitted set can never
        // match an existing stable folder; nothing to preserve here.
        _skippedUnsubmitted.Add(new SkippedUnsubmittedSet(set.SetId, set.Artist, set.Title, set.Protected));

        if (_options.Verbose)
            Console.Error.WriteLine($"  skip (no online id) {Describe(set)}");

        return true;
    }

    /// <summary>Produces the destination folder name for a set, preferring an existing stable folder.</summary>
    private string ResolveFolder(SongSnapshot set, HashSet<string> used)
    {
        if (set.SetId > 0 && _existingBySetId.TryGetValue(set.SetId, out var existing))
        {
            _packagesReusedExistingFolder++;
            return existing.Folder;
        }

        return UniqueFolder(set, used);
    }

    private string UniqueFolder(SongSnapshot set, HashSet<string> used)
    {
        string baseName = StableNaming.BeatmapFolderName(set.SetId, set.Artist, set.Title);

        if (used.Add(baseName))
            return baseName;

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} ({i})";

            if (used.Add(candidate))
            {
                _notices.Add(new FileResult(baseName, string.Empty, string.Empty, FileOutcome.SkippedExisting,
                    $"folder name collision; using '{candidate}'"));
                return candidate;
            }
        }
    }

    private static string Describe(SongSnapshot set)
        => $"set {set.SetId} {set.Artist} - {set.Title}".Trim();

    private void RestorePackage(SongSnapshot set, string folder)
    {
        string packageDirectory = Path.Combine(_songsDirectory, folder);

        int linked = 0, copied = 0, skipped = 0, missing = 0, failed = 0;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Nested Realm filenames (storyboard assets, etc.) resolve under here, and
        // Directory.CreateDirectory creates intermediate levels, so one call is enough.
        if (!_options.DryRun)
            EnsureDirectory(packageDirectory);

        foreach (var file in set.Files)
        {
            string relative = file.RelativePath;

            // Two Realm entries can legitimately map to the same destination name after
            // sanitisation; keep the first and report the rest instead of silently overwriting.
            if (!seenNames.Add(relative))
            {
                _filesSkippedExisting++;
                skipped++;
                _notices.Add(new FileResult(folder, relative, file.Hash, FileOutcome.SkippedExisting,
                    "duplicate filename inside this beatmap set"));
                continue;
            }

            if (!TryResolveDestination(packageDirectory, relative, out string destination, out string? reason))
            {
                _filesUnsafePath++;
                failed++;
                _notices.Add(new FileResult(folder, relative, file.Hash, FileOutcome.Failed, reason));
                continue;
            }

            string source = LazerDatabase.HashPath(_options.LazerDataDirectory, file.Hash);
            var result = _restorer.Restore(folder, destination, source, file.Hash);
            long size = SafeLength(source);

            // "Bytes referenced" counts every planned file; "bytes written" only counts content that
            // genuinely consumed new space, so a fresh hard link adds nothing - the data already
            // exists in the lazer store and the link is just another name for it.
            _bytes += size;

            switch (result.Outcome)
            {
                case FileOutcome.Linked:
                    _filesLinked++;
                    linked++;
                    break;

                case FileOutcome.Copied:
                    _filesCopied++;
                    copied++;
                    _bytesWritten += size;
                    break;

                case FileOutcome.CopiedAfterLinkFailure:
                    _filesCopiedAfterLinkFailure++;
                    copied++;
                    _bytesWritten += size;
                    _notices.Add(result with
                    {
                        Detail = "hard link unavailable (different volume, or the file already holds " +
                                 $"NTFS's maximum of 1024 links); copied instead: {result.Detail}",
                    });
                    break;

                case FileOutcome.Replaced:
                    _filesReplaced++;
                    copied++;
                    _bytesWritten += size;
                    break;

                case FileOutcome.AlreadyLinked:
                    _filesAlreadyLinked++;
                    skipped++;
                    break;

                case FileOutcome.SkippedExisting:
                    _filesSkippedExisting++;
                    skipped++;
                    _notices.Add(result);
                    break;

                case FileOutcome.PlannedOnly:
                    _filesPlannedOnly++;
                    linked++;

                    // A dry run writes nothing, so it consumes nothing either.
                    break;

                case FileOutcome.SourceMissing:
                    _filesSourceMissing++;
                    missing++;
                    _notices.Add(result);
                    break;

                case FileOutcome.SourceHashMismatch:
                    _filesSourceHashMismatch++;
                    failed++;
                    _notices.Add(result with { Detail = $"content does not match its SHA-256 name ({result.Detail})" });
                    break;

                case FileOutcome.Failed:
                    _filesFailed++;
                    failed++;
                    _notices.Add(result);
                    break;
            }

            Progress();
        }

        // Keep a folder for every set that produced at least one file, so partially restored
        // sets are still importable by stable.
        _packages.Add(new PackageSummary(folder, set.Files.Count, linked, copied, skipped, missing, failed));

        if (_options.Verbose)
            Console.Error.WriteLine($"  {folder}: {linked + copied} written, {skipped} skipped, {missing} missing, {failed} failed");
    }

    private void EnsureDirectory(string path)
    {
        if (_createdDirectories.Add(path))
            Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Builds the absolute destination path, refusing anything that would escape the package folder.
    /// </summary>
    private static bool TryResolveDestination(string packageDirectory, string relative, out string destination, out string? reason)
    {
        destination = string.Empty;
        reason = null;

        if (string.IsNullOrWhiteSpace(relative))
        {
            reason = "empty filename";
            return false;
        }

        if (Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
        {
            reason = "absolute path in Realm filename";
            return false;
        }

        string[] segments = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                reason = "path traversal in Realm filename";
                return false;
            }
        }

        if (segments.Length == 0)
        {
            reason = "empty filename after normalisation";
            return false;
        }

        destination = Path.Combine(packageDirectory, Path.Combine(segments));

        string packageFull = Path.GetFullPath(packageDirectory + Path.DirectorySeparatorChar);
        string destinationFull = Path.GetFullPath(destination);

        if (!destinationFull.StartsWith(packageFull, StringComparison.OrdinalIgnoreCase))
        {
            reason = "resolved path escapes the package directory";
            return false;
        }

        return true;
    }

    private void Progress()
    {
        _processed++;

        if (_options.Quiet)
            return;

        if (_options.Progress != null)
        {
            if (_processed % 100 == 0 || _processed == _snapshot.TotalNamedFiles)
                _options.Progress(_processed, _snapshot.TotalNamedFiles);

            return;
        }

        if (_processed % 200 != 0)
            return;

        Console.Error.Write($"\r  {_processed}/{_snapshot.TotalNamedFiles} files " +
                            $"(linked {_filesLinked}, copied {_filesCopied}, " +
                            $"existing {_filesAlreadyLinked + _filesSkippedExisting}, " +
                            $"missing {_filesSourceMissing}, failed {_filesFailed})        ");
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
