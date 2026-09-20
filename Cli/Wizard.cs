using System.Text.Json;
using StableRestorer.Engine;
using StableRestorer.IO;
using StableRestorer.Lazer;

namespace StableRestorer.Cli;

/// <summary>
/// Interactive front end for the same operations the CLI exposes. No windowing or TUI dependency:
/// it only reads lines from stdin and writes to stdout, so it runs in any terminal (including a
/// plain console window) and stays usable over a remote session.
///
/// Flow: read (or create) the settings file -> auto-detect both installs -> show the current
/// configuration and any warnings -> let the user pick an operation.
/// </summary>
public static class Wizard
{
    private sealed class Settings
    {
        /// <summary>osu!lazer data directory (holds client.realm and files/).</summary>
        public string? Lazer { get; set; }

        /// <summary>
        /// osu!stable install root. The output directory is deliberately not a separate setting:
        /// the restored Songs/ tree is written straight into this install.
        /// </summary>
        public string? Stable { get; set; }

        public string Mode { get; set; } = "hardlink";

        /// <summary>Skip maps that have no online id (never submitted to osu!). Default on.</summary>
        public bool SkipUnsubmitted { get; set; } = true;

        /// <summary>Reuse the folder name of an existing stable folder for the same set id.</summary>
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
                // Next to the executable when published; falls back to app data if that is read-only.
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

        Welcome(createdNew, loadedFrom);

        while (true)
        {
            ShowConfiguration(settings);

            Console.WriteLine("请选择操作：");
            Console.WriteLine("  1) 体检        只读取 lazer 数据库，报告能还原什么（不写任何文件）");
            Console.WriteLine("  2) 演练        完整走一遍并校验，但不写文件，用来先看清单");
            Console.WriteLine("  3) 开始还原    真正创建硬链接");
            Console.WriteLine("  4) 修改设置    重新指定 lazer / stable 目录与选项");
            Console.WriteLine("  5) 帮助        显示完整命令行用法");
            Console.WriteLine("  0) 退出");
            Console.WriteLine();

            string choice = Prompt("输入序号", "0").Trim();

            try
            {
                switch (choice)
                {
                    case "1":
                        ScanFlow(settings);
                        break;

                    case "2":
                        RestoreFlow(settings, dryRun: true);
                        break;

                    case "3":
                        RestoreFlow(settings, dryRun: false);
                        break;

                    case "4":
                        SettingsFlow(settings);
                        break;

                    case "5":
                        Console.WriteLine();
                        Console.WriteLine(CommandLine.Usage);
                        break;

                    case "0":
                    case "":
                        Console.WriteLine("已退出。");
                        return 0;

                    default:
                        Warn($"无法识别的序号 '{choice}'。");
                        break;
                }
            }
            catch (LazerSchemaMismatchException ex)
            {
                Console.Error.WriteLine();
                Error("无法打开 client.realm（Realm 数据库结构与本程序内置的版本不一致）。");
                Console.Error.WriteLine("  " + FirstLines(ex.Message, 6));
                Console.Error.WriteLine("  在“修改设置”里可以指定 schema 版本，也可以用 schemas 子命令排查。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Error($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    #region startup

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

                    // Fill in anything the file does not cover, so older settings files keep working.
                    loaded.Lazer ??= InstallLocator.DetectLazer();
                    loaded.Stable ??= InstallLocator.DetectStable().Path;

                    Console.WriteLine($"已读取配置文件：{path}");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"配置文件无法解析（{ex.Message}），将改用自动探测。");
            }
        }

        createdNew = true;
        loadedFrom = null;

        var settings = new Settings
        {
            Lazer = InstallLocator.DetectLazer(),
            Stable = InstallLocator.DetectStable().Path,
        };

        Console.WriteLine($"没有找到配置文件，已新建一份并自动探测安装位置：{path}");
        SaveSettings(settings, quiet: true);

        return settings;
    }

