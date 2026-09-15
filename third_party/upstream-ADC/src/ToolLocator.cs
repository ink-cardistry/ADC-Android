namespace ArcaeaDarkApkCreator;

internal sealed class ToolPaths
{
    public string? Adb { get; set; }
    public string? Apksigner { get; set; }
    public string? Zipalign { get; set; }
    public string? Keytool { get; set; }
    public string? Java { get; set; }
    public string? SdkRoot { get; set; }
    public string? BuildToolsDir { get; set; }

    public bool HasAdb => Adb != null;
    public bool HasBuildTools => Apksigner != null && Zipalign != null;
    public bool HasJava => Java != null;
    public bool HasKeytool => Keytool != null;
    public bool Ready => HasAdb && HasBuildTools && HasJava && HasKeytool;
}

/// <summary>定位 adb / apksigner / zipalign / keytool，缺失时给出下载地址。</summary>
internal static class ToolLocator
{
    public const string UrlPlatformTools = "https://developer.android.com/tools/releases/platform-tools";
    public const string UrlBuildTools = "https://developer.android.com/tools/releases/build-tools";
    public const string UrlJdk = "https://adoptium.net/";
    public const string UrlSdkManager = "https://developer.android.com/tools/sdkmanager";

    public static ToolPaths Locate()
    {
        var t = new ToolPaths();
        t.SdkRoot = FindSdkRoot();
        t.Adb = Which("adb.exe") ?? Probe(t.SdkRoot, "platform-tools", "adb.exe");
        t.Java = Which("java.exe");
        t.Keytool = Which("keytool.exe") ?? ProbeJavaHome("keytool.exe");

        var bt = FindBuildTools(t.SdkRoot);
        if (bt != null)
        {
            t.BuildToolsDir = bt;
            t.Apksigner = Path.Combine(bt, "apksigner.bat");
            if (!File.Exists(t.Apksigner)) t.Apksigner = Path.Combine(bt, "apksigner.exe");
            if (!File.Exists(t.Apksigner)) t.Apksigner = null;

            t.Zipalign = Path.Combine(bt, "zipalign.exe");
            if (!File.Exists(t.Zipalign)) t.Zipalign = null;
        }
        return t;
    }

    public static void PrintReport(ToolPaths t)
    {
        ConsoleUi.Section("工具检测结果");
        Report("adb", t.Adb, UrlPlatformTools, "用于连接设备、提取/安装 APK");
        Report("apksigner", t.Apksigner, UrlBuildTools, "用于签名 APK");
        Report("zipalign", t.Zipalign, UrlBuildTools, "用于 APK 对齐");
        Report("java", t.Java, UrlJdk, "apksigner/keytool 依赖的运行时");
        Report("keytool", t.Keytool, UrlJdk, "用于生成签名密钥");
        if (t.BuildToolsDir != null) ConsoleUi.Dim($"build-tools 目录: {t.BuildToolsDir}");
        if (t.SdkRoot != null) ConsoleUi.Dim($"Android SDK 目录: {t.SdkRoot}");
    }

    private static void Report(string name, string? path, string url, string purpose)
    {
        if (path != null)
            ConsoleUi.Ok($"{name,-10} {path}");
        else
            ConsoleUi.Fail($"{name,-10} 未找到  ({purpose})\n             下载: {url}");
    }

    private static string? FindSdkRoot()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } lad
                ? Path.Combine(lad, "Android", "Sdk") : null,
            Environment.GetEnvironmentVariable("USERPROFILE") is { } up
                ? Path.Combine(up, "AppData", "Local", "Android", "Sdk") : null,
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return c;
        }
        return null;
    }

    private static string? FindBuildTools(string? sdkRoot)
    {
        if (sdkRoot == null) return null;
        var dir = Path.Combine(sdkRoot, "build-tools");
        if (!Directory.Exists(dir)) return null;

        string? best = null;
        Version? bestVer = null;
        foreach (var d in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (!Version.TryParse(name, out var v)) continue;
            if (bestVer == null || v > bestVer)
            {
                bestVer = v;
                best = d;
            }
        }
        return best;
    }

    private static string? Probe(string? root, params string[] parts)
    {
        if (root == null) return null;
        var p = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(p) ? p : null;
    }

    private static string? ProbeJavaHome(string exe)
    {
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(jh)) return null;
        var p = Path.Combine(jh, "bin", exe);
        return File.Exists(p) ? p : null;
    }

    public static string? Which(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                var p = Path.Combine(dir, exeName);
                if (File.Exists(p)) return p;
            }
            catch
            {
                // 忽略非法路径
            }
        }
        return null;
    }
}
