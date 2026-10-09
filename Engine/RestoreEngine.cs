using System.Diagnostics;
using StableRestorer.IO;
using StableRestorer.Lazer;

namespace StableRestorer.Engine;

/// <summary>Running totals for one category (谱面 / 皮肤 / 回放).</summary>
internal sealed class CategoryStats
{
    public required string Name { get; init; }
    public int Items;
    public int ItemsSkipped;
    public int Planned;
    public int Linked;
    public int Copied;
    public int Skipped;
    public int Missing;
    public int Failed;
    public long BytesWritten;

    public CategorySummary ToSummary() => new(Name, Items, ItemsSkipped, Planned, Linked, Copied, Skipped, Missing, Failed, BytesWritten);
}

/// <summary>
/// Availability findings collected before a run: missing paths, unwritable targets, cross-volume
/// hard links, missing sources and files that would be blocked by their destination.
/// </summary>
internal sealed class AvailabilityReport
{
    private readonly List<CheckFinding> _findings = new();

    public IReadOnlyList<CheckFinding> Findings => _findings;

    public void Ok(string message) => _findings.Add(new CheckFinding("ok", message));

    public void Warn(string message) => _findings.Add(new CheckFinding("warn", message));

    public void Error(string message) => _findings.Add(new CheckFinding("error", message));

    public bool HasErrors => _findings.Any(f => f.Level == "error");
}

/// <summary>
/// Migrates the local data osu!lazer holds into the layout osu!stable expects:
/// <c>Songs/</c>, <c>Skins/</c> and <c>Replays/</c>.
///
/// Guarantees:
/// <list type="bullet">
/// <item>the lazer data directory is only ever read;</item>
/// <item>nothing is written outside the configured target directories;</item>
/// <item>an existing destination that is already the same file (an existing hard link) is left
/// untouched, and nothing is ever deleted except when explicitly overwriting a target file.</item>
/// </list>
/// </summary>
public sealed class RestoreEngine
{
    private readonly RestoreOptions _options;
    private readonly LazerSnapshot _snapshot;
    private readonly FileRestorer _restorer;
    private readonly MigrateTargets _targets;

    private readonly List<PackageSummary> _packages = new();
    private readonly List<FileResult> _notices = new();
    private readonly List<SkippedUnsubmittedSet> _skippedUnsubmitted = new();
    private readonly List<ExistingFolder> _existingFolders = new();
    private readonly Dictionary<int, ExistingFolder> _existingBySetId = new();
    private readonly AvailabilityReport _availability = new();

    private readonly CategoryStats _songs = new() { Name = "谱面" };
    private readonly CategoryStats _skins = new() { Name = "皮肤" };
    private readonly CategoryStats _replays = new() { Name = "回放" };

    private int _processed;
    private int _totalFiles;
    private int _packagesReusedExistingFolder;
    private int _unsafePaths;
    private long _bytes;
    private string _currentCategory = string.Empty;

    /// <summary>只读检查走完曲库后的统计，真实迁移时为 null。</summary>
    private LibraryStats? _stats;

