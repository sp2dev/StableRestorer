namespace StableRestorer.Cli;

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message)
    {
    }
}

public static class CommandLine
{
    public const string ToolName = "stablerestorer";

    public static string Version =>
        typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Usage => $$"""
        {{ToolName}} {{Version}} —— 把 osu!lazer 的本地数据迁移回 osu!stable 的目录布局。

        用法
          {{ToolName}}                                      交互模式（等同 {{ToolName}} interactive）
          {{ToolName}} check   --lazer <目录> --out <目录>   体检：只读，统计 + 可用性检查
          {{ToolName}} check   --lazer <目录> --no-target-check
                                                            只统计，不检查目标目录，不需要 --out
          {{ToolName}} migrate --lazer <目录> --out <目录>   正式迁移
          {{ToolName}} schemas --lazer <目录>                探测 Realm 结构版本

        命令说明
          check     体检：统计谱面 / 皮肤 / 回放的数量，确认每个被引用的文件都在，
                    并检查目标目录能否创建或写入、能否使用硬链接、以及每个目标路径会不会被
                    安全检查拒绝。**完全不写入**：不写文件，也不创建任何目录。
                    加 --no-target-check 则退化为纯统计（此时 --out 可省略）。
          migrate   真正创建文件。默认硬链接（同一分区时几乎不占额外空间），
                    跨分区或达到链接上限时自动改为复制。
          schemas   逐个候选版本报告"哪条声明与文件不一致"，换 lazer 版本时用它排查。
          interactive  进入交互菜单；无参数启动时若 stdin 连着终端也会自动进入。

        通用参数
          --lazer <目录>          osu!lazer 数据目录（含 client.realm 与 files）。
          --out <目录>            输出目录，迁移结果写到这里。migrate 必需，check 可选。
          --stable <目录>         已有 osu!stable 安装目录，用于按 set id 复用已有文件夹名。
                                  默认与 --out 相同。
          --only <列表>           只处理指定内容：songs / skins / replays / all，默认 all。
                                  也可以写成 --no-skins、--no-replays 这种排除式。
          --no-target-check       体检时只统计曲库，不检查目标目录（不需要 --out）。
          --keep-unsubmitted      连没有 online ID 的未提交谱面也一并导入。
                                  默认不导入：它们在 stable 里没有对应曲目，只会让曲库变乱。
          --no-reuse-existing     不匹配已有文件夹，一律按本工具的命名规则新建。
                                  默认会按 set id 匹配并复用，避免产生重复谱面。
          --mode <hardlink|copy>  文件创建方式，默认 hardlink（失败时自动改为复制）。
          --overwrite             目标已存在且内容不同时替换它。默认跳过并在报告里记录。
          --no-verify-hash        跳过源文件的 SHA-256 校验（默认每个源文件都校验）。
          --no-progress           关闭进度显示。
          --no-color              关闭彩色输出。输出被重定向或设置了 NO_COLOR 时同样自动关闭。
          --report <文件>         JSON 报告路径，默认 <out>/stablerestorer-report.json。
          --schema-version <n>    Realm 结构版本，默认自动探测（52、51、50 …）。
          --verbose               输出每个条目的细节。
          --quiet                 只输出摘要。
          -h, --help              显示本帮助。

        标识符
          谱面集由 BeatmapSetInfo.OnlineID（set id）标识，单张难度由 BeatmapInfo.OnlineID
          （map id）标识，两者在 osu! 的数据库里都是唯一的。stable 用
          "{setId} {artist} - {title}" 命名文件夹，所以本工具按 set id 匹配：
          stable 里已经有同一个 set id 的文件夹时，直接把缺的文件补进那个文件夹，
          而不是按另一个名字再建一份。

        安全约定
          * osu!lazer 数据目录以只读方式打开，绝不写入或删除其中任何文件。
          * --out 不能等于、位于或包含 lazer 数据目录，启动时就会拒绝。
          * 只增不删：唯一会删除的动作是 --overwrite 时替换目标文件本身。
          * 已经是同一份文件的硬链接会被跳过，不会重复创建。
          * check 不写任何东西：不写文件，也不创建目录（可写性靠写一个探测文件再立刻删掉来确认）。
          * 硬链接与 lazer 曲库共享同一份数据：删掉任意一侧都不会丢数据，
            但修改任意一侧会同时改动另一侧。需要完全独立就用 --mode copy。

        退出码
          0 成功   1 运行错误   2 参数错误   3 Realm 结构不匹配
          4 体检发现缺失文件   5 迁移过程有失败记录   6 体检发现阻塞问题
        """;
}
