using System.Text.Json;
using StableRestorer.Engine;
using StableRestorer.IO;
using StableRestorer.Lazer;

namespace StableRestorer.Cli;

/// <summary>
/// 交互式前端，服务的是和命令行完全相同的一套引擎。不使用任何图形界面或 TUI 库，
/// 只读写标准输入输出，因此在任何终端里都能跑，也能通过远程会话使用。
///
/// 流程：读（或新建）配置文件 → 自动探测两端安装位置 → 回主界面显示当前配置与警告 → 选操作。
/// 迁移与体检结束后会停下来等一次按键，否则摘要会被下一轮的主界面顶掉。
/// </summary>
public static class Wizard
{
    private sealed class Settings
    {
        /// <summary>osu!lazer 数据目录（含 client.realm 与 files）。</summary>
        public string? Lazer { get; set; }

        /// <summary>
        /// osu!stable 安装目录。输出目录不单独设置：迁移结果直接写进这个安装目录。
        /// </summary>
        public string? Stable { get; set; }

        // 迁移内容，默认三样都迁移。
        public bool MigrateSongs { get; set; } = true;
        public bool MigrateSkins { get; set; } = true;
        public bool MigrateReplays { get; set; } = true;

        public string Mode { get; set; } = "hardlink";

        /// <summary>是否导入没有 online ID 的未提交谱面。默认不导入。</summary>
        public bool KeepUnsubmitted { get; set; }

        /// <summary>按 set id 复用已有 stable 文件夹名。默认开启。</summary>
        public bool ReuseExisting { get; set; } = true;

        public bool VerifyHashes { get; set; } = true;
        public bool Overwrite { get; set; }
    }

    private static string SettingsPath
    {
        get
        {
            string baseDirectory = AppContext.BaseDirectory;

            try
            {
                // 发布后与 exe 同目录；写不进去则退到用户配置目录。
                string candidate = Path.Combine(baseDirectory, "stablerestorer.settings.json");
                File.WriteAllText(candidate + ".probe", string.Empty);
                File.Delete(candidate + ".probe");
                return candidate;
            }
            catch
            {
                string fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StableRestorer");
                Directory.CreateDirectory(fallback);
                return Path.Combine(fallback, "stablerestorer.settings.json");
            }
        }
    }

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var settings = LoadOrCreateSettings(out bool createdNew, out string? loadedFrom);

        Banner(createdNew, loadedFrom);

        while (true)
        {
            ShowConfiguration(settings);
            ShowMenu();

            string choice = ConsoleInput.Prompt("输入序号", "0").Trim();

            try
            {
                switch (choice)
                {
                    case "1":
                        CheckFlow(settings);
                        break;

                    case "2":
                        MigrateFlow(settings);
                        break;

                    case "3":
                        SettingsFlow(settings);
                        break;

                    case "4":
                        Console.WriteLine();
                        Console.WriteLine(CommandLine.Usage);
                        break;

                    case "0":
                    case "":
                        Theme.WriteLine("已退出。");
                        return 0;

                    default:
                        Theme.Warn($"  ! 无法识别的序号 '{choice}'。");
                        break;
                }
            }
            catch (LazerSchemaMismatchException ex)
            {
                Console.Error.WriteLine();
                Theme.Error("错误：无法打开 client.realm（Realm 数据库结构与本程序内置的版本不一致）。");
                Theme.Error("      " + FirstLines(ex.Message, 6));
                Theme.Error("      可以用 schemas 子命令排查，或用 --schema-version 指定版本。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Theme.Error($"错误：{ex.GetType().Name}：{ex.Message}");
            }
        }
    }

    #region 启动

    private static void ShowMenu()
    {
        Console.WriteLine("请选择操作：");
        MenuItem("1", "体检", "统计能迁移什么 + 检查目标目录与硬链接（不写任何文件）");
        MenuItem("2", "开始迁移", "真正创建文件");
        MenuItem("3", "修改设置", "选择要迁移的内容、指定目录与选项");
        MenuItem("4", "帮助", "显示完整命令行用法");
        MenuItem("0", "退出", string.Empty);
        Console.WriteLine();
    }

