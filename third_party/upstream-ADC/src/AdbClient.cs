using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcaeaDarkApkCreator;

internal sealed class AdbDevice
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";
    public string Model { get; set; } = "";
    public string Product { get; set; } = "";
    public string Extra { get; set; } = "";

    public bool Online => State == "device";

    public override string ToString()
    {
        var desc = string.IsNullOrWhiteSpace(Model) ? "" : $"  型号:{Model}";
        var prod = string.IsNullOrWhiteSpace(Product) ? "" : $"  产品:{Product}";
        return $"{Serial}  [{State}]{desc}{prod}";
    }
}

/// <summary>
/// adb 封装。所有调用都带超时 —— 无线调试的端口会变，失效的条目会让命令**永久挂住**，
/// 因此宁可超时报错，也不能无限等待。
/// </summary>
internal sealed class AdbClient
{
    private readonly string _adb;

    /// <summary>普通查询类命令的超时。</summary>
    public const int QueryTimeoutMs = 15_000;

    /// <summary>探测设备是否存活用的短超时。</summary>
    public const int ProbeTimeoutMs = 10_000;

    /// <summary>推送/拉取时，多久没有任何进展就判定为卡死。</summary>
    public const int StallSeconds = 45;

    public AdbClient(string adb) => _adb = adb;

    /// <summary>选中的设备序列号；为空时依赖 adb 默认行为。</summary>
    public string Serial { get; set; } = "";

    private List<string> Base(string sub) =>
        Serial.Length > 0 ? new List<string> { "-s", Serial, sub } : new List<string> { sub };

    private ProcessResult Run(string sub, params string[] rest)
        => RunT(QueryTimeoutMs, sub, rest);

    private ProcessResult RunT(int timeoutMs, string sub, params string[] rest)
    {
        var args = Base(sub);
        args.AddRange(rest);
        return ProcessRunner.Run(_adb, args, timeoutMs: timeoutMs);
    }

    // ---------------- 设备 ----------------

