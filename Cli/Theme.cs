namespace StableRestorer.Cli;

/// <summary>
/// 终端配色与排版。刻意使用控制台自身的颜色 API（<see cref="Console.ForegroundColor"/>）而不是
/// ANSI 转义序列：
///
/// <list type="bullet">
/// <item>老 conhost、Windows Terminal、重定向到文件三种情况都不会出问题；</item>
/// <item>输出被重定向时 .NET 会忽略颜色设置，日志里**不可能**混进控制字符；</item>
/// <item>不需要 P/Invoke 去开 VT 处理，也不需要失败回退路径。</item>
/// </list>
///
/// 代价是不支持加粗/暗淡，用亮色与暗灰近似。
/// </summary>
public static class Theme
{
    /// <summary>标题色。</summary>
    private const ConsoleColor Heading = ConsoleColor.Cyan;

    /// <summary>可用、成功。</summary>
    private const ConsoleColor Good = ConsoleColor.Green;

    /// <summary>需要注意。</summary>
    private const ConsoleColor Caution = ConsoleColor.Yellow;

    /// <summary>阻塞、错误。</summary>
    private const ConsoleColor Bad = ConsoleColor.Red;

    /// <summary>次要说明、提示。等同"暗淡"。</summary>
    private const ConsoleColor Muted = ConsoleColor.DarkGray;

    /// <summary>区块标题的总显示宽度，分隔线按它补齐。</summary>
    private const int SectionWidth = 46;

    /// <summary>
    /// 是否上色。输出被重定向、设置了 <c>NO_COLOR</c>、或命令行给了 <c>--no-color</c> 时关闭。
    /// </summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>在程序入口调用一次，决定本次运行是否上色。</summary>
    public static void Configure(bool noColor)
    {
        bool disabledByEnvironment = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        Enabled = !noColor && !disabledByEnvironment && !IsRedirected();
    }

    private static bool IsRedirected()
    {
        try
        {
            return Console.IsOutputRedirected;
        }
        catch
        {
            // 连控制台都没有（例如宿主把标准输出拿走了）时按重定向处理。
            return true;
        }
    }

    public static void Write(string text, ConsoleColor? color = null)
    {
        if (!Enabled || color == null)
        {
            Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    public static void WriteLine(string text = "", ConsoleColor? color = null)
    {
        Write(text, color);
        Console.WriteLine();
    }

    /// <summary>
    /// 写标准错误并着色。进度行走 stderr（这样重定向 stdout 时日志里不会有 \r 残行），
    /// 所以它不能借用写 stdout 的 <see cref="Write"/>，否则两股输出会在终端上交错。
    /// </summary>
    public static void WriteError(string text, ConsoleColor? color = null)
    {
        if (!Enabled || color == null)
        {
            Console.Error.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.Error.Write(text);
        Console.ForegroundColor = previous;
    }

    /// <summary>区块标题：青色标题 + 右侧延伸的横线，整体宽度对齐到 <see cref="SectionWidth"/> 列。</summary>
    public static void Section(string title)
    {
        string head = $"── {title} ";
        int fill = Math.Max(4, SectionWidth - Text.DisplayWidth(head));

        Console.WriteLine();
        Write(head + new string('─', fill), Heading);
        Console.WriteLine();
    }

    /// <summary>一行"标签：值"，标签按显示宽度补齐，冒号才会对齐。</summary>
    public static void Item(string label, string value, ConsoleColor? valueColor = null, int labelWidth = 16)
    {
        Write("  " + Text.PadRight(label, labelWidth) + "：", Muted);
        WriteLine(value, valueColor);
    }

    /// <summary>纯文本行，带缩进。</summary>
    public static void Line(string text, ConsoleColor? color = null) => WriteLine("  " + text, color);

    /// <summary>次要提示。</summary>
    public static void Hint(string text) => WriteLine(text, Muted);

    /// <summary>体检结论行：按等级着色，标记文字与 <c>CheckFinding.Level</c> 对应。</summary>
    public static void Finding(string level, string message)
        => WriteLine($"  {Marker(level)} {message}", ColorFor(level));

    public static void Ok(string message) => WriteLine("  " + message, Good);

    public static void Warn(string message) => WriteLine("  " + message, Caution);

    public static void Error(string message) => WriteLine("  " + message, Bad);

    public static string Marker(string level) => level switch
    {
        "ok" => "[通过]",
        "warn" => "[注意]",
        _ => "[阻塞]",
    };

    public static ConsoleColor ColorFor(string level) => level switch
    {
        "ok" => Good,
        "warn" => Caution,
        _ => Bad,
    };

    /// <summary>需要着色而又要自己拼行的场合（进度条、表格里的单个数值）。</summary>
    public static ConsoleColor AccentColor => Heading;

    public static ConsoleColor SuccessColor => Good;

    public static ConsoleColor CautionColor => Caution;

    public static ConsoleColor BadColor => Bad;
}
