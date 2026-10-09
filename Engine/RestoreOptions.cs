using StableRestorer.IO;

namespace StableRestorer.Engine;

/// <summary>Which kinds of local data a run is allowed to migrate.</summary>
public sealed record MigrateSelection(bool Songs, bool Skins, bool Replays)
{
    public static MigrateSelection All => new(true, true, true);

    public bool Any => Songs || Skins || Replays;

    public IEnumerable<string> Names()
    {
        if (Songs)
            yield return "谱面";

        if (Skins)
            yield return "皮肤";

        if (Replays)
            yield return "回放";
    }

    public override string ToString() => string.Join("、", Names());
}

/// <summary>三类数据各自的落点：<c>&lt;输出目录&gt;/Songs</c>、<c>/Skins</c>、<c>/Replays</c>。</summary>
public sealed record MigrateTargets(string SongsDirectory, string SkinsDirectory, string ReplaysDirectory);

public sealed record RestoreOptions
{
    /// <summary>Directory containing <c>client.realm</c> and <c>files/</c>.</summary>
    public required string LazerDataDirectory { get; init; }

    /// <summary>
    /// Directory that receives the migrated trees. May be empty when <see cref="ScanOnly"/> is set,
    /// since a statistics-only run never writes and therefore has no target.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Directory whose existing layout is examined when <see cref="ReuseExistingFolders"/> is set.
    /// Defaults to <see cref="OutputDirectory"/>. Point it at an existing osu!stable install to
    /// adopt that install's folder names.
    /// </summary>
    public string? StableDirectory { get; init; }

    /// <summary>What to migrate.</summary>
    public MigrateSelection Selection { get; init; } = MigrateSelection.All;

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
    /// </summary>
    public bool SkipUnsubmitted { get; init; } = true;

    public RestoreMode Mode { get; init; } = RestoreMode.HardLink;

    /// <summary>
    /// 体检（只读）：只读数据库、只统计与检查，**不写任何文件、也不创建任何目录**。
    /// 目标目录还没指定时也能跑（此时 <see cref="CheckTargets"/> 为假）。
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// 是否连同目标目录一起检查：目标可写性、硬链接可行性、以及每个目标路径是否会被安全检查拒绝。
    /// 只在 <see cref="ReadOnly"/> 下有区别 —— 纯统计不需要目标目录。
    /// </summary>
    public bool CheckTargets { get; init; }

    /// <summary>Replace existing destination files whose content is not the same file as the source.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Verify SHA-256 of every source file against its name before linking.</summary>
    public bool VerifyHashes { get; init; } = true;

    public bool Verbose { get; init; }

    public bool Quiet { get; init; }

    /// <summary>
    /// Invoked with (filesProcessed, filesTotal, currentCategory) as the run advances. Both the CLI
    /// and the interactive mode use it to draw their progress indicator.
    /// </summary>
    public Action<int, int, string>? Progress { get; init; }

    /// <summary>Effective per-category target directories.</summary>
    public MigrateTargets ResolveTargets() => new(
        Path.Combine(OutputDirectory, "Songs"),
        Path.Combine(OutputDirectory, "Skins"),
        Path.Combine(OutputDirectory, "Replays"));

