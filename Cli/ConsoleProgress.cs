using System.Diagnostics;

namespace StableRestorer.Cli;

/// <summary>
/// 单行进度显示。回调会被每个文件调用一次，所以这里自己节流（默认每 120 毫秒或每 1% 重绘一次），
/// 避免把控制台刷爆。
/// </summary>
public static class ConsoleProgress
{
    private static ProgressRenderer? _current;

    /// <summary>创建一个进度回调，交给 <c>RestoreOptions.Progress</c>。</summary>
    public static Action<int, int, string> Create(bool quiet)
    {
        var renderer = new ProgressRenderer(quiet);
        _current = renderer;
        return renderer.Report;
    }

    /// <summary>跑完后清掉进度行，避免和后面的摘要挤在一起。</summary>
    public static void Finish()
    {
        _current?.Finish();
        _current = null;
    }

    private sealed class ProgressRenderer
    {
        private const int BarCells = 24;

        private readonly bool _quiet;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly bool _interactive;

        private int _lastPercent = -1;
        private long _lastRedrawMs = -1;
        private int _lastLoggedDecile = -1;
        private string _lastCategory = string.Empty;
        private bool _drewAnything;

        public ProgressRenderer(bool quiet)
        {
            _quiet = quiet;

            // 输出被重定向时不要用 \r 覆盖，否则日志里会变成一堆残行。
            try
            {
                _interactive = !Console.IsErrorRedirected;
            }
            catch
            {
                _interactive = false;
            }
        }

        public void Report(int done, int total, string category)
        {
            if (_quiet)
                return;

            int percent = total > 0 ? (int)(done * 100L / total) : -1;
            long elapsed = _watch.ElapsedMilliseconds;
            bool categoryChanged = !string.Equals(category, _lastCategory, StringComparison.Ordinal);

            if (!categoryChanged && percent == _lastPercent && elapsed - _lastRedrawMs < 120)
                return;

            _lastPercent = percent;
            _lastRedrawMs = elapsed;
            _lastCategory = category;
            _drewAnything = true;

            if (_interactive)
                DrawInteractive(done, total, percent, category);
            else
                DrawLogLine(done, total, percent, category);
        }

        /// <summary>交互式：一行原地刷新，带彩色进度条。</summary>
        private void DrawInteractive(int done, int total, int percent, string category)
        {
            string categoryText = category.Length > 0 ? $"正在迁移{category}" : "正在迁移";
            string counter = total > 0 ? $"{done}/{total}" : done.ToString();

            Console.Error.Write("\r  ");

            if (percent >= 0)
            {
                var color = percent >= 100 ? Theme.SuccessColor : Theme.AccentColor;
                Theme.WriteError(BuildBar(percent), color);
                Theme.WriteError($" {percent,3}% ", color);
            }

            Theme.WriteError(Text.PadRight($"{categoryText}（{counter} 个文件）", 28));

            // 上一次的行可能更长，多写几个空格盖掉尾巴。
            Console.Error.Write("      ");
        }

        /// <summary>非交互（重定向到日志）：按 10% 打点，保持纯文本可读。</summary>
        private void DrawLogLine(int done, int total, int percent, string category)
        {
            if (percent < 0)
                return;

            if (percent / 10 == _lastLoggedDecile)
                return;

            _lastLoggedDecile = percent / 10;

            string categoryText = category.Length > 0 ? $"正在迁移{category}  " : string.Empty;
            Console.Error.WriteLine($"  {categoryText}{percent,3}%（{done}/{total} 个文件）");
        }

        private static string BuildBar(int percent)
        {
            int filled = Math.Clamp(percent * BarCells / 100, 0, BarCells);

            return "[" + new string('█', filled) + new string('░', BarCells - filled) + "]";
        }

        public void Finish()
        {
            if (!_drewAnything || !_interactive)
                return;

            // 进度行最长约 24 + 6 + 28 + 6 列，清干净再让摘要接着打印。
            Console.Error.Write("\r" + new string(' ', 80) + "\r");
        }
    }
}