    public RestoreEngine(RestoreOptions options, LazerSnapshot snapshot)
    {
        _options = options;
        _snapshot = snapshot;
        _restorer = new FileRestorer(options);
        _targets = options.ResolveTargets();

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
        if (!_options.ReuseExistingFolders || !_options.Selection.Songs)
            return;

        // 纯统计模式没有输出目录；此时不能用空路径拼出相对的 "Songs"，否则会去当前工作目录里找。
        if (string.IsNullOrWhiteSpace(_options.OutputDirectory))
            return;

        if (!Directory.Exists(_targets.SongsDirectory))
            return;

        foreach (string directory in Directory.EnumerateDirectories(_targets.SongsDirectory))
        {
            string name = Path.GetFileName(directory);
            int? setId = StableNaming.TryParseSetIdFromFolderName(name);

            if (setId is not > 0)
                continue;

            if (_existingBySetId.ContainsKey(setId.Value))
            {
                _notices.Add(new FileResult(name, string.Empty, string.Empty, FileOutcome.SkippedExisting,
                    $"已有两个文件夹声称属于同一个 set id {setId}，保留先出现的那个"));
                continue;
            }

            var folder = new ExistingFolder(name, setId.Value, CountFiles(directory));

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

        if (_options.Selection.Songs)
            _totalFiles += _snapshot.SongSets.Sum(s => s.Files.Count);

        if (_options.Selection.Skins)
            _totalFiles += _snapshot.Skins.Sum(s => s.Files.Count);

        if (_options.Selection.Replays)
            _totalFiles += _snapshot.Replays.Count;

        if (!_options.Selection.Any)
            _availability.Error("没有选择任何要迁移的数据类型（谱面 / 皮肤 / 回放至少选一项）。");

        // 体检是只读模式：不创建目录、不写文件，只统计与检查。
        if (_options.ReadOnly)
        {
            RunAvailabilityChecks();
        }
        else
        {
            PrepareTargetDirectories();

            if (_options.Selection.Songs)
                MigrateSongs();

            if (_options.Selection.Skins)
                MigrateSkins();

            if (_options.Selection.Replays)
                MigrateReplays();
        }

        stopwatch.Stop();

        var categories = new List<CategorySummary>();

        if (_options.Selection.Songs)
            categories.Add(_songs.ToSummary());

        if (_options.Selection.Skins)
            categories.Add(_skins.ToSummary());

        if (_options.Selection.Replays)
            categories.Add(_replays.ToSummary());

        return new RestoreReport
        {
            LazerDataDirectory = _options.LazerDataDirectory,
            OutputDirectory = _options.OutputDirectory,
            Mode = _options.ReadOnly
                ? "体检（只读）"
                : _options.Mode == RestoreMode.Copy ? "复制" : "硬链接",
            ReadOnly = _options.ReadOnly,
            SchemaVersion = _snapshot.SchemaVersion,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now.ToString("O"),
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            PackagesPlanned = _snapshot.SongSets.Count,
            PackagesCreated = _packages.Count,
            PackagesReusedExistingFolder = _packagesReusedExistingFolder,
            PackagesSkippedUnsubmitted = _skippedUnsubmitted.Count,
            SkinsSkippedProtected = _snapshot.ProtectedSkinCount,
            ReplaysSkippedWithoutFile = _snapshot.ScoresWithoutReplay,
            ReplaysSkippedWithoutBeatmap = _snapshot.ScoresWithoutBeatmap,
            FilesPlanned = _totalFiles,
            FilesLinked = Total(s => s.Linked),
            FilesCopied = Total(s => s.Copied),
            FilesCopiedAfterLinkFailure = CountOutcome(FileOutcome.CopiedAfterLinkFailure),
            FilesAlreadyLinked = CountOutcome(FileOutcome.AlreadyLinked),
            FilesSkippedExisting = CountOutcome(FileOutcome.SkippedExisting),
            FilesReplaced = CountOutcome(FileOutcome.Replaced),
            FilesSourceMissing = CountOutcome(FileOutcome.SourceMissing),
            FilesSourceHashMismatch = CountOutcome(FileOutcome.SourceHashMismatch),
            FilesFailed = Total(s => s.Failed),
            FilesUnsafePath = _unsafePaths,
            Bytes = _bytes,
            BytesWritten = TotalBytesWritten,
            Categories = categories,
            Findings = _availability.Findings,
            Stats = _stats,
            Packages = _packages,
            ExistingFolders = _existingFolders,
            SkippedUnsubmitted = _skippedUnsubmitted,
            Notices = _notices,
        };
    }

    private int Total(Func<CategoryStats, int> selector)
    {
        int result = 0;

        foreach (var stats in Selected())
            result += selector(stats);

        return result;
    }

    private long TotalBytesWritten
    {
        get
        {
            long result = 0;

            foreach (var stats in Selected())
                result += stats.BytesWritten;

            return result;
        }
    }

    /// <summary>
    /// Counts how many files ended with a given disposition. Recorded straight from the restore
    /// pipeline because several dispositions ("duplicate name", "already a hard link") deliberately
    /// land in the same user-facing bucket and cannot be told apart afterwards.
    /// </summary>
    private int CountOutcome(FileOutcome outcome) => _outcomeCounts.GetValueOrDefault(outcome);

    private readonly Dictionary<FileOutcome, int> _outcomeCounts = new();

    private void RecordOutcome(FileOutcome outcome)
        => _outcomeCounts[outcome] = _outcomeCounts.GetValueOrDefault(outcome) + 1;

    private IEnumerable<CategoryStats> Selected()
    {
        if (_options.Selection.Songs)
            yield return _songs;

        if (_options.Selection.Skins)
            yield return _skins;

        if (_options.Selection.Replays)
            yield return _replays;
    }

    #region pre-flight

    private void PrepareTargetDirectories()
    {
        if (_options.Selection.Songs)
            Directory.CreateDirectory(_targets.SongsDirectory);

        if (_options.Selection.Skins)
            Directory.CreateDirectory(_targets.SkinsDirectory);

        if (_options.Selection.Replays)
            Directory.CreateDirectory(_targets.ReplaysDirectory);
    }

    /// <summary>
    /// 体检用的只读检查。基于当前已有的信息就能跑，且**不做任何写入**（连目录都不创建）：
    /// 目标目录存在就验证可写，不存在就检查最近的上级目录能否写出文件。
    /// 是否连目标目录一起检查由 <see cref="RestoreOptions.CheckTargets"/> 决定 ——
    /// 纯统计（没给输出目录）时只走曲库那一遍。
    /// </summary>
    private void RunAvailabilityChecks()
    {
        string lazerFiles = Path.Combine(_options.LazerDataDirectory, "files");

        if (!Directory.Exists(lazerFiles))
            _availability.Error($"找不到 lazer 曲库目录：{lazerFiles}");
        else
            _availability.Ok($"lazer 曲库可读：{lazerFiles}");

        if (_options.CheckTargets)
        {
            if (_options.Selection.Songs)
                CheckTarget("谱面", _targets.SongsDirectory);

            if (_options.Selection.Skins)
                CheckTarget("皮肤", _targets.SkinsDirectory);

            if (_options.Selection.Replays)
                CheckTarget("回放", _targets.ReplaysDirectory);

            // 硬链接可行性决定一次真实迁移是几乎不花空间，还是要把整个曲库复制一份。
            if (Directory.Exists(lazerFiles))
            {
                switch (FileSystem.CanHardLinkBetween(lazerFiles, _options.OutputDirectory))
                {
                    case HardLinkVerdict.SameVolume:
                        _availability.Ok("lazer 曲库与目标目录在同一分区，可以使用硬链接，几乎不额外占用空间。");
                        break;

                    case HardLinkVerdict.DifferentVolume:
                        _availability.Warn("lazer 曲库与目标目录不在同一分区，无法硬链接；实际迁移会改为复制文件，" +
                                           "需要与原曲库等量的额外空间。");
                        break;

                    default:
                        _availability.Warn("无法确认两边是否在同一分区；实际迁移时若不能硬链接会自动改为复制。");
                        break;
                }
            }
        }

        // 走一遍全部被引用的文件：既统计"有多少、缺多少、占多大"，也逐个确认目标路径能被接受。
        // 这是本工具唯一一次遍历整库的地方，Program 侧的统计全部读这里的结果。
        long referenced = 0;
        long distinctBytes = 0;
        int present = 0;
        var missingHashes = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var unsafeSamples = new List<string>();
        int unsafeCount = 0;

        foreach (var (file, baseDirectory, relative) in AllMappedFiles())
        {
            if (_options.CheckTargets
                && unsafeCount < 100
                && !TryResolveDestination(baseDirectory, relative, out _, out string? reason))
            {
                unsafeCount++;
                unsafeSamples.Add($"{relative}（{reason}）");
            }

            string source = LazerDatabase.HashPath(_options.LazerDataDirectory, file.Hash);

            if (File.Exists(source))
            {
                present++;
                long size = SafeLength(source);
                referenced += size;

                if (distinct.Add(file.Hash))
                    distinctBytes += size;
            }
            else
            {
                missingHashes.Add(file.Hash);
            }

            Progress();
        }

        _stats = new LibraryStats(
            present,
            missingHashes.Count,
            distinct.Count,
            referenced,
            distinctBytes,
            missingHashes.Take(5).ToList());

        if (missingHashes.Count == 0)
        {
            _availability.Ok($"lazer 曲库里所有被引用的文件都存在（共 {distinct.Count} 个不同哈希）。");
        }
        else
        {
            _availability.Error($"lazer 曲库里缺失 {missingHashes.Count} 个被引用的文件，" +
                                $"例如 {string.Join("、", missingHashes.Take(3).Select(h => h[..Math.Min(12, h.Length)]))}…；" +
                                "这些文件无法迁移（详见报告）。");
        }

        if (_options.CheckTargets)
        {
            if (unsafeCount == 0)
                _availability.Ok("所有目标路径都通过了安全检查（没有绝对路径、路径穿越或越界）。");
            else
                _availability.Error($"有 {unsafeCount} 个文件的路径会被拒绝，例如 {string.Join("；", unsafeSamples.Take(3))}（详见报告）。");
        }

        _availability.Ok($"选中数据的逻辑大小 {Text.FormatSize(referenced)}；" +
                         $"硬链接方式几乎不额外占用空间，复制方式约需 {Text.FormatSize(distinctBytes)}。");
    }

    /// <summary>
    /// Same enumeration as the migration uses, but also yields where each file would land, so the
    /// check pass can validate destination paths without any of the write machinery.
    /// </summary>
    private IEnumerable<(FileEntry File, string BaseDirectory, string Relative)> AllMappedFiles()
    {
        if (_options.Selection.Songs)
        {
            var usedFolders = new HashSet<string>(_existingFolders.Select(f => f.Folder), StringComparer.OrdinalIgnoreCase);

            foreach (var set in _snapshot.SongSets)
            {
                if (_options.SkipUnsubmitted && set.SetId <= 0)
                    continue;

                string folder = ResolveFolder(set, usedFolders);
                string baseDirectory = Path.Combine(_targets.SongsDirectory, folder);

                foreach (var file in set.Files)
                    yield return (file, baseDirectory, file.RelativePath);
            }
        }

        if (_options.Selection.Skins)
        {
            var usedFolders = ExistingSkinFolderSet();

            foreach (var skin in _snapshot.Skins)
            {
                string folder = ResolveSkinFolder(skin, usedFolders);
                string baseDirectory = Path.Combine(_targets.SkinsDirectory, folder);

                foreach (var file in skin.Files)
                    yield return (file, baseDirectory, file.RelativePath);
            }
        }

        if (_options.Selection.Replays)
        {
            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var replay in _snapshot.Replays)
            {
                string baseName = StableNaming.ReplayFileName(
                    replay.PlayerName, replay.Artist, replay.Title, replay.DifficultyName,
                    replay.Date, replay.RulesetShortName, duplicateIndex: 1);

                string key = Path.GetFileNameWithoutExtension(baseName);
                nameCounts.TryGetValue(key, out int seen);
                seen++;
                nameCounts[key] = seen;

                string fileName = seen == 1
                    ? baseName
                    : StableNaming.ReplayFileName(
                        replay.PlayerName, replay.Artist, replay.Title, replay.DifficultyName,
                        replay.Date, replay.RulesetShortName, seen);

                yield return (replay.ReplayFile, _targets.ReplaysDirectory, fileName);
            }
        }
    }