    /// <summary>
    /// Rejects any combination where the output directory could reach into the read-only
    /// osu!lazer data directory. Called before the database is opened so a bad target fails fast.
    /// </summary>
    public void ValidateDirectories()
    {
        string lazer = Path.GetFullPath(LazerDataDirectory).TrimEnd(Path.DirectorySeparatorChar);

        // 纯统计模式没有目标目录，也就没有需要校验的东西。
        if (ReadOnly && !CheckTargets)
            return;

        string output = Path.GetFullPath(OutputDirectory).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(lazer, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出目录不能就是 osu!lazer 的数据目录。");

        if (output.StartsWith(lazer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出目录不能位于 osu!lazer 数据目录之内。");

        if (lazer.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出目录不能包含 osu!lazer 数据目录。");
    }
}

/// <summary>Per-category totals. <c>Items</c> counts beatmap sets, skins or replays.</summary>
public sealed record CategorySummary(
    string Category,
    int Items,
    int ItemsSkipped,
    int FilesPlanned,
    int FilesLinked,
    int FilesCopied,
    int FilesSkipped,
    int FilesMissing,
    int FilesFailed,
    long BytesWritten);

public sealed record PackageSummary(string Folder, int Files, int Linked, int Copied, int Skipped, int Missing, int Failed);

/// <summary>One osu!stable folder that already exists and was matched by set id.</summary>
public sealed record ExistingFolder(string Folder, int SetId, int FilesOnDisk);

/// <summary>One beatmap set that was left out because it has no online id.</summary>
public sealed record SkippedUnsubmittedSet(int SetId, string Artist, string Title, bool BundledWithLazer);

/// <summary>A check result. <c>Level</c> is "ok", "warn" or "error".</summary>
public sealed record CheckFinding(string Level, string Message);

/// <summary>
/// 只读检查期间走一遍曲库得到的统计。体检的"有多少文件、缺多少、占多少空间"全部来自这里，
/// 早先 <c>Program.Scan()</c> 会把同一批文件再走一遍，两份统计还各写了一套 —— 现在只有这一份。
/// </summary>
public sealed record LibraryStats(
    int PresentFiles,
    int MissingFiles,
    int DistinctHashes,
    long ReferencedBytes,
    long DistinctBytes,
    IReadOnlyList<string> MissingSamples);

public sealed record RestoreReport
{
    public required string LazerDataDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string Mode { get; init; }
    public bool ReadOnly { get; init; }
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

    /// <summary>Skins skipped because osu!lazer bundles them (they own no files).</summary>
    public int SkinsSkippedProtected { get; init; }

    /// <summary>Local scores that have no replay file attached.</summary>
    public int ReplaysSkippedWithoutFile { get; init; }

    /// <summary>Local scores whose beatmap metadata is gone, so no stable filename can be built.</summary>
    public int ReplaysSkippedWithoutBeatmap { get; init; }

    public int FilesPlanned { get; init; }
    public int FilesLinked { get; init; }
    public int FilesCopied { get; init; }
    public int FilesCopiedAfterLinkFailure { get; init; }
    public int FilesAlreadyLinked { get; init; }
    public int FilesSkippedExisting { get; init; }
    public int FilesReplaced { get; init; }
    public int FilesSourceMissing { get; init; }
    public int FilesSourceHashMismatch { get; init; }
    public int FilesFailed { get; init; }
    public int FilesUnsafePath { get; init; }

    /// <summary>Total size of the files this run created or planned to create.</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Bytes actually consumed by this run: only content that genuinely took new space. A fresh
    /// hard link and a test run both count as zero.
    /// </summary>
    public long BytesWritten { get; init; }

    public required IReadOnlyList<CategorySummary> Categories { get; init; }

    /// <summary>Availability findings produced by a read-only run (empty for a real run).</summary>
    public required IReadOnlyList<CheckFinding> Findings { get; init; }

    /// <summary>曲库文件统计。只有只读运行才走这一步，真实迁移时为 null。</summary>
    public LibraryStats? Stats { get; init; }

    public required IReadOnlyList<PackageSummary> Packages { get; init; }

    /// <summary>Existing stable folders that were detected (by set id) before migrating.</summary>
    public required IReadOnlyList<ExistingFolder> ExistingFolders { get; init; }

    /// <summary>Sets that were deliberately not imported because they have no online id.</summary>
    public required IReadOnlyList<SkippedUnsubmittedSet> SkippedUnsubmitted { get; init; }

    /// <summary>Non-routine events: failures, conflicts, hash mismatches, unsafe paths.</summary>
    public required IReadOnlyList<FileResult> Notices { get; init; }

    public bool HasProblems => FilesFailed > 0 || FilesSourceMissing > 0 || FilesSourceHashMismatch > 0 || FilesUnsafePath > 0;

    /// <summary>只读检查有阻塞项：体检不通过时不该继续迁移。</summary>
    public bool Blocked => Findings.Any(f => f.Level == "error");
}
