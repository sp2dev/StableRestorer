using System.Globalization;

namespace StableRestorer;

/// <summary>
/// 显示层文本工具：字节数格式化，以及按**终端显示宽度**对齐。
///
/// 为什么要自己算宽度：中文、全角标点在终端里占两列，用 <c>string.Length</c> 补空格永远对不齐
/// （"皮肤写到"是 4 个字符但占 8 列）。所有输出块的对齐都走这里。
/// </summary>
public static class Text
{
    /// <summary>把字节数格式化成人类可读的大小。全工具只有这一份实现。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:N2} GiB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} MiB",
        >= 1024 => $"{bytes / 1024.0:N0} KiB",
        _ => $"{bytes} 字节",
    };

    /// <summary>文本在终端里占用的列数：宽字符（汉字、全角符号）算 2 列，组合符算 0 列。</summary>
    public static int DisplayWidth(string text)
    {
        int width = 0;

        foreach (char c in text)
            width += ColumnWidth(c);

        return width;
    }

    /// <summary>右侧补空格到指定显示宽度；已经等于或超出时原样返回。</summary>
    public static string PadRight(string text, int width)
    {
        int missing = width - DisplayWidth(text);

        return missing > 0 ? text + new string(' ', missing) : text;
    }

    private static int ColumnWidth(char c)
    {
        // 组合符（变音符号、声调）叠在前一个字符上，不单独占位。
        if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(c) ? 2 : 1;
    }

    /// <summary>东亚宽字符（East Asian Wide / Fullwidth）的常用区间。</summary>
    private static bool IsWide(char c) => c switch
    {
        >= '\u1100' and <= '\u115F' => true,   // 韩文字母
        >= '\u2E80' and <= '\u303E' => true,   // 康熙部首、CJK 符号（不含 U+303F）
        >= '\u3041' and <= '\u33FF' => true,   // 假名、注音、CJK 兼容
        >= '\u3400' and <= '\u4DBF' => true,   // 汉字扩展 A
        >= '\u4E00' and <= '\u9FFF' => true,   // 基本汉字
        >= '\uA000' and <= '\uA4CF' => true,   // 彝文
        >= '\uAC00' and <= '\uD7A3' => true,   // 韩文音节
        >= '\uF900' and <= '\uFAFF' => true,   // CJK 兼容汉字
        >= '\uFE30' and <= '\uFE6F' => true,   // CJK 兼容形式
        >= '\uFF00' and <= '\uFF60' => true,   // 全角 ASCII
        >= '\uFFE0' and <= '\uFFE6' => true,   // 全角符号
        _ => false,
    };
}
