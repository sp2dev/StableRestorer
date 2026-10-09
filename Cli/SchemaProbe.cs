using StableRestorer.Lazer;

namespace StableRestorer.Cli;

/// <summary>
/// 排查"Realm 打不开这个数据库"：逐个候选版本尝试，并报告到底哪条声明与文件不一致。
/// </summary>
public static class SchemaProbe
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                探测 osu!lazer 的 client.realm 使用哪个 Realm 结构版本。

                用法
                  stablerestorer schemas --lazer <目录> [--schema-version <n>]

                不给 --schema-version 时会依次尝试所有已知版本，并把每个被拒绝版本的
                具体差异打出来。升级 osu!lazer 之后就用它定位改动过的字段。
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

        Console.WriteLine($"client.realm ：{Path.Combine(lazerDir, "client.realm")}");
        Console.WriteLine($"本程序编译于 ：结构版本 {LazerDatabase.SupportedSchemaVersion}");
        Console.WriteLine();

        foreach (int candidate in candidates)
        {
            try
            {
                var snapshot = LazerDatabase.Read(lazerDir, candidate);

                Console.WriteLine($"v{candidate}：匹配成功");
                Console.WriteLine($"  谱面集     ：{snapshot.SongSets.Count}");
                Console.WriteLine($"  带名文件   ：{snapshot.TotalNamedFiles}");
                Console.WriteLine($"  皮肤 / 回放：{snapshot.Skins.Count} / {snapshot.Replays.Count}");
                Console.WriteLine($"  不同哈希   ：{snapshot.DistinctHashes}");
                return 0;
            }
            catch (LazerSchemaMismatchException ex)
            {
                Console.WriteLine($"v{candidate}：不匹配");
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

        throw new CommandLineException("缺少必需参数 --lazer。");
    }

    private static string Indent(string text)
        => string.Join("\n", text.Split('\n').Select(l => "  " + l.TrimEnd()));
}