    /// <summary>
    /// Read-only writability probe. Never creates anything: when the directory does not exist yet
    /// it checks the nearest existing ancestor instead, which is what a real run would create
    /// through.
    /// </summary>
    private void CheckTarget(string label, string directory)
    {
        if (Directory.Exists(directory))
        {
            try
            {
                string probe = Path.Combine(directory, $".stablerestorer-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                _availability.Ok($"{label}目标目录已存在且可写：{directory}");
            }
            catch (Exception ex)
            {
                _availability.Error($"{label}目标目录无法写入：{directory}（{ex.Message}）");
            }

            return;
        }

        string? ancestor = directory;

        while (!string.IsNullOrEmpty(ancestor) && !Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor);

        if (string.IsNullOrEmpty(ancestor))
        {
            _availability.Error($"{label}目标目录的上级路径不存在：{directory}");
            return;
        }

        try
        {
            string probe = Path.Combine(ancestor, $".stablerestorer-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            _availability.Ok($"{label}目标目录还不存在，但可以在 {ancestor} 下创建：{directory}");
        }
        catch (Exception ex)
        {
            _availability.Error($"{label}目标目录无法创建：{directory}（在 {ancestor} 下写入失败：{ex.Message}）");
        }
    }

    #endregion

    #region songs

    private void MigrateSongs()
    {
        _currentCategory = _songs.Name;

        // Folder names in use, seeded with the stable install's own folders so nothing is duplicated.
        var usedFolders = new HashSet<string>(_existingFolders.Select(f => f.Folder), StringComparer.OrdinalIgnoreCase);

        foreach (var set in _snapshot.SongSets)
        {
            if (ShouldSkipUnsubmitted(set))
                continue;

            _songs.Items++;

            string folder = ResolveFolder(set, usedFolders);
            MigrateSongPackage(set, folder);
        }
    }

    private bool ShouldSkipUnsubmitted(SongSnapshot set)
    {
        if (!_options.SkipUnsubmitted || set.SetId > 0)
            return false;

        _songs.ItemsSkipped++;
        _skippedUnsubmitted.Add(new SkippedUnsubmittedSet(set.SetId, set.Artist, set.Title, set.Protected));

        if (_options.Verbose)
            Console.Error.WriteLine($"  跳过（无 online ID）：{Describe(set)}");

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

        string baseName = StableNaming.BeatmapFolderName(set.SetId, set.Artist, set.Title);

        if (used.Add(baseName))
            return baseName;

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} ({i})";

            if (used.Add(candidate))
            {
                _notices.Add(new FileResult(baseName, string.Empty, string.Empty, FileOutcome.SkippedExisting,
                    $"文件夹重名，改用 '{candidate}'"));
                return candidate;
            }
        }
    }