    private static void Welcome(bool createdNew, string? loadedFrom)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine($"  StableRestorer {CommandLine.Version}");
        Console.WriteLine("  把 osu!lazer 的曲库还原成 osu!stable 的 Songs 目录");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        if (createdNew)
            Console.WriteLine("首次运行：已自动寻找两边的安装位置（可用“修改设置”更正）。");
        else if (loadedFrom != null)
            Console.WriteLine("已载入上次使用的设置。");
    }

    /// <summary>
    /// The main screen's configuration block: every path in use, plus anything that would make the
    /// run behave differently from what the user probably expects.
    /// </summary>
    private static void ShowConfiguration(Settings settings)
    {
        Console.WriteLine();
        Console.WriteLine("──────────── 当前配置 ────────────");

        string? lazer = settings.Lazer;
        bool lazerOk = lazer != null && File.Exists(Path.Combine(lazer, "client.realm"));
        Console.WriteLine($"  osu!lazer 目录 : {FormatPath(lazer, lazerOk)}");

        string? stable = settings.Stable;
        bool stableOk = stable != null && Directory.Exists(stable);
        Console.WriteLine($"  osu!stable目录 : {FormatPath(stable, stableOk)}");

        if (stableOk)
            Console.WriteLine($"  还原到         : {InstallLocator.ResolveStableSongsDirectory(stable!)}");

        Console.WriteLine($"  文件方式       : {(settings.Mode == "copy" ? "复制（会额外占用等量空间）" : "硬链接（同一分区时不额外占用空间）")}");
        Console.WriteLine($"  未提交谱面     : {(settings.SkipUnsubmitted ? "不导入（没有 online ID 的谱面）" : "一并导入")}");
        Console.WriteLine($"  复用已有文件夹 : {(settings.ReuseExisting ? "是（按 set id 匹配，避免重复谱面）" : "否")}");
        Console.WriteLine($"  校验 SHA-256   : {(settings.VerifyHashes ? "是" : "否")}");
        Console.WriteLine($"  覆盖同名文件   : {(settings.Overwrite ? "是" : "否（内容不同时跳过并记录）")}");

        Console.WriteLine("──────────────────────────────────");

        foreach (string warning in CollectWarnings(settings))
            Console.WriteLine($"  ⚠ {warning}");

        Console.WriteLine();
    }

    private static string FormatPath(string? path, bool ok)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "（未设置）";

        return ok ? path : $"{path}   ← 这里没有预期的文件";
    }

    /// <summary>
    /// Everything worth warning about before a long run: missing paths, a volume split that forces
    /// copying, and a stable folder that is missing the layout the restore assumes.
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

        if (string.IsNullOrWhiteSpace(settings.Lazer) || string.IsNullOrWhiteSpace(settings.Stable))
            yield break;

        string lazerFiles = Path.Combine(settings.Lazer, "files");

        if (!Directory.Exists(lazerFiles))
        {
            yield return $"在 {settings.Lazer} 里找不到 files 目录（lazer 的曲库本体）。";
            yield break;
        }

        var verdict = FileSystem.CanHardLinkBetween(lazerFiles, settings.Stable);

        if (verdict == HardLinkVerdict.DifferentVolume)
        {
            yield return "lazer 曲库和 stable 目录不在同一个磁盘分区，无法创建硬链接；";
            yield return "  还原时会改为复制文件，会额外占用与曲库等量的磁盘空间。";
        }
        else if (verdict == HardLinkVerdict.Unknown && settings.Mode != "copy")
        {
            yield return "无法确认两边是否在同一分区，若不能硬链接会自动改为复制。";
        }
    }

    #endregion

    #region flows

    private static void SettingsFlow(Settings settings)
    {
        Console.WriteLine();
        Console.WriteLine("──────────── 修改设置 ────────────");
        Console.WriteLine("每一步直接回车 = 保持当前值。");

        settings.Lazer = PromptDirectory(
            "osu!lazer 数据目录（应包含 client.realm 与 files）",
            settings.Lazer,
            requireClientRealm: true);

        settings.Stable = PromptDirectory(
            "osu!stable 安装目录（还原结果写进它的 Songs）",
            settings.Stable,
            requireClientRealm: false);

        Console.WriteLine();
        settings.SkipUnsubmitted = PromptBool(
            "不导入未提交谱面（没有 online ID 的谱面，stable 里没有对应曲目）",
            settings.SkipUnsubmitted);

        settings.ReuseExisting = PromptBool(
            "按 set id 复用已有 stable 文件夹（避免产生重复谱面）",
            settings.ReuseExisting);

        settings.VerifyHashes = PromptBool(
            "校验每个源文件的 SHA-256 与文件名是否一致（更慢但更安全）",
            settings.VerifyHashes);

        settings.Mode = PromptChoice("文件创建方式", new[] { "hardlink", "copy" }, settings.Mode);

        settings.Overwrite = PromptBool(
            "覆盖内容不同的同名文件（默认跳过并记录）",
            settings.Overwrite);

        SaveSettings(settings, quiet: false);

        Console.WriteLine();
        Console.WriteLine("设置已保存，下面是更新后的配置。");
    }

    private static void ScanFlow(Settings settings)
    {
        if (!RequirePaths(settings))
            return;

        int schema = Program.ResolveSchema(settings.Lazer!, Array.Empty<string>());

        Console.WriteLine();
        Console.WriteLine($"正在读取 {Path.Combine(settings.Lazer!, "client.realm")} ...");
        Console.WriteLine();

        Program.Scan(settings.Lazer!, schema);

        Console.WriteLine();
        Console.WriteLine("如果上面 “hashed files missing” 是 0，说明 lazer 曲库里的文件都是齐的。");
    }

    private static void RestoreFlow(Settings settings, bool dryRun)
    {
        if (!RequirePaths(settings))
            return;

        string stableRoot = Path.GetFullPath(settings.Stable!);

        var options = new RestoreOptions
        {
            LazerDataDirectory = Path.GetFullPath(settings.Lazer!),

            // The output is the stable install itself - there is no separate destination to pick.
            OutputDirectory = stableRoot,
            StableDirectory = stableRoot,
            ReuseExistingFolders = settings.ReuseExisting,
            SkipUnsubmitted = settings.SkipUnsubmitted,
            Mode = settings.Mode.Equals("copy", StringComparison.OrdinalIgnoreCase)
                ? RestoreMode.Copy
                : RestoreMode.HardLink,
            DryRun = dryRun,
            Overwrite = settings.Overwrite,
            VerifyHashes = settings.VerifyHashes,
            Quiet = true,
        };

        options.ValidateDirectories();

        Console.WriteLine();
        Console.WriteLine("即将执行：");
        Console.WriteLine($"  lazer 目录   : {options.LazerDataDirectory}  (只读)");
        Console.WriteLine($"  写入 stable  : {InstallLocator.ResolveStableSongsDirectory(stableRoot)}");
        Console.WriteLine($"  方式         : {(dryRun ? "演练（不写文件）" : options.Mode.ToString().ToLowerInvariant())}");
        Console.WriteLine($"  未提交谱面   : {(options.SkipUnsubmitted ? "不导入" : "导入")}");
        Console.WriteLine($"  复用文件夹   : {(options.ReuseExistingFolders ? "是（按 set id）" : "否")}");
        Console.WriteLine($"  校验哈希     : {(options.VerifyHashes ? "是" : "否")}");
        Console.WriteLine();

        if (!dryRun)
        {
            Console.WriteLine("提示：stable 里已有的文件夹只会被补齐，不会删除文件；");
            Console.WriteLine("      内容与 lazer 不同名的同名文件默认跳过，不会覆盖。");
            Console.WriteLine();
        }

        if (!PromptBool("确认执行？", true))
        {
            Console.WriteLine("已取消。");
            return;
        }

        if (!dryRun && options.VerifyHashes)
            Console.WriteLine("逐个校验源文件哈希，大约需要 1~2 分钟。");

        Console.WriteLine();

        int lastPercent = -1;

        options = options with
        {
            Progress = (done, total) =>
            {
                if (total <= 0)
                    return;

                int percent = (int)(done * 100L / total);

                if (percent == lastPercent)
                    return;

                lastPercent = percent;
                Console.Write($"\r  进度 {percent,3}%  ({done}/{total} 文件)");
            },
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();

        int exitCode = Program.ExecuteRestore(
            options,
            Program.ResolveSchema(options.LazerDataDirectory, Array.Empty<string>()),
            Path.Combine(options.OutputDirectory, "stablerestorer-report.json"));

        watch.Stop();

        Console.WriteLine($"\r  完成，用时 {watch.Elapsed.TotalSeconds:N1} 秒。                    ");
        Console.WriteLine();

        if (dryRun)
            Console.WriteLine("这是演练，没有写入任何文件。确认清单无误后，回主菜单选 3 开始真正还原。");

        Console.WriteLine(exitCode == 0
            ? "没有发现问题。"
            : $"退出码 {exitCode}：请查看报告里的 notices 部分。");
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

        Warn($"还没有设置：{string.Join("、", missing)}。请先选 4 修改设置。");
        return false;
    }

    #endregion

    #region prompts

    /// <summary>
    /// Reads one line of input, removing a leading byte-order mark. Piping a UTF-8 stream into the
    /// process (scripts, CI) can leave a BOM at the start of the first line, which would otherwise
    /// make an otherwise valid menu choice unrecognisable.
    /// </summary>
    private static string? ReadLine() => Console.ReadLine()?.TrimStart('\uFEFF').TrimEnd('\r');

    private static string Prompt(string label, string fallback)
    {
        Console.Write($"{label} [{fallback}]: ");
        string? input = ReadLine();
        return string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
    }

    private static string? PromptDirectory(string label, string? current, bool requireClientRealm)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"{label}");
            Console.WriteLine($"  当前: {(string.IsNullOrWhiteSpace(current) ? "（未设置）" : current)}");
            Console.Write("  新的路径（回车保持不变，输入 - 清空）: ");

            string? input = ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return current;

            if (input == "-")
                return null;

            input = input.Trim('"');

            if (requireClientRealm && !File.Exists(Path.Combine(input, "client.realm")))
            {
                Warn($"这个目录里没有 client.realm：{input}");
                continue;
            }

            if (!requireClientRealm && !Directory.Exists(input))
            {
                Warn($"目录不存在：{input}");
                continue;
            }

            return input;
        }
    }

    private static bool PromptBool(string label, bool current)
    {
        Console.Write($"{label} [{(current ? "Y/n" : "y/N")}]: ");
        string? input = ReadLine()?.Trim().ToLowerInvariant();

        return input switch
        {
            null or "" => current,
            "y" or "yes" or "是" or "1" => true,
            "n" or "no" or "否" or "0" => false,
            _ => current,
        };
    }

    private static string PromptChoice(string label, string[] choices, string current)
    {
        Console.Write($"{label} ({string.Join("/", choices)}) [{current}]: ");
        string? input = ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(input))
            return current;

        foreach (string choice in choices)
        {
            if (choice.Equals(input, StringComparison.OrdinalIgnoreCase))
                return choice;
        }

        Warn($"无效选择，保持 '{current}'。");
        return current;
    }

    #endregion

    private static void SaveSettings(Settings settings, bool quiet)
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

            if (!quiet)
                Console.WriteLine($"已写入 {SettingsPath}");
        }
        catch (Exception ex)
        {
            Warn($"设置无法保存到 {SettingsPath}：{ex.Message}");
        }
    }

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n');

        return string.Join("\n  ", lines.Take(count).Select(l => l.TrimEnd())).Trim();
    }

    private static void Warn(string message) => Console.WriteLine($"  ! {message}");

    private static void Error(string message) => Console.Error.WriteLine($"  错误: {message}");
}
