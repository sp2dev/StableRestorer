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
        Theme.Configure(Has(args, "--no-color"));

        try
        {
            // 双击 exe（无参数）且连接着终端时进入交互模式。stdin 被重定向说明是脚本调用，
            // 此时打印用法最安全，避免在管道里挂住等待输入。
            if (args.Length == 0)
            {
                if (Console.IsInputRedirected)
                {
                    Console.WriteLine(CommandLine.Usage);
                    return 1;
                }

                return Wizard.Run();
            }

            if (Has(args, "-h") || Has(args, "--help") || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(CommandLine.Usage);
                return 0;
            }

            // 显式要求交互模式时始终生效，即使 stdin 是管道（自动化测试就是这样驱动的）。
            if (args[0].Equals("interactive", StringComparison.OrdinalIgnoreCase))
                return Wizard.Run();

            return args[0].ToLowerInvariant() switch
            {
                "migrate" => RunMigrate(args[1..]),
                "check" => RunCheck(args[1..]),
                "schemas" => SchemaProbe.Run(args[1..]),
                _ => FailUnknown(args[0]),
            };
        }
        catch (CommandLineException ex)
        {
            return Fail(ex.Message);
        }
        catch (LazerSchemaMismatchException ex)
        {
            Theme.Error("错误：无法打开 client.realm。");
            Theme.Error("      " + ex.Message.Replace("\n", "\n      "));
            Console.Error.WriteLine();
            Theme.Error("      声明的 Realm 结构必须与文件内部的结构一致。");
            Theme.Error("      如果知道版本号可以用 --schema-version <n> 指定，");
            Theme.Error("      或用 schemas 子命令查看每个候选版本被拒绝的原因。");
            return 3;
        }
        catch (Exception ex)
        {
            Theme.Error($"错误：{ex.GetType().Name}：{ex.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Theme.Error($"错误：{message}");
        Theme.Hint($"运行 '{CommandLine.ToolName} --help' 查看用法。");
        return 2;
    }

    /// <summary>
    /// 认不出来的命令。改名过的旧命令给出明确去向（比一句"无法识别"有用），
    /// 其余情况按编辑距离猜一个最接近的，太远就不猜，避免误导。
    /// </summary>
    private static int FailUnknown(string command)
    {
        string? renamed = command.ToLowerInvariant() switch
        {
            "test" => "体检与可用性检查已经合并成同一个操作，请改用 check",
            "scan" => "scan 已改名为 check",
            "restore" => "restore 已改名为 migrate",
            "schema" => "schema 已改名为 schemas",
            "osudb" => "osudb 子命令已移除",
            _ => null,
        };

        if (renamed != null)
            return Fail(renamed + "。");

        return Fail($"无法识别的命令 '{command}'。" + DescribeGuess(command));
    }

    private static string DescribeGuess(string command)
    {
        string[] known = { "check", "migrate", "schemas", "interactive" };

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in known)
        {
            int distance = EditDistance(command.ToLowerInvariant(), candidate);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // 只差一两个字符才值得猜，否则建议本身就是噪音。
        return best != null && bestDistance <= 2 ? $"是不是想输 '{best}'？" : string.Empty;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    #region check

    /// <summary>
    /// 体检：统计曲库内容 + 检查目标目录、硬链接可行性与每个目标路径。
    /// 加 <c>--no-target-check</c>（或不给 <c>--out</c>）时退化为纯统计。全程只读。
    /// </summary>
    private static int RunCheck(string[] args)
    {
        string lazerDir = LazerDatabase.ResolveDataDirectory(Require(args, "--lazer"));
        int schema = ResolveSchema(lazerDir, args);

        bool hasOut = TryGet(args, "--out", out string? outArg) && !string.IsNullOrWhiteSpace(outArg);
        string outputDir = hasOut ? Path.GetFullPath(outArg!) : string.Empty;

        string stableDir = TryGet(args, "--stable", out string? stableArg) && !string.IsNullOrWhiteSpace(stableArg)
            ? Path.GetFullPath(stableArg!)
            : outputDir;

        // 没有目标目录就没有可查的东西。
        bool checkTargets = hasOut && !Has(args, "--no-target-check");

        var options = new RestoreOptions
        {
            LazerDataDirectory = lazerDir,
            OutputDirectory = outputDir,
            StableDirectory = stableDir,
            Selection = ResolveSelection(args),
            ReuseExistingFolders = !Has(args, "--no-reuse-existing"),
            SkipUnsubmitted = !Has(args, "--keep-unsubmitted"),
            ReadOnly = true,
            CheckTargets = checkTargets,
            VerifyHashes = !Has(args, "--no-verify-hash"),
            Quiet = Has(args, "--quiet"),
            Progress = Has(args, "--no-progress") ? null : ConsoleProgress.Create(quiet: Has(args, "--quiet")),
        };

        if (!options.Selection.Any)
            throw new CommandLineException("--only 至少要选择一项（songs / skins / replays）。");

        options.ValidateDirectories();

        Theme.WriteLine();
        Theme.WriteLine($"{CommandLine.ToolName} {CommandLine.Version} — 体检（只读）", Theme.AccentColor);
        Theme.Item("lazer 数据目录", lazerDir + "（只读）");
        Theme.Item("Realm 版本", schema.ToString());
        Theme.Item("检查内容", options.Selection.ToString());

        if (checkTargets)
        {
            Theme.Item("输出目录", outputDir);
            Theme.Item("stable 目录", stableDir);
        }
        else
        {
            Theme.Item("目标目录", "未指定，本次只统计曲库内容");
        }

        Theme.Hint(checkTargets
            ? "  体检不会写入任何文件，也不会创建任何目录。"
            : "  只统计模式：既没有给 --out，也用了 --no-target-check。");

        return ExecuteCheck(options, schema);
    }

    /// <summary>
    /// 体检与交互模式共用：读一次数据库，跑一遍只读检查，再打印统计与结论。
    /// </summary>
    public static int ExecuteCheck(RestoreOptions options, int schema)
    {
        var readWatch = Stopwatch.StartNew();
        var snapshot = LazerDatabase.Read(options.LazerDataDirectory, schema);
        readWatch.Stop();

        Theme.WriteLine();
        Theme.Line($"已从 Realm 读取 {snapshot.SongSets.Count} 个谱面集 / {snapshot.Skins.Count} 个皮肤 / " +
                   $"{snapshot.Replays.Count} 份回放，用时 {readWatch.Elapsed.TotalSeconds:N1} 秒");

        var engine = new RestoreEngine(options, snapshot);
        var report = engine.Run();

        ConsoleProgress.Finish();

        PrintStatistics(snapshot, options, report.Stats);
        PrintFindings(report);

        Theme.WriteLine();

        if (report.Blocked)
        {
            Theme.Error("体检发现阻塞问题，请按上面的提示处理后重试。");
            return 6;
        }

        if ((report.Stats?.MissingFiles ?? 0) > 0)
        {
            Theme.Warn("曲库里有文件缺失，迁移仍可进行，但这些文件无法还原。");
            return 4;
        }

        Theme.Ok(options.CheckTargets
            ? "体检通过：源数据与目标目录都可用，可以开始迁移。"
            : "统计完成，没有发现问题。");

        return 0;
    }

    /// <summary>打印数据库里有什么、会被还原成什么样子。数值全部来自本次只读检查。</summary>
    private static void PrintStatistics(LazerSnapshot snapshot, RestoreOptions options, LibraryStats? stats)
    {
        // 只打印这次真正会处理的那几类：否则"可迁移的谱面集 2148"会让人以为回放之外的也会被迁移。
        if (options.Selection.Songs)
        {
            Theme.Section("谱面");
            Stat("可迁移的谱面集", snapshot.SongSets.Count.ToString());
            Stat("已标记删除（跳过）", snapshot.DeletedSetCount.ToString());
            Stat("没有文件（跳过）", snapshot.EmptySetCount.ToString());
            Stat("随 lazer 附带的内置集", snapshot.ProtectedSetCount.ToString());
            Stat("未提交（无 online ID）", snapshot.SongSets.Count(s => s.SetId <= 0) + "（默认不导入）");
            Stat("不同 set id 数量", snapshot.DistinctSetIds.ToString());

            if (snapshot.DuplicateSetIds > 0)
                Stat("重复的 set id", $"{snapshot.DuplicateSetIds} 个 -> {string.Join("、", snapshot.DuplicateSetIdList.Take(10))}", Theme.CautionColor);

            Stat("不同 map id 数量", snapshot.DistinctMapIds.ToString());
            Stat("带文件名的条目", snapshot.TotalNamedFiles.ToString());
        }

        if (options.Selection.Skins)
        {
            Theme.Section("皮肤");
            Stat("可迁移的皮肤", snapshot.Skins.Count.ToString());
            Stat("随 lazer 附带（跳过）", snapshot.ProtectedSkinCount.ToString());
            Stat("皮肤文件总数", snapshot.Skins.Sum(s => s.Files.Count).ToString());
        }

        if (options.Selection.Replays)
        {
            Theme.Section("回放");
            Stat("可迁移的回放", snapshot.Replays.Count.ToString());
            Stat("没有回放文件（跳过）", snapshot.ScoresWithoutReplay.ToString());
            Stat("谱面信息缺失（跳过）", snapshot.ScoresWithoutBeatmap.ToString());
        }

        if (stats != null)
        {
            Theme.Section("曲库文件");
            Stat("存在的文件", stats.PresentFiles.ToString());
            Stat("缺失的文件", stats.MissingFiles.ToString(), stats.MissingFiles > 0 ? Theme.BadColor : null);
            Stat("不同哈希数量", stats.DistinctHashes.ToString());
            Stat("逻辑总大小", Text.FormatSize(stats.ReferencedBytes));
            Stat("不同哈希实际占用", $"{Text.FormatSize(stats.DistinctBytes)}（硬链接方式几乎不额外占用）");

            if (stats.MissingSamples.Count > 0)
            {
                Theme.Line("缺失示例：", Theme.CautionColor);

                foreach (string hash in stats.MissingSamples)
                    Theme.Line("  " + hash, Theme.CautionColor);
            }
        }

        PrintSamplePaths(snapshot, options);
    }

    /// <summary>
    /// 统计表的标签列宽。定成常量而不是每行各写一个，是因为中文标签的显示宽度不一样
    /// （"未提交（无 online ID）"占 22 列），列宽不统一冒号就会参差不齐。
    /// </summary>
    private const int StatLabelWidth = 24;

    private const int SummaryLabelWidth = 20;

    private static void Stat(string label, string value, ConsoleColor? color = null)
        => Theme.Item(label, value, color, StatLabelWidth);

    private static void Sum(string label, string value, ConsoleColor? color = null)
        => Theme.Item(label, value, color, SummaryLabelWidth);

    /// <summary>抽几条真实命名，让人一眼看出会建成什么样子。</summary>
    private static void PrintSamplePaths(LazerSnapshot snapshot, RestoreOptions options)
    {
        var sets = options.Selection.Songs
            ? snapshot.SongSets.Where(s => !(options.SkipUnsubmitted && s.SetId <= 0)).Take(3).ToList()
            : new List<SongSnapshot>();

        bool anySkins = options.Selection.Skins && snapshot.Skins.Count > 0;
        bool anyReplays = options.Selection.Replays && snapshot.Replays.Count > 0;

        if (sets.Count == 0 && !anySkins && !anyReplays)
            return;

        Theme.Section("迁移后会创建的目录（示例）");

        foreach (var set in sets)
            Theme.Line($"Songs/{StableNaming.BeatmapFolderName(set.SetId, set.Artist, set.Title)}/  （{set.Files.Count} 个文件）");

        if (anySkins)
        {
            var skin = snapshot.Skins[0];
            Theme.Line($"Skins/{StableNaming.SkinFolderName(skin.Name, skin.Creator)}/  （{skin.Files.Count} 个文件）");
        }

        if (anyReplays)
        {
            var replay = snapshot.Replays[0];

            Theme.Line("Replays/" + StableNaming.ReplayFileName(
                replay.PlayerName, replay.Artist, replay.Title, replay.DifficultyName,
                replay.Date, replay.RulesetShortName, 1));
        }
    }

    /// <summary>体检结论：源数据与目标目录的逐项判断。</summary>
    private static void PrintFindings(RestoreReport report)
    {
        if (report.Findings.Count == 0)
            return;

        Theme.Section("体检结论");

        foreach (var finding in report.Findings)
            Theme.Finding(finding.Level, finding.Message);
    }

    #endregion

    #region migrate

    private static int RunMigrate(string[] args)
    {
        string lazerDir = LazerDatabase.ResolveDataDirectory(Require(args, "--lazer"));
        string outputDir = Path.GetFullPath(Require(args, "--out"));

        var mode = RestoreMode.HardLink;

        if (TryGet(args, "--mode", out string? modeArg))
        {
            mode = modeArg!.ToLowerInvariant() switch
            {
                "hardlink" or "hard-link" or "link" or "链接" => RestoreMode.HardLink,
                "copy" or "复制" => RestoreMode.Copy,
                _ => throw new CommandLineException($"--mode 只能是 hardlink 或 copy（收到 '{modeArg}'）。"),
            };
        }

        // 已有 stable 安装目录，用于按 set id 复用文件夹名；默认与输出目录一致。
        string stableDir = TryGet(args, "--stable", out string? stableArg) && !string.IsNullOrWhiteSpace(stableArg)
            ? Path.GetFullPath(stableArg!)
            : outputDir;

        var options = new RestoreOptions
        {
            LazerDataDirectory = lazerDir,
            OutputDirectory = outputDir,
            StableDirectory = stableDir,
            Selection = ResolveSelection(args),
            ReuseExistingFolders = !Has(args, "--no-reuse-existing"),
            SkipUnsubmitted = !Has(args, "--keep-unsubmitted"),
            Mode = mode,
            Overwrite = Has(args, "--overwrite"),
            VerifyHashes = !Has(args, "--no-verify-hash"),
            Verbose = Has(args, "--verbose"),
            Quiet = Has(args, "--quiet"),
            Progress = Has(args, "--no-progress") ? null : ConsoleProgress.Create(quiet: Has(args, "--quiet")),
        };

        if (!options.Selection.Any)
            throw new CommandLineException("--only 至少要选择一项（songs / skins / replays）。");

        options.ValidateDirectories();

        int schema = ResolveSchema(lazerDir, args);

        Theme.WriteLine();
        Theme.WriteLine($"{CommandLine.ToolName} {CommandLine.Version} — 迁移", Theme.AccentColor);
        Theme.Item("lazer 数据目录", lazerDir + "（只读）");
        Theme.Item("输出目录", outputDir);
        Theme.Item("stable 目录", stableDir + (options.ReuseExistingFolders ? "（按 set id 复用已有文件夹）" : "（不匹配已有文件夹）"));
        Theme.Item("迁移内容", options.Selection.ToString());
        Theme.Item("文件方式", options.Mode == RestoreMode.HardLink ? "硬链接（失败时自动复制）" : "复制");
        Theme.Item("未提交谱面", options.SkipUnsubmitted ? "不导入" : "一并导入");
        Theme.Item("Realm 版本", schema.ToString());

        string reportPath = TryGet(args, "--report", out string? reportArg)
            ? Path.GetFullPath(reportArg!)
            : Path.Combine(outputDir, "stablerestorer-report.json");

        return ExecuteMigrate(options, schema, reportPath);
    }

    private static MigrateSelection ResolveSelection(string[] args)
    {
        // 未指定 --only 时默认三样都迁移。
        if (!TryGet(args, "--only", out string? only) || string.IsNullOrWhiteSpace(only))
        {
            if (Has(args, "--no-skins") || Has(args, "--no-replays") || Has(args, "--no-songs"))
            {
                return new MigrateSelection(
                    !Has(args, "--no-songs"),
                    !Has(args, "--no-skins"),
                    !Has(args, "--no-replays"));
            }

            return MigrateSelection.All;
        }

        bool songs = false, skins = false, replays = false;

        foreach (string token in only!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "songs" or "song" or "beatmaps" or "谱面":
                    songs = true;
                    break;

                case "skins" or "skin" or "皮肤":
                    skins = true;
                    break;

                case "replays" or "replay" or "scores" or "回放":
                    replays = true;
                    break;

                case "all" or "全部":
                    songs = skins = replays = true;
                    break;

                default:
                    throw new CommandLineException($"--only 无法识别的取值 '{token}'（可用 songs / skins / replays / all）。");
            }
        }

        return new MigrateSelection(songs, skins, replays);
    }

    /// <summary>
    /// CLI 与交互模式共用：读取数据库、执行迁移、写出 JSON 报告，返回进程退出码。
    /// </summary>
    public static int ExecuteMigrate(RestoreOptions options, int schema, string reportPath)
    {
        var readWatch = Stopwatch.StartNew();
        var snapshot = LazerDatabase.Read(options.LazerDataDirectory, schema);
        readWatch.Stop();

        // 先建目录，保证 JSON 报告在任何情况下都写得出来。
        Directory.CreateDirectory(options.OutputDirectory);

        Theme.Line($"已从 Realm 读取 {snapshot.SongSets.Count} 个谱面集 / {snapshot.Skins.Count} 个皮肤 / " +
                   $"{snapshot.Replays.Count} 份回放，用时 {readWatch.Elapsed.TotalSeconds:N1} 秒");

        var engine = new RestoreEngine(options, snapshot);
        var report = engine.Run();

        ConsoleProgress.Finish();

        PrintSummary(report);

        try
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json_options));
            Theme.WriteLine();
            Theme.Hint($"JSON 报告：{reportPath}");
        }
        catch (Exception ex)
        {
            Theme.Warn($"警告：无法写入报告 {reportPath}：{ex.Message}");
        }

        if (report.HasProblems)
        {
            Theme.WriteLine();
            Theme.Error("迁移过程中有失败记录，请查看报告里的 notices 部分。");
            return 5;
        }

        return 0;
    }

    /// <summary>交互模式也使用这个打印摘要。</summary>
    public static void PrintSummary(RestoreReport report)
    {
        Theme.WriteLine();
        Theme.WriteLine("迁移结果", Theme.AccentColor);

        foreach (var category in report.Categories)
        {
            Theme.Section(category.Category);
            Sum("条目", $"{category.Items}（跳过 {category.ItemsSkipped}）");
            Sum("文件", $"计划 {category.FilesPlanned}，创建硬链接 {category.FilesLinked}，"
                       + $"复制 {category.FilesCopied}，跳过 {category.FilesSkipped}");
            Sum("缺失 / 失败", $"{category.FilesMissing} / {category.FilesFailed}",
                category.FilesMissing + category.FilesFailed > 0 ? Theme.BadColor : null);

            if (category.BytesWritten > 0)
                Sum("实际新增占用", Text.FormatSize(category.BytesWritten));
        }

        Theme.Section("汇总");

        if (report.Categories.Any(c => c.Category == "谱面"))
        {
            Sum("谱面集", $"{report.PackagesCreated} 个（复用已有文件夹 {report.PackagesReusedExistingFolder} 个，"
                         + $"跳过未提交 {report.PackagesSkippedUnsubmitted} 个）");
        }

        if (report.Categories.Any(c => c.Category == "皮肤") && report.SkinsSkippedProtected > 0)
            Sum("皮肤", $"跳过随游戏附带的 {report.SkinsSkippedProtected} 个");

        if (report.Categories.Any(c => c.Category == "回放"))
        {
            Sum("回放", $"跳过无回放文件 {report.ReplaysSkippedWithoutFile} 个，"
                      + $"跳过谱面信息缺失 {report.ReplaysSkippedWithoutBeatmap} 个");
        }

        Sum("已是硬链接（跳过）", report.FilesAlreadyLinked.ToString());
        Sum("目标已存在（跳过）", report.FilesSkippedExisting.ToString());
        Sum("链接上限改复制", report.FilesCopiedAfterLinkFailure.ToString());
        Sum("源文件缺失", report.FilesSourceMissing.ToString(),
            report.FilesSourceMissing > 0 ? Theme.BadColor : null);
        Sum("内容校验不一致", report.FilesSourceHashMismatch.ToString(),
            report.FilesSourceHashMismatch > 0 ? Theme.BadColor : null);
        Sum("路径不安全", report.FilesUnsafePath.ToString(),
            report.FilesUnsafePath > 0 ? Theme.BadColor : null);
        Sum("失败", report.FilesFailed.ToString(),
            report.FilesFailed > 0 ? Theme.BadColor : null);
        Sum("逻辑总大小", Text.FormatSize(report.Bytes));
        Sum("实际新增占用", Text.FormatSize(report.BytesWritten));
        Sum("用时", $"{report.DurationSeconds:N1} 秒");
    }

    #endregion

    /// <summary>
    /// 决定声明哪个 Realm 结构版本。版本号存在文件内部，所以给了值就照用，否则按候选顺序探测。
    /// </summary>
    public static int ResolveSchema(string lazerDir, string[] args)
    {
        if (TryGet(args, "--schema-version", out string? explicitArg))
        {
            if (!int.TryParse(explicitArg, out int parsed) || parsed <= 0)
                throw new CommandLineException($"--schema-version 必须是正整数（收到 '{explicitArg}'）。");

            return parsed;
        }

        int[] candidates = { LazerDatabase.SupportedSchemaVersion, 51, 50, 49, 48, 47, 46, 45, 44, 43, 42 };

        var failures = new List<string>();

        foreach (int candidate in candidates)
        {
            try
            {
                // 快照是普通记录对象，不持有非托管句柄；Realm 实例已在 Read() 内部释放。
                _ = LazerDatabase.Read(lazerDir, candidate);
                return candidate;
            }
            catch (LazerSchemaMismatchException ex)
            {
                failures.Add($"  v{candidate}：{FirstLine(ex.Message)}");
            }
        }

        if (Has(args, "--verbose"))
        {
            Console.Error.WriteLine("各候选版本的探测结果：");
            failures.ForEach(f => Console.Error.WriteLine(f));
        }

        throw new LazerSchemaMismatchException(
            $"已知的结构版本（{string.Join("、", candidates)}）都与这个数据库不匹配。" +
            "如果知道版本号请用 --schema-version 指定；加 --verbose 可以看到每个候选被拒绝的原因。");
    }

    private static string FirstLine(string message)
    {
        int index = message.IndexOf('\n');
        return (index < 0 ? message : message[..index]).Trim();
    }

    private static string Require(string[] args, string name)
        => TryGet(args, name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value!
            : throw new CommandLineException($"缺少必需参数 {name}。");

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