    private static void MenuItem(string key, string title, string description)
    {
        Theme.Write($"  {key}) ", Theme.AccentColor);

        if (description.Length == 0)
        {
            Theme.WriteLine(title, ConsoleColor.White);
            return;
        }

        Theme.Write(Text.PadRight(title, 14), ConsoleColor.White);
        Theme.Hint(description);
    }

    private static Settings LoadOrCreateSettings(out bool createdNew, out string? loadedFrom)
    {
        string path = SettingsPath;

        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));

                if (loaded != null)
                {
                    createdNew = false;
                    loadedFrom = path;

                    // 老配置文件缺的字段用自动探测补齐。
                    loaded.Lazer ??= InstallLocator.DetectLazer();
                    loaded.Stable ??= InstallLocator.DetectStable().Path;

                    Theme.Hint($"已读取配置文件：{path}");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Theme.Warn($"  ! 配置文件无法解析（{ex.Message}），改用自动探测。");
            }
        }

        createdNew = true;
        loadedFrom = null;

        var settings = new Settings
        {
            Lazer = InstallLocator.DetectLazer(),
            Stable = InstallLocator.DetectStable().Path,
        };

        Theme.Hint($"未找到配置文件，已新建一份并自动探测安装位置：{path}");
        SaveSettings(settings, quiet: true);

        return settings;
    }

    private static void Banner(bool createdNew, string? loadedFrom)
    {
        Console.WriteLine();
        Theme.WriteLine("============================================================", Theme.AccentColor);
        Theme.WriteLine($"  StableRestorer {CommandLine.Version}", Theme.AccentColor);
        Theme.WriteLine("  把 osu!lazer 的曲库、皮肤与本地回放迁移到 osu!stable", ConsoleColor.White);
        Theme.WriteLine("============================================================", Theme.AccentColor);
        Console.WriteLine();

        if (createdNew)
            Theme.Hint("首次运行：已自动寻找两边的安装位置（可用“修改设置”更正）。");
        else if (loadedFrom != null)
            Theme.Hint("已载入上次使用的设置。");
    }

    /// <summary>
    /// 主界面的配置区块：所有会用到的路径与选项，外加任何会让实际行为和预期不一致的警告。
    /// </summary>
    private static void ShowConfiguration(Settings settings)
    {
        Theme.Section("当前配置");

        string? lazer = settings.Lazer;
        bool lazerOk = lazer != null && File.Exists(Path.Combine(lazer, "client.realm"));
        Theme.Item("osu!lazer 目录", FormatPath(lazer, lazerOk), lazerOk ? null : Theme.CautionColor);

        string? stable = settings.Stable;
        bool stableOk = stable != null && Directory.Exists(stable);
        Theme.Item("osu!stable 目录", FormatPath(stable, stableOk), stableOk ? null : Theme.CautionColor);

        if (stableOk)
        {
            Theme.Item("谱面写到", Path.Combine(stable!, "Songs"));
            Theme.Item("皮肤写到", Path.Combine(stable!, "Skins"));
            Theme.Item("回放写到", Path.Combine(stable!, "Replays"));
        }

        Theme.Item("迁移内容", DescribeSelection(settings));
        Theme.Item("文件方式", settings.Mode == "copy" ? "复制（会额外占用等量空间）" : "硬链接（同一分区时不额外占用空间）");
        Theme.Item("未提交谱面", settings.KeepUnsubmitted ? "一并导入" : "不导入（没有 online ID 的谱面）");
        Theme.Item("复用已有文件夹", settings.ReuseExisting ? "是（按 set id 匹配，避免重复谱面）" : "否");
        Theme.Item("校验 SHA-256", settings.VerifyHashes ? "是" : "否");
        Theme.Item("覆盖同名文件", settings.Overwrite ? "是" : "否（内容不同时跳过并记录）");

        Console.WriteLine();

        foreach (string warning in CollectWarnings(settings))
            Theme.Warn("  ⚠ " + warning);

        Console.WriteLine();
    }

    private static string DescribeSelection(Settings settings)
    {
        var parts = new List<string>();

        if (settings.MigrateSongs)
            parts.Add("谱面");

        if (settings.MigrateSkins)
            parts.Add("皮肤");

        if (settings.MigrateReplays)
            parts.Add("回放");

        return parts.Count == 0 ? "（未选择任何内容）" : string.Join("、", parts);
    }

    private static string FormatPath(string? path, bool ok)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "（未设置）";

        return ok ? path : $"{path}   ← 这里没有预期的文件";
    }

    /// <summary>
    /// 一次长时间运行前值得提醒的所有事情：路径缺失、跨分区导致必须复制、
    /// stable 目录缺少迁移所需的布局。
    /// </summary>
    private static IEnumerable<string> CollectWarnings(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Lazer))
            yield return "还没有设置 osu!lazer 目录，请在“修改设置”里指定。";
        else if (!File.Exists(Path.Combine(settings.Lazer, "client.realm")))
            yield return $"在 {settings.Lazer} 里找不到 client.realm，请确认这是 lazer 的数据目录。";

        if (string.IsNullOrWhiteSpace(settings.Stable))
            yield return "还没有设置 osu!stable 目录，请在“修改设置”里指定。";
        else if (!Directory.Exists(settings.Stable))
            yield return $"osu!stable 目录不存在：{settings.Stable}";
        else
        {
            foreach (string problem in InstallLocator.DescribeStableProblems(settings.Stable))
                yield return problem;
        }

        if (!settings.MigrateSongs && !settings.MigrateSkins && !settings.MigrateReplays)
            yield return "没有选择任何要迁移的内容，迁移不会有任何效果。";

        if (string.IsNullOrWhiteSpace(settings.Lazer) || string.IsNullOrWhiteSpace(settings.Stable))
            yield break;

        string lazerFiles = Path.Combine(settings.Lazer, "files");

        if (!Directory.Exists(lazerFiles))
        {
            yield return $"在 {settings.Lazer} 里找不到 files 目录（lazer 的曲库本体）。";
            yield break;
        }

        if (string.Equals(Path.GetFullPath(lazerFiles), Path.GetFullPath(settings.Stable), StringComparison.OrdinalIgnoreCase))
        {
            yield return "osu!stable 目录就是 lazer 的数据目录，两者必须分开。";
            yield break;
        }

        switch (FileSystem.CanHardLinkBetween(lazerFiles, settings.Stable))
        {
            case HardLinkVerdict.DifferentVolume:
                yield return "lazer 曲库和 stable 目录不在同一个磁盘分区，无法创建硬链接；";
                yield return "  迁移时会改为复制文件，会额外占用与曲库等量的磁盘空间。";
                break;

            case HardLinkVerdict.Unknown when settings.Mode != "copy":
                yield return "无法确认两边是否在同一分区，若不能硬链接会自动改为复制。";
                break;
        }
    }

    #endregion

    #region 操作流程

    private static void SettingsFlow(Settings settings)
    {
        Theme.Section("修改设置");
        Theme.Hint("每一步直接回车 = 保持当前值。");

        settings.Lazer = ConsoleInput.PromptDirectory(
            "osu!lazer 数据目录（应包含 client.realm 与 files）",
            settings.Lazer,
            requireClientRealm: true);

        settings.Stable = ConsoleInput.PromptDirectory(
            "osu!stable 安装目录（迁移结果写进它的 Songs / Skins / Replays）",
            settings.Stable,
            requireClientRealm: false);

        Console.WriteLine();
        Console.WriteLine("要迁移哪些内容？（可分别开关，回车保持不变）");

        settings.MigrateSongs = ConsoleInput.PromptBool("  谱面（Songs）", settings.MigrateSongs);
        settings.MigrateSkins = ConsoleInput.PromptBool("  皮肤（Skins，跳过随游戏附带的）", settings.MigrateSkins);
        settings.MigrateReplays = ConsoleInput.PromptBool("  本地回放（Replays）", settings.MigrateReplays);

        Console.WriteLine();
        settings.KeepUnsubmitted = ConsoleInput.PromptBool(
            "导入没有 online ID 的未提交谱面（stable 里没有对应曲目，默认不导入）",
            settings.KeepUnsubmitted);

        settings.ReuseExisting = ConsoleInput.PromptBool(
            "按 set id 复用已有 stable 文件夹（避免产生重复谱面）",
            settings.ReuseExisting);

        settings.VerifyHashes = ConsoleInput.PromptBool(
            "校验每个源文件的 SHA-256 与文件名是否一致（更慢但更安全）",
            settings.VerifyHashes);

        settings.Mode = ConsoleInput.PromptChoice("文件创建方式", new[] { "hardlink", "copy" }, settings.Mode);

        settings.Overwrite = ConsoleInput.PromptBool(
            "覆盖内容不同的同名文件（默认跳过并记录）",
            settings.Overwrite);

        SaveSettings(settings, quiet: false);

        Console.WriteLine();
        Theme.Ok("设置已保存，下面是更新后的配置。");
    }

    /// <summary>
    /// 体检：统计数据库里有什么，再跑一遍只读检查（目标目录、硬链接可行性、缺文件、
    /// 每个目标路径是否会被拒绝）。全程不写入，也不创建目录。
    /// </summary>
    private static void CheckFlow(Settings settings)
    {
        if (!RequirePaths(settings))
            return;

        if (!AnySelection(settings))
            return;

        string stableRoot = Path.GetFullPath(settings.Stable!);

        var options = new RestoreOptions
        {
            LazerDataDirectory = Path.GetFullPath(settings.Lazer!),
            OutputDirectory = stableRoot,
            StableDirectory = stableRoot,
            Selection = new MigrateSelection(settings.MigrateSongs, settings.MigrateSkins, settings.MigrateReplays),
            ReuseExistingFolders = settings.ReuseExisting,
            SkipUnsubmitted = !settings.KeepUnsubmitted,
            ReadOnly = true,
            CheckTargets = true,
            Quiet = true,
            Progress = ConsoleProgress.Create(quiet: false),
        };

        options.ValidateDirectories();

        Theme.WriteLine();
        Theme.WriteLine($"{CommandLine.ToolName} {CommandLine.Version} — 体检（只读）", Theme.AccentColor);
        Theme.Item("lazer 数据目录", options.LazerDataDirectory + "（只读）");
        Theme.Item("检查内容", options.Selection.ToString());
        Theme.Item("谱面目标", Path.Combine(stableRoot, "Songs"));
        Theme.Item("皮肤目标", Path.Combine(stableRoot, "Skins"));
        Theme.Item("回放目标", Path.Combine(stableRoot, "Replays"));
        Theme.Hint("  体检不会写入任何文件，也不会创建任何目录。");

        var watch = System.Diagnostics.Stopwatch.StartNew();

        int exitCode = Program.ExecuteCheck(options, Program.ResolveSchema(options.LazerDataDirectory, Array.Empty<string>()));

        watch.Stop();

        Theme.WriteLine();
        Theme.Hint($"  体检用时 {watch.Elapsed.TotalSeconds:N1} 秒。");

        if (exitCode == 0)
            Theme.Ok("  接着可以回主菜单选 2 开始迁移。");
        else
            Theme.Warn("  请先按上面的提示处理，再考虑迁移。");

        ConsoleInput.Pause();
    }

    private static void MigrateFlow(Settings settings)
    {
        if (!RequirePaths(settings))
            return;

        if (!AnySelection(settings))
            return;

        string stableRoot = Path.GetFullPath(settings.Stable!);

        var options = new RestoreOptions
        {
            LazerDataDirectory = Path.GetFullPath(settings.Lazer!),

            // 输出就是 stable 安装本身 —— 没有单独的"输出目录"要选。
            OutputDirectory = stableRoot,
            StableDirectory = stableRoot,
            Selection = new MigrateSelection(settings.MigrateSongs, settings.MigrateSkins, settings.MigrateReplays),
            ReuseExistingFolders = settings.ReuseExisting,
            SkipUnsubmitted = !settings.KeepUnsubmitted,
            Mode = settings.Mode.Equals("copy", StringComparison.OrdinalIgnoreCase)
                ? RestoreMode.Copy
                : RestoreMode.HardLink,
            Overwrite = settings.Overwrite,
            VerifyHashes = settings.VerifyHashes,
            Quiet = true,
            Progress = ConsoleProgress.Create(quiet: false),
        };

        options.ValidateDirectories();

        Theme.WriteLine();
        Theme.WriteLine($"{CommandLine.ToolName} {CommandLine.Version} — 迁移", Theme.AccentColor);
        Theme.Item("lazer 数据目录", options.LazerDataDirectory + "（只读）");
        Theme.Item("迁移内容", options.Selection.ToString());

        if (settings.MigrateSongs)
            Theme.Item("谱面到", Path.Combine(stableRoot, "Songs"));

        if (settings.MigrateSkins)
            Theme.Item("皮肤到", Path.Combine(stableRoot, "Skins"));

        if (settings.MigrateReplays)
            Theme.Item("回放到", Path.Combine(stableRoot, "Replays"));

        Theme.Item("方式", options.Mode == RestoreMode.HardLink ? "硬链接" : "复制");
        Theme.Item("未提交谱面", options.SkipUnsubmitted ? "不导入" : "导入");
        Theme.Item("复用文件夹", options.ReuseExistingFolders ? "是（按 set id）" : "否");
        Theme.Item("校验哈希", options.VerifyHashes ? "是" : "否");

        Theme.Hint("  提示：stable 里已有的文件夹只会被补齐，不会删除文件；");
        Theme.Hint("        内容不同的同名文件默认跳过，不会覆盖。");

        if (!ConsoleInput.PromptBool("确认开始迁移？", true))
        {
            Theme.WriteLine("已取消。");
            return;
        }

        if (options.VerifyHashes)
            Theme.Hint("  逐个校验源文件哈希，可能需要 1~2 分钟；下面会显示进度。");

        Console.WriteLine();

        var watch = System.Diagnostics.Stopwatch.StartNew();

        int exitCode = Program.ExecuteMigrate(
            options,
            Program.ResolveSchema(options.LazerDataDirectory, Array.Empty<string>()),
            Path.Combine(options.OutputDirectory, "stablerestorer-report.json"));

        watch.Stop();

        Theme.Hint($"  用时 {watch.Elapsed.TotalSeconds:N1} 秒。");
        Console.WriteLine();

        if (exitCode == 0)
            Theme.Ok("  迁移完成，没有发现问题。建议在 stable 里按 F5 重新扫描曲库。");
        else
            Theme.Error($"  退出码 {exitCode}：请查看报告里的 notices 部分。");

        ConsoleInput.Pause();
    }

    private static bool RequirePaths(Settings settings)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.Lazer))
            missing.Add("osu!lazer 目录");

        if (string.IsNullOrWhiteSpace(settings.Stable))
            missing.Add("osu!stable 目录");

        if (missing.Count == 0)
            return true;

        Theme.Warn($"  ! 还没有设置：{string.Join("、", missing)}。请先选 3 修改设置。");
        return false;
    }

    private static bool AnySelection(Settings settings)
    {
        if (settings.MigrateSongs || settings.MigrateSkins || settings.MigrateReplays)
            return true;

        Theme.Warn("  ! 还没有选择要检查的内容，请先选 3 修改设置。");
        return false;
    }

    #endregion

    private static void SaveSettings(Settings settings, bool quiet)
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

            if (!quiet)
                Theme.Hint($"已写入 {SettingsPath}");
        }
        catch (Exception ex)
        {
            Theme.Warn($"  ! 设置无法保存到 {SettingsPath}：{ex.Message}");
        }
    }

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n');

        return string.Join("\n      ", lines.Take(count).Select(l => l.TrimEnd())).Trim();
    }
}
