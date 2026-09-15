using System.Text;

namespace ArcaeaDarkApkCreator;

/// <summary>控制台输出与交互（带颜色、进度条、询问）。</summary>
internal static class ConsoleUi
{
    private static readonly object Sync = new();

    public static void Raw(string text, ConsoleColor? color = null)
    {
        lock (Sync)
        {
            var old = Console.ForegroundColor;
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.Write(text);
            if (color.HasValue) Console.ForegroundColor = old;
        }
    }

    public static void Line(string text = "", ConsoleColor? color = null)
    {
        lock (Sync)
        {
            var old = Console.ForegroundColor;
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.WriteLine(text);
            if (color.HasValue) Console.ForegroundColor = old;
        }
    }

    public static void Info(string msg) => Line("  " + msg, ConsoleColor.Gray);
    public static void Ok(string msg) => Line("  [OK] " + msg, ConsoleColor.Green);
    public static void Warn(string msg) => Line("  [!] " + msg, ConsoleColor.Yellow);
    public static void Fail(string msg) => Line("  [x] " + msg, ConsoleColor.Red);
    public static void Dim(string msg) => Line("  " + msg, ConsoleColor.DarkGray);

    public static void Title(string msg)
    {
        Line();
        Line("================================================================", ConsoleColor.Cyan);
        Line("  " + msg, ConsoleColor.Cyan);
        Line("================================================================", ConsoleColor.Cyan);
    }

    public static void Section(string msg)
    {
        Line();
        Line("--- " + msg + " ---", ConsoleColor.White);
    }

    public static string? Ask(string prompt, string? def = null)
    {
        lock (Sync)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(def is null ? $"{prompt}: " : $"{prompt} [{def}]: ");
            Console.ForegroundColor = old;
        }
        var ans = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(ans)) return def;
        return ans.Trim();
    }

    public static string AskRequired(string prompt, string? def = null)
    {
        while (true)
        {
            var v = Ask(prompt, def);
            if (!string.IsNullOrWhiteSpace(v)) return v!;
            Fail("不能为空，请重新输入。");
        }
    }

    public static bool Confirm(string prompt, bool def = true)
    {
        var a = Ask(prompt + (def ? " (Y/n)" : " (y/N)"));
        if (string.IsNullOrWhiteSpace(a)) return def;
        return a.Trim().ToLowerInvariant() is "y" or "yes" or "是" or "1";
    }

    public static void Pause(string text = "按回车键继续...")
    {
        Line();
        Line("  " + text, ConsoleColor.DarkGray);
        Console.ReadLine();
    }

    public static string Human(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{(long)v} {units[i]}" : $"{v:0.00} {units[i]}";
    }
}

/// <summary>简单进度条：百分比 + 已传/总量 + 速度。</summary>
internal sealed class ProgressBar
{
    private readonly string _label;
    private readonly long _total;
    private long _value;
    private readonly DateTime _start = DateTime.UtcNow;
    private DateTime _lastRender = DateTime.MinValue;
    private int _lastWidth;
    private const int BarWidth = 30;

    public ProgressBar(string label, long total)
    {
        _label = label;
        _total = total;
    }

    public void Set(long value)
    {
        _value = value;
        Render(false);
    }

    public void Add(long delta)
    {
        _value += delta;
        Render(false);
    }

    public void Tick() => Render(false);

    private void Render(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRender).TotalMilliseconds < 120) return;
        _lastRender = now;

        double pct = _total > 0 ? Math.Min(1.0, (double)_value / _total) : 0;
        int filled = (int)Math.Round(pct * BarWidth);
        if (filled < 0) filled = 0;
        if (filled > BarWidth) filled = BarWidth;

        var sb = new StringBuilder();
        sb.Append(_label).Append(" [");
        sb.Append('#', filled);
        sb.Append('-', BarWidth - filled);
        sb.Append("] ");
        sb.Append((pct * 100).ToString("0.0")).Append("%  ");
        sb.Append(ConsoleUi.Human(_value));
        if (_total > 0) sb.Append(" / ").Append(ConsoleUi.Human(_total));
        var secs = (now - _start).TotalSeconds;
        if (secs > 0.5 && _value > 0)
        {
            sb.Append("  ").Append(ConsoleUi.Human((long)(_value / secs))).Append("/s");
            sb.Append("  已用 ").Append(TimeSpan.FromSeconds(secs).ToString(@"mm\:ss"));
        }

        var text = sb.ToString();
        if (text.Length < _lastWidth) text = text.PadRight(_lastWidth);
        _lastWidth = text.Length;
        ConsoleUi.Raw("\r" + text);
    }

    public void Complete()
    {
        _value = _total > 0 ? _total : _value;
        Render(true);
        ConsoleUi.Line();
    }
}

/// <summary>等待型转圈提示（用于 install / sign 等无进度输出的长任务）。</summary>
internal sealed class Spinner : IDisposable
{
    private readonly string _label;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;

    public Spinner(string label)
    {
        _label = label;
        _task = Task.Run(() =>
        {
            var frames = new[] { '|', '/', '-', '\\' };
            int i = 0;
            var start = DateTime.UtcNow;
            while (!_cts.IsCancellationRequested)
            {
                var el = DateTime.UtcNow - start;
                ConsoleUi.Raw($"\r  {_label} {frames[i++ % frames.Length]}  {el:mm\\:ss}   ");
                Thread.Sleep(150);
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task.Wait(500); } catch { /* ignore */ }
        ConsoleUi.Raw("\r" + new string(' ', 60) + "\r");
    }
}