    private void MigrateSongPackage(SongSnapshot set, string folder)
    {
        string packageDirectory = Path.Combine(_targets.SongsDirectory, folder);

        int linked = 0, copied = 0, skipped = 0, missing = 0, failed = 0;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in set.Files)
        {
            string relative = file.RelativePath;

            // Two Realm entries can legitimately map to the same destination name after
            // sanitisation; keep the first and report the rest instead of silently overwriting.
            if (!seenNames.Add(relative))
            {
                skipped++;
                RecordOutcome(FileOutcome.SkippedExisting);
                _notices.Add(new FileResult(folder, relative, file.Hash, FileOutcome.SkippedExisting,
                    "同一谱面集内出现重复文件名，已保留第一个"));
                continue;
            }

            var outcome = MigrateFile(_songs, folder, packageDirectory, relative, file);

            switch (outcome.Outcome)
            {
                case FileOutcome.Linked:
                    linked++;
                    break;

                case FileOutcome.Copied:
                case FileOutcome.CopiedAfterLinkFailure:
                case FileOutcome.Replaced:
                    copied++;
                    break;

                case FileOutcome.AlreadyLinked:
                    skipped++;
                    break;

                case FileOutcome.SkippedExisting:
                    skipped++;
                    break;

                case FileOutcome.SourceMissing:
                    missing++;
                    break;

                default:
                    failed++;
                    break;
            }
        }

