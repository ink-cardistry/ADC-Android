using System.Diagnostics;
using System.Text;

namespace ArcaeaDarkApkCreator;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public string All => (StdOut + "\n" + StdErr).Trim();
    public bool Ok => ExitCode == 0;
}

/// <summary>统一的子进程调用封装（adb / apksigner / zipalign / keytool）。</summary>
internal static class ProcessRunner
{
    public static ProcessResult Run(
        string exe,
        IEnumerable<string> args,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? env = null,
        int timeoutMs = 0)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (timeoutMs > 0)
        {
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return new ProcessResult(-1, outSb.ToString().Trim(), errSb + "\n[超时] 进程已被终止");
            }
        }
        else
        {
            p.WaitForExit();
        }
        // 确保异步输出读取完成
        p.WaitForExit();

        return new ProcessResult(p.ExitCode, outSb.ToString().Trim(), errSb.ToString().Trim());
    }

    /// <summary>启动一个进程（用于需要自定义 IO 的场景）。</summary>
    public static Process Start(string exe, IEnumerable<string> args, bool redirectStdin = false,
        bool redirectStdout = true, bool redirectStderr = true,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = redirectStderr,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // 关键：写 stdin 时会写二进制（APK），必须确保不写入 UTF-8 BOM
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
    }

    /// <summary>运行命令并显示等待转圈，返回结果。</summary>
    public static ProcessResult RunWithSpinner(string exe, IEnumerable<string> args, string message,
        IReadOnlyDictionary<string, string>? env = null, int timeoutMs = 0)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (new Spinner(message))
        {
            if (timeoutMs > 0 && !p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return new ProcessResult(-1, outSb.ToString().Trim(), errSb + "\n[超时] 进程已被终止");
            }
            p.WaitForExit();
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, outSb.ToString().Trim(), errSb.ToString().Trim());
    }
}