    public List<AdbDevice> Devices()
    {
        var res = ProcessRunner.Run(_adb, new[] { "devices", "-l" }, timeoutMs: QueryTimeoutMs);
        var list = new List<AdbDevice>();
        foreach (var raw in res.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*")) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var d = new AdbDevice { Serial = parts[0], State = parts[1] };
            for (int i = 2; i < parts.Length; i++)
            {
                var kv = parts[i].Split(':', 2);
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "model": d.Model = kv[1]; break;
                    case "product": d.Product = kv[1]; break;
                    default: d.Extra += " " + parts[i]; break;
                }
            }
            list.Add(d);
        }
        return list;
    }

    /// <summary>
    /// 探测当前选中的设备是否真的响应。
    /// 无线调试的端口会变，`adb devices` 里可能残留一条失效条目：对它下任何命令都会永久挂住。
    /// </summary>
    public bool IsResponsive(int timeoutMs = ProbeTimeoutMs)
    {
        try
        {
            var res = RunT(timeoutMs, "shell", "echo", "ok");
            return res.Ok && res.StdOut.Contains("ok", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public ProcessResult Connect(string target)
        => ProcessRunner.Run(_adb, new[] { "connect", target }, timeoutMs: 30_000);

    public ProcessResult Pair(string target, string code)
        => ProcessRunner.Run(_adb, new[] { "pair", target, code }, timeoutMs: 60_000);

    public ProcessResult Disconnect(string target)
        => ProcessRunner.Run(_adb, new[] { "disconnect", target }, timeoutMs: 30_000);

    // ---------------- 设备信息 ----------------

    public string? GetPackageApkPath(string package)
    {
        var res = RunT(QueryTimeoutMs, "shell", "pm", "path", package);
        if (!res.Ok) return null;
        foreach (var raw in res.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("package:", StringComparison.Ordinal))
                return line.Substring("package:".Length).Trim();
        }
        return null;
    }

    /// <summary>取远端文件大小；失败返回 -1。</summary>
    public long GetRemoteSize(string remotePath, int timeoutMs = 8_000)
    {
        foreach (var probe in new[]
                 {
                     new[] { "stat", "-c", "%s", remotePath },
                     new[] { "toybox", "stat", "-c", "%s", remotePath },
                 })
        {
            var args = Base("shell");
            args.AddRange(probe);
            var res = ProcessRunner.Run(_adb, args, timeoutMs: timeoutMs);
            if (long.TryParse(res.StdOut.Trim(), out var size) && size >= 0) return size;
        }
        var res2 = RunT(timeoutMs, "shell", $"wc -c < '{remotePath}'");
        var m = Regex.Match(res2.StdOut, @"(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var s2)) return s2;
        return -1;
    }

    // ---------------- 拉取 ----------------

    /// <summary>从设备拉取文件到本地，显示进度条。带"停滞"看门狗，不会无限等待。</summary>
    public bool PullWithProgress(string remotePath, string localPath, string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);

        long total = GetRemoteSize(remotePath);
        if (total > 0) ConsoleUi.Dim($"远端文件大小: {ConsoleUi.Human(total)}");

        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { /* ignore */ }

        var psi = new ProcessStartInfo(_adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Base("pull")) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(remotePath);
        psi.ArgumentList.Add(localPath);

        using var p = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var bar = new ProgressBar(label, total > 0 ? total : 1);
        long lastLen = -1;
        var lastChange = DateTime.UtcNow;
        bool stalled = false;

        while (!p.HasExited)
        {
            try
            {
                if (File.Exists(localPath))
                {
                    var len = new FileInfo(localPath).Length;
                    if (len != lastLen)
                    {
                        lastLen = len;
                        lastChange = DateTime.UtcNow;
                        bar.Set(total > 0 ? Math.Min(len, total) : len);
                    }
                }
            }
            catch { /* 文件被占用时忽略 */ }

            if ((DateTime.UtcNow - lastChange).TotalSeconds > StallSeconds)
            {
                stalled = true;
                try { p.Kill(true); } catch { /* ignore */ }
                break;
            }
            Thread.Sleep(200);
        }
        p.WaitForExit();
        bar.Complete();

        if (stalled)
        {
            ReportStall("拉取");
            return false;
        }
        if (p.ExitCode != 0)
        {
            ConsoleUi.Fail($"adb pull 失败 (exit={p.ExitCode})");
            var msg = (errSb.ToString() + outSb).Trim();
            if (msg.Length > 0) ConsoleUi.Dim(msg.Length > 500 ? msg[..500] : msg);
            return false;
        }
        if (!File.Exists(localPath))
        {
            ConsoleUi.Fail("adb pull 未生成目标文件。");
            return false;
        }

        var actual = new FileInfo(localPath).Length;
        if (total > 0 && actual != total)
        {
            ConsoleUi.Fail($"拉取不完整：期望 {total} 字节，实际 {actual} 字节。");
            return false;
        }
        ConsoleUi.Ok($"已拉取到 {localPath} ({ConsoleUi.Human(actual)})");
        return true;
    }

    // ---------------- 推送 ----------------

    /// <summary>
    /// 把本地文件推送到设备，显示进度条。
    /// 用官方 `adb push`（`exec-in` 实测会静默失败），进度靠轮询远端文件大小得到，
    /// 并带"停滞"看门狗。
    /// </summary>
    public bool PushWithProgress(string localPath, string remotePath, string label)
    {
        long total = new FileInfo(localPath).Length;
        ConsoleUi.Dim($"待推送大小: {ConsoleUi.Human(total)}");

        // 先清掉旧文件，否则会轮询到上一次残留的大小
        RemoveRemote(remotePath);

        var psi = new ProcessStartInfo(_adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Base("push")) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(localPath);
        psi.ArgumentList.Add(remotePath);

        using var p = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var bar = new ProgressBar(label, total);
        long lastSize = -1;
        var lastChange = DateTime.UtcNow;
        bool stalled = false;

        while (!p.HasExited)
        {
            var size = GetRemoteSize(remotePath);
            if (size >= 0 && size != lastSize)
            {
                lastSize = size;
                lastChange = DateTime.UtcNow;
                bar.Set(Math.Min(size, total));
            }

            if ((DateTime.UtcNow - lastChange).TotalSeconds > StallSeconds)
            {
                stalled = true;
                try { p.Kill(true); } catch { /* ignore */ }
                break;
            }
            Thread.Sleep(1500);
        }
        p.WaitForExit();
        bar.Set(total);
        bar.Complete();

        if (stalled)
        {
            ReportStall("推送");
            return false;
        }
        if (p.ExitCode != 0)
        {
            ConsoleUi.Fail($"adb push 失败 (exit={p.ExitCode})");
            var msg = (errSb.ToString() + outSb).Trim();
            if (msg.Length > 500) msg = msg[..500];
            if (msg.Length > 0) ConsoleUi.Dim(msg);
            return false;
        }

        var remoteSize = GetRemoteSize(remotePath);
        if (remoteSize != total)
        {
            ConsoleUi.Fail($"推送校验失败：本地 {total} 字节，远端 {(remoteSize < 0 ? "未知" : remoteSize.ToString())} 字节。");
            return false;
        }
        ConsoleUi.Ok($"已推送到设备 {remotePath}");
        return true;
    }

    /// <summary>统一打印"卡住"的诊断建议。</summary>
    private static void ReportStall(string action)
    {
        ConsoleUi.Fail($"{action}停滞超过 {StallSeconds} 秒，已中止。");
        ConsoleUi.Info("常见原因：");
        ConsoleUi.Info("  1. 无线调试的连接已失效（端口会变）—— 用 2) 重新配对 / 连接，或重新选择设备");
        ConsoleUi.Info("  2. 手机休眠 / 锁屏 / 切走了网络");
        ConsoleUi.Info("  3. USB 线松动");
        ConsoleUi.Info("排查后可重试；建议先用 1) 检测环境 看看设备是否还在。");
    }

    // ---------------- 安装 ----------------

    /// <summary>安装并显示等待转圈（手机上需要人工确认时会有较长等待）。</summary>
    public ProcessResult InstallWithSpinner(string remoteApkPath, string message)
    {
        var args = Base("shell");
        args.AddRange(new[] { "pm", "install", "-r", "-d", remoteApkPath });
        return ProcessRunner.RunWithSpinner(_adb, args, message, timeoutMs: 30 * 60 * 1000);
    }

    public ProcessResult RemoveRemote(string remotePath)
        => RunT(QueryTimeoutMs, "shell", "rm", "-f", remotePath);
}
