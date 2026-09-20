using StableRestorer.IO;

namespace StableRestorer.Engine;

public sealed record RestoreOptions
{
    /// <summary>Directory containing <c>client.realm</c> and <c>files/</c>.</summary>
    public required string LazerDataDirectory { get; init; }

    /// <summary>Directory that receives <c>Songs/</c> (and, in future, <c>Skins/</c>).</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Directory whose existing <c>Songs/</c> layout is examined when
    /// <see cref="ReuseExistingFolders"/> is set. Defaults to <see cref="OutputDirectory"/>.
    /// Point it at an existing osu!stable install to adopt that install's folder names.
    /// </summary>
    public string? StableDirectory { get; init; }

    /// <summary>
    /// Key sets on <c>BeatmapSetInfo.OnlineID</c> and reuse the folder name of an already existing
    /// stable folder for the same set id instead of creating a second folder under this tool's own
    /// naming scheme.
    /// </summary>
    public bool ReuseExistingFolders { get; init; } = true;

    /// <summary>
    /// Do not import beatmap sets that have no online id (<c>BeatmapSetInfo.OnlineID &lt;= 0</c>),
    /// i.e. maps that were never submitted to osu!. They have no stable equivalent - this includes
    /// the maps osu!lazer bundles with the game - and importing them only clutters the library.
    /// A set that this stable install already has is still filled in rather than skipped.
    /// </summary>
    public bool SkipUnsubmitted { get; init; }

    public RestoreMode Mode { get; init; } = RestoreMode.HardLink;

    public bool DryRun { get; init; }

    /// <summary>Replace existing destination files whose content is not the same file as the source.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Verify SHA-256 of every source file against its name before linking.</summary>
    public bool VerifyHashes { get; init; } = true;

    public bool Verbose { get; init; }

    public bool Quiet { get; init; }

    /// <summary>
    /// Invoked with (filesProcessed, filesTotal) as the run advances. The interactive mode uses
    /// this to draw its progress indicator; the CLI leaves it null and reports on stderr instead.
    /// </summary>
    public Action<int, int>? Progress { get; init; }

    /// <summary>
    /// Rejects any combination where the output directory could reach into the read-only
    /// osu!lazer data directory. Called before the database is opened so a bad target fails fast.
    /// </summary>
    public void ValidateDirectories()
    {
        string lazer = Path.GetFullPath(LazerDataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string output = Path.GetFullPath(OutputDirectory).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(lazer, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output directory must not be the osu!lazer data directory.");

        if (output.StartsWith(lazer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output directory must not live inside the osu!lazer data directory.");

        if (lazer.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output directory must not contain the osu!lazer data directory.");
    }
}

public sealed record PackageSummary(string Folder, int Files, int Linked, int Copied, int Skipped, int Missing, int Failed);

/// <summary>One osu!stable <c>Songs/</c> folder that already exists and was matched by set id.</summary>
public sealed record ExistingFolder(string Folder, int SetId, int FilesOnDisk, bool MatchedById);

/// <summary>One beatmap set that was left out because it has no online id.</summary>
public sealed record SkippedUnsubmittedSet(int SetId, string Artist, string Title, bool BundledWithLazer);

public sealed record RestoreReport
{
    public required string LazerDataDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string Mode { get; init; }
    public bool DryRun { get; init; }
    public int SchemaVersion { get; init; }
    public required string StartedAt { get; init; }
    public required string FinishedAt { get; init; }
    public double DurationSeconds { get; init; }

    public int PackagesPlanned { get; init; }
    public int PackagesCreated { get; init; }

    /// <summary>Sets whose files were merged into an already existing stable folder.</summary>
    public int PackagesReusedExistingFolder { get; init; }

    /// <summary>Sets left out because they have no online id.</summary>
    public int PackagesSkippedUnsubmitted { get; init; }

    public int FilesPlanned { get; init; }
    public int FilesLinked { get; init; }
    public int FilesCopied { get; init; }
    public int FilesCopiedAfterLinkFailure { get; init; }
    public int FilesAlreadyLinked { get; init; }
    public int FilesSkippedExisting { get; init; }
    public int FilesReplaced { get; init; }
    public int FilesPlannedOnly { get; init; }
    public int FilesSourceMissing { get; init; }
    public int FilesSourceHashMismatch { get; init; }
    public int FilesFailed { get; init; }
    public int FilesUnsafePath { get; init; }

    /// <summary>Total size of the files this run created or planned to create.</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Bytes actually consumed by this run: sums only files that were newly written, and counts an
    /// already-present hard link as zero. This is the honest "disk cost" of the restore.
    /// </summary>
    public long BytesWritten { get; init; }

    public required IReadOnlyList<PackageSummary> Packages { get; init; }

    /// <summary>Existing stable folders that were detected (by set id) before restoring.</summary>
    public required IReadOnlyList<ExistingFolder> ExistingFolders { get; init; }

    /// <summary>Sets that were deliberately not restored because they have no online id.</summary>
    public required IReadOnlyList<SkippedUnsubmittedSet> SkippedUnsubmitted { get; init; }

    /// <summary>Non-routine events: failures, conflicts, hash mismatches, unsafe paths.</summary>
    public required IReadOnlyList<FileResult> Notices { get; init; }

    public bool HasProblems => FilesFailed > 0 || FilesSourceMissing > 0 || FilesSourceHashMismatch > 0 || FilesUnsafePath > 0;
}
