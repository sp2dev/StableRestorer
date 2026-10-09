namespace StableRestorer.Cli;

/// <summary>
/// 交互输入。所有读取都会剥掉可能存在的 UTF-8 字节序标记：用管道把输入喂进来时首行很容易带上，
/// 否则一个本来合法的菜单序号会变成无法识别。
/// </summary>
public static class ConsoleInput
{
    /// <summary>读一行，去掉 BOM 与结尾的回车。</summary>
    public static string? ReadLine() => Console.ReadLine()?.TrimStart('\uFEFF').TrimEnd('\r');

    /// <summary>
    /// 停下来等用户按键。stdin 被重定向（脚本驱动）时**不能**调用 <see cref="Console.ReadKey()"/>，
    /// 它会直接抛 <see cref="InvalidOperationException"/>，所以改为读掉一行；读到文件结尾会立刻返回，
    /// 不会把脚本挂住。
    /// </summary>
    public static void Pause(string hint = "按任意键返回主菜单…")
    {
        Theme.Hint("  " + hint);

        if (Console.IsInputRedirected)
        {
            ReadLine();
        }
        else
        {
            try
            {
                Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                // 没有可读的控制台输入，按"已按键"处理，继续走。
            }
        }

        Console.WriteLine();
    }

    /// <summary>带默认值的一行输入。</summary>
    public static string Prompt(string label, string fallback)
    {
        Theme.Write($"{label} [{fallback}]：");
        string? input = ReadLine();

        return string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
    }

    /// <summary>
    /// 询问一个目录。输入 <c>-</c> 清空；<paramref name="requireClientRealm"/> 为真时要求目录里有
    /// <c>client.realm</c>（用于识别 lazer 数据目录），否则只要求目录存在。
    /// </summary>
    public static string? PromptDirectory(string label, string? current, bool requireClientRealm)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine(label);
            Theme.Hint($"  当前：{(string.IsNullOrWhiteSpace(current) ? "（未设置）" : current)}");
            Theme.Write("  新的路径（回车保持不变，输入 - 清空）：");

            string? input = ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return current;

            if (input == "-")
                return null;

            input = input.Trim('"');

            if (requireClientRealm && !File.Exists(Path.Combine(input, "client.realm")))
            {
                Theme.Warn($"  ! 这个目录里没有 client.realm：{input}");
                continue;
            }

            if (!requireClientRealm && !Directory.Exists(input))
            {
                Theme.Warn($"  ! 目录不存在：{input}");
                continue;
            }

            return input;
        }
    }

    public static bool PromptBool(string label, bool current)
    {
        Theme.Write($"{label} [{(current ? "Y/n" : "y/N")}]：");
        string? input = ReadLine()?.Trim().ToLowerInvariant();

        return input switch
        {
            null or "" => current,
            "y" or "yes" or "是" or "1" => true,
            "n" or "no" or "否" or "0" => false,
            _ => current,
        };
    }

    public static string PromptChoice(string label, string[] choices, string current)
    {
        Theme.Write($"{label}（{string.Join("/", choices)}）[{current}]：");
        string? input = ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(input))
            return current;

        foreach (string choice in choices)
        {
            if (choice.Equals(input, StringComparison.OrdinalIgnoreCase))
                return choice;
        }

        Theme.Warn($"  ! 无效选择，保持 '{current}'。");
        return current;
    }
}