        // Keep a record for every set that was planned, so partial results stay visible.
        _packages.Add(new PackageSummary(folder, set.Files.Count, linked, copied, skipped, missing, failed));

        if (_options.Verbose)
            Console.Error.WriteLine($"  {folder}：写入 {linked + copied}，跳过 {skipped}，缺失 {missing}，失败 {failed}");
    }

    private static string Describe(SongSnapshot set)
        => $"set {set.SetId} {set.Artist} - {set.Title}".Trim();

    #endregion

    #region skins

    private void MigrateSkins()
    {
        _currentCategory = _skins.Name;

        var usedFolders = ExistingSkinFolderSet();

        foreach (var skin in _snapshot.Skins)
        {
            _skins.Items++;
            MigrateSkin(skin, ResolveSkinFolder(skin, usedFolders));
        }
    }

    /// <summary>
    /// The skin folders this install already has. Seed it as the "in use" set so a name that is
    /// already taken is never handed out twice.
    /// </summary>
    private List<string> ExistingSkinFolders()
    {
        var folders = new List<string>();

        if (!Directory.Exists(_targets.SkinsDirectory))
            return folders;

        foreach (string directory in Directory.EnumerateDirectories(_targets.SkinsDirectory))
            folders.Add(Path.GetFileName(directory));

        return folders;
    }

    /// <summary>
    /// Picks the folder for a skin, preferring one this stable install already has (stable appends
    /// its own source note to skin names, e.g. <c>X (hobby)</c>) and otherwise deriving a fresh,
    /// collision-free name. Adds the chosen name to <paramref name="used"/>.
    /// </summary>
    private string ResolveSkinFolder(SkinSnapshot skin, HashSet<string> used)
    {
        // Prefer a folder this stable install already has for the same skin, so files are filled in
        // rather than a second copy being created under a slightly different name.
        foreach (string candidate in used)
        {
            if (StableNaming.SkinFolderMatchesName(candidate, skin.Name))
                return candidate;
        }

        string baseName = StableNaming.SkinFolderName(skin.Name, skin.Creator);

        if (used.Add(baseName))
            return baseName;

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} ({i})";

            if (used.Add(candidate))
            {
                _notices.Add(new FileResult(baseName, string.Empty, string.Empty, FileOutcome.SkippedExisting,
                    $"皮肤文件夹重名，改用 '{candidate}'"));
                return candidate;
            }
        }
    }

    /// <summary>Skin folder names as a set, ready to be passed to <see cref="ResolveSkinFolder"/>.</summary>
    private HashSet<string> ExistingSkinFolderSet()
        => new(ExistingSkinFolders(), StringComparer.OrdinalIgnoreCase);

    private void MigrateSkin(SkinSnapshot skin, string folder)
    {
        string skinDirectory = Path.Combine(_targets.SkinsDirectory, folder);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in skin.Files)
        {
            string relative = file.RelativePath;

            if (!seenNames.Add(relative))
            {
                RecordOutcome(FileOutcome.SkippedExisting);
                _notices.Add(new FileResult(folder, relative, file.Hash, FileOutcome.SkippedExisting,
                    "同一皮肤内出现重复文件名，已保留第一个"));
                continue;
            }

            MigrateFile(_skins, folder, skinDirectory, relative, file);
        }
    }

    #endregion

    #region replays

    private void MigrateReplays()
    {
        _currentCategory = _replays.Name;

        // Several scores can share the same player, beatmap and day; stable disambiguates with a
        // -2, -3… suffix, so count the occurrences of each base name.
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var replay in _snapshot.Replays)
        {
            _replays.Items++;

            string baseName = StableNaming.ReplayFileName(
                replay.PlayerName, replay.Artist, replay.Title, replay.DifficultyName,
                replay.Date, replay.RulesetShortName, duplicateIndex: 1);

            string key = Path.GetFileNameWithoutExtension(baseName);
            nameCounts.TryGetValue(key, out int seen);
            seen++;
            nameCounts[key] = seen;

            string fileName = seen == 1
                ? baseName
                : StableNaming.ReplayFileName(
                    replay.PlayerName, replay.Artist, replay.Title, replay.DifficultyName,
                    replay.Date, replay.RulesetShortName, seen);

            if (_options.Verbose)
                Console.Error.WriteLine($"  回放：{fileName}");

            MigrateFile(_replays, "Replays", _targets.ReplaysDirectory, fileName, replay.ReplayFile);
        }
    }

    #endregion

    /// <summary>
    /// Shared per-file pipeline: resolve the destination inside <paramref name="baseDirectory"/>,
    /// refuse path escapes, then hand off to <see cref="FileRestorer"/> and fold the outcome into
    /// the category totals.
    /// </summary>
    private FileResult MigrateFile(CategoryStats stats, string packageName, string baseDirectory, string relative, FileEntry file)
    {
        stats.Planned++;

        if (!TryResolveDestination(baseDirectory, relative, out string destination, out string? reason))
        {
            _unsafePaths++;
            stats.Failed++;

            var unsafeResult = new FileResult(packageName, relative, file.Hash, FileOutcome.Failed, reason);
            _notices.Add(unsafeResult);
            Progress();
            return unsafeResult;
        }

        string source = LazerDatabase.HashPath(_options.LazerDataDirectory, file.Hash);
        string label = Path.Combine(Path.GetFileName(baseDirectory), relative);

        var result = _restorer.Restore(packageName, destination, source, file.Hash, label);
        long size = SafeLength(source);

        RecordOutcome(result.Outcome);

        // "Bytes referenced" counts every planned file; "bytes written" only counts content that
        // genuinely consumed new space, so a fresh hard link adds nothing.
        _bytes += size;

        switch (result.Outcome)
        {
            case FileOutcome.Linked:
                stats.Linked++;
                break;

            case FileOutcome.Copied:
                stats.Copied++;
                stats.BytesWritten += size;
                break;

            case FileOutcome.CopiedAfterLinkFailure:
                stats.Copied++;
                stats.BytesWritten += size;
                _notices.Add(result with
                {
                    Detail = "无法创建硬链接（跨分区，或该文件已达 NTFS 的 1024 个链接上限），已改为复制：" + result.Detail,
                });
                break;

            case FileOutcome.Replaced:
                stats.Copied++;
                stats.BytesWritten += size;
                break;

            case FileOutcome.AlreadyLinked:
                stats.Skipped++;
                break;

            case FileOutcome.SkippedExisting:
                stats.Skipped++;
                _notices.Add(result);
                break;

            case FileOutcome.SourceMissing:
                stats.Missing++;
                _notices.Add(result);
                break;

            case FileOutcome.SourceHashMismatch:
                stats.Failed++;
                _notices.Add(result with { Detail = "文件内容与它的 SHA-256 文件名不一致（" + result.Detail + "）" });
                break;

            default:
                stats.Failed++;
                _notices.Add(result);
                break;
        }

        Progress();
        return result;
    }

    /// <summary>
    /// Builds the absolute destination path, refusing anything that would escape the item folder.
    /// </summary>
    private static bool TryResolveDestination(string baseDirectory, string relative, out string destination, out string? reason)
    {
        destination = string.Empty;
        reason = null;

        if (Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
        {
            reason = "数据库里的文件名是绝对路径";
            return false;
        }

        string[] segments = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                reason = "文件名里含有路径穿越（..）";
                return false;
            }
        }

        if (segments.Length == 0)
        {
            reason = "规范化后文件名为空";
            return false;
        }

        destination = Path.Combine(baseDirectory, Path.Combine(segments));

        string baseFull = Path.GetFullPath(baseDirectory + Path.DirectorySeparatorChar);
        string destinationFull = Path.GetFullPath(destination);

        if (!destinationFull.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
        {
            reason = "解析后的路径超出了目标文件夹";
            return false;
        }

        return true;
    }

    private void Progress()
    {
        _processed++;

        // The callback decides how often to actually redraw; a test run prints every file, so let
        // the consumer throttle rather than dropping updates here.
        _options.Progress?.Invoke(_processed, _totalFiles, _currentCategory);
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
