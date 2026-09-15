using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ArcaeaDarkApkCreator;

internal sealed class App
{
    private AppConfig _cfg = new();
    private ToolPaths _tools = new();
    private AdbClient? _adb;
    private string? _sourceApk;
    private ReplacementPlan? _plan;
    private AxmlPatcher.ManifestInfo? _manifest;

    private const string RemoteTempApk = "/data/local/tmp/arc-dark.apk";

    /// <summary>本程序版本号（取自程序集元数据）。</summary>
    private static string AppVersion =>
        typeof(App).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "?";

    private static readonly JsonSerializerOptions PlanJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Run()
    {
        try
        {
            // 只设置输出编码（中文显示）；不要改 Console.InputEncoding，
            // 在 Windows 上强制 UTF-8 输入反而可能弄坏非 ASCII 路径，且会重置 stdin 缓冲。
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { /* 某些终端不支持，忽略 */ }

        _cfg = AppConfig.Load();
        _tools = ToolLocator.Locate();
        if (_tools.HasAdb) _adb = new AdbClient(_tools.Adb!) { Serial = _cfg.AdbSerial };
        _cfg.EnsureWorkDir();
        EnsureRulesFile();

        PrintBanner();

        while (true)
        {
            PrintMenu();
            var choice = ConsoleUi.Ask("请输入编号");

            // 输入结束（例如被重定向且内容已读完）时优雅退出，避免空转刷屏
            if (choice is null)
            {
                _cfg.Save();
                ConsoleUi.Info("输入已结束，退出。");
                return;
            }

            try
            {
                switch (choice)
                {
                    case "1": DoCheckEnv(); break;
                    case "2": DoDevices(); break;
                    case "3": DoExtract(); break;
                    case "4": DoImportLocal(); break;
                    case "5": DoAnalyze(); break;
                    case "6": DoRepack(); break;
                    case "7": DoSign(); break;
                    case "8": DoExport(); break;
                    case "9": DoInstall(); break;
                    case "a": DoAll(); break;
                    case "c": DoConfig(); break;
                    case "0":
                        _cfg.Save();
                        ConsoleUi.Info("已退出。");
                        return;
                    default:
                        ConsoleUi.Warn("无效选择，请重新输入。");
                        break;
                }
            }
            catch (Exception ex)
            {
                ConsoleUi.Fail($"操作失败：{ex.Message}");
                ConsoleUi.Dim(ex.GetType().Name);
                ConsoleUi.Pause();
            }
        }
    }

    // =====================================================================
    //  菜单
    // =====================================================================

    /// <summary>首次运行时把内置的默认规则释放到 rules/ 下，方便用户查看与修改。</summary>
    private void EnsureRulesFile()
    {
        try
        {
            var path = _cfg.ResolveRulesFile();
            if (File.Exists(path)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, RuleSet.EmbeddedDefaultJson());
            ConsoleUi.Info($"首次运行：已释放内置默认规则到 {path}");
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"无法释放内置规则文件（改包时会重试）：{ex.Message}");
        }
    }

    private void PrintBanner()
    {
        ConsoleUi.Title("Arcaea 全暗测改包工具  (光/消色/殸 → 纷争侧)");
        ConsoleUi.Dim("仅限个人研究学习使用，请勿传播或商用；改包前请先同步云端存档。");
        ConsoleUi.Dim($"版本: v{AppVersion}    工作目录: {_cfg.WorkDirFull}");
        ConsoleUi.Dim($"规则文件: {_cfg.ResolveRulesFile()}");
        ConsoleUi.Dim($"签名密钥: {_cfg.KeystorePath}{(IsDefaultKeystore() ? "  (内置公共密钥)" : "  (自定义)")}");
        if (!_tools.Ready)
        {
            ConsoleUi.Line();
            ConsoleUi.Warn("环境不完整：请先执行 1) 检测环境 查看缺失项与下载地址。");
            ConsoleUi.Warn("没有 adb 时也可用 4) 导入本地 APK 离线改包，改好后自行拷回手机安装。");
        }
    }

    private void PrintMenu()
    {
        ConsoleUi.Line();
        ConsoleUi.Line("──────── 主菜单 ────────", ConsoleColor.DarkCyan);
        ConsoleUi.Line("  1) 检测环境（adb / build-tools / JDK）", ConsoleColor.Gray);
        ConsoleUi.Line("  2) 查看 / 连接设备（有线 / 无线）", ConsoleColor.Gray);
        ConsoleUi.Line("  3) 从设备提取 Arcaea 安装包", ConsoleColor.Gray);
        ConsoleUi.Line("  4) 导入本地 APK（离线模式）", ConsoleColor.Gray);
        ConsoleUi.Line("  5) 分析 APK 并生成替换计划", ConsoleColor.Gray);
        ConsoleUi.Line("  6) 执行改包（替换资源 + 修改包名）", ConsoleColor.Gray);
        ConsoleUi.Line("  7) 对齐并签名", ConsoleColor.Gray);
        ConsoleUi.Line("  8) 导出改包 APK（不安装，可拷到手机自行安装）", ConsoleColor.Gray);
        ConsoleUi.Line("  9) 安装到设备", ConsoleColor.Gray);
        ConsoleUi.Line("  a) 一键全流程（3/4 → 5 → 6 → 7 → 8/9）", ConsoleColor.Gray);
        ConsoleUi.Line("  c) 查看 / 修改配置", ConsoleColor.DarkGray);
        ConsoleUi.Line("  0) 退出", ConsoleColor.DarkGray);
    }

    // =====================================================================
    //  1) 环境检测
    // =====================================================================

    private void DoCheckEnv()
    {
        ConsoleUi.Title("环境检测");
        _tools = ToolLocator.Locate();
        if (_tools.HasAdb) _adb = new AdbClient(_tools.Adb!) { Serial = _cfg.AdbSerial };
        ToolLocator.PrintReport(_tools);

        ConsoleUi.Section("说明");
        if (!_tools.HasAdb)
        {
            ConsoleUi.Warn("未找到 adb：无法自动提取/安装。");
            ConsoleUi.Info($"下载 platform-tools(内含 adb)：{ToolLocator.UrlPlatformTools}");
            ConsoleUi.Info("解压后把该目录加入 PATH，或用 4) 导入本地 APK 走离线模式。");
        }
        if (!_tools.HasBuildTools)
        {
            ConsoleUi.Warn("未找到 build-tools：无法 zipalign / 签名。");
            ConsoleUi.Info($"下载 build-tools：{ToolLocator.UrlBuildTools}");
            ConsoleUi.Info("或用 Android Studio 的 SDK Manager 安装 build-tools。");
        }
        if (!_tools.HasJava || !_tools.HasKeytool)
        {
            ConsoleUi.Warn("未找到 java / keytool：apksigner 与密钥生成不可用。");
            ConsoleUi.Info($"下载 JDK：{ToolLocator.UrlJdk}");
        }
        if (_tools.Ready) ConsoleUi.Ok("环境完整，可以走完整流程。");

        if (_tools.HasAdb)
        {
            ConsoleUi.Section("当前设备");
            foreach (var d in (_adb ?? new AdbClient(_tools.Adb!)).Devices())
                ConsoleUi.Info(d.ToString());
        }
        ConsoleUi.Pause();
    }

    // =====================================================================
    //  2) 设备
    // =====================================================================

    private void DoDevices()
    {
        ConsoleUi.Title("设备管理");
        if (!EnsureAdb()) return;

        while (true)
        {
            var devices = _adb!.Devices();
            ConsoleUi.Section("设备列表");
            if (devices.Count == 0) ConsoleUi.Warn("没有检测到设备。");
            else foreach (var d in devices) ConsoleUi.Info(d.ToString());

            ConsoleUi.Line();
            ConsoleUi.Line("  r) 刷新    c) 无线连接    p) 无线配对    s) 选择设备    d) 断开    q) 返回", ConsoleColor.DarkGray);
            var op = (ConsoleUi.Ask("操作") ?? "q").ToLowerInvariant();
            switch (op)
            {
                case "r":
                    break;
                case "c":
                {
                    ConsoleUi.Dim("手机：开发者选项 → 无线调试 → 记下 IP 与端口（形如 192.168.1.5:37215）");
                    var target = ConsoleUi.AskRequired("输入 ip:端口");
                    var res = _adb.Connect(target);
                    if (res.Ok) ConsoleUi.Ok(res.All);
                    else ConsoleUi.Fail(res.All);
                    break;
                }
                case "p":
                {
                    ConsoleUi.Dim("手机：开发者选项 → 无线调试 → 使用配对码配对设备（形如 192.168.1.5:37123 + 6位配对码）");
                    var target = ConsoleUi.AskRequired("输入 配对用 ip:端口");
                    var code = ConsoleUi.AskRequired("输入 6 位配对码");
                    var res = _adb.Pair(target, code);
                    if (res.Ok) ConsoleUi.Ok(res.All);
                    else ConsoleUi.Fail(res.All);
                    break;
                }
                case "s":
                {
                    if (devices.Count == 0) { ConsoleUi.Warn("没有可选择的设备。"); break; }
                    for (int i = 0; i < devices.Count; i++)
                        ConsoleUi.Info($"{i + 1}. {devices[i]}");
                    var idx = ConsoleUi.AskRequired("输入序号");
                    if (int.TryParse(idx, out var n) && n >= 1 && n <= devices.Count)
                    {
                        _cfg.AdbSerial = devices[n - 1].Serial;
                        _adb.Serial = _cfg.AdbSerial;
                        _cfg.Save();
                        ConsoleUi.Ok($"已选择设备: {_cfg.AdbSerial}");
                    }
                    else ConsoleUi.Warn("序号无效。");
                    break;
                }
                case "d":
                {
                    var serial = string.IsNullOrEmpty(_adb.Serial)
                        ? ConsoleUi.AskRequired("要断开的 ip:端口")
                        : _adb.Serial;
                    ConsoleUi.Ok(_adb.Disconnect(serial).All);
                    break;
                }
                default:
                    return;
            }
        }
    }

    private bool EnsureAdb()
    {
        if (_tools.HasAdb && _adb != null) return true;
        ConsoleUi.Fail("未找到 adb，无法进行设备相关操作。");
        ConsoleUi.Info($"请安装 platform-tools：{ToolLocator.UrlPlatformTools}");
        ConsoleUi.Info("安装后把目录加入 PATH 并重启本程序；或用 4) 导入本地 APK 走离线模式。");
        ConsoleUi.Pause();
        return false;
    }

    /// <summary>
    /// 确保有一个真正可用的设备。
    /// 先挑候选（优先配置里保存的），再逐个探测是否响应 —— 无线调试端口会变，
    /// `adb devices` 里可能残留失效条目，对它下任何命令都会**永久挂住**。
    /// </summary>
    private bool EnsureDevice()
    {
        if (!EnsureAdb()) return false;

        var online = _adb!.Devices().Where(d => d.Online).ToList();
        if (online.Count == 0)
        {
            ConsoleUi.Fail("没有处于 online 状态的设备。");
            ConsoleUi.Info("请用 USB 连接并开启 USB 调试，或用 2) 无线连接 / 无线配对。");
            ConsoleUi.Info("若手机上弹出“允许 USB 调试”，请在手机上点击允许。");
            return false;
        }

        var candidates = new List<AdbDevice>();
        var saved = online.FirstOrDefault(d => d.Serial == _cfg.AdbSerial);
        if (saved != null) candidates.Add(saved);
        candidates.AddRange(online.Where(d => d != saved));

        if (saved == null && candidates.Count > 1)
        {
            ConsoleUi.Warn("检测到多台设备，请选择：");
            for (int i = 0; i < candidates.Count; i++) ConsoleUi.Info($"{i + 1}. {candidates[i]}");
            var idx = ConsoleUi.AskRequired("输入序号");
            if (!int.TryParse(idx, out var n) || n < 1 || n > candidates.Count)
            {
                ConsoleUi.Fail("序号无效。");
                return false;
            }
            var chosen = candidates[n - 1];
            candidates.Remove(chosen);
            candidates.Insert(0, chosen);
        }

        ConsoleUi.Dim("正在确认设备是否响应……");
        foreach (var d in candidates)
        {
            _adb.Serial = d.Serial;
            if (!_adb.IsResponsive()) 
            {
                ConsoleUi.Warn($"设备 {d.Serial} 无响应（多为失效的无线调试连接），换下一个……");
                continue;
            }
            if (_cfg.AdbSerial != d.Serial)
            {
                _cfg.AdbSerial = d.Serial;
                _cfg.Save();
            }
            ConsoleUi.Info($"使用设备: {d.Serial}");
            return true;
        }

        ConsoleUi.Fail("所有设备连接都无响应，已停止（否则命令会无限挂起）。");
        ConsoleUi.Info("无线调试的端口会变，旧连接常会变成这种「假在线」状态。建议：");
        ConsoleUi.Info("  · 用 2) 无线调试 重新配对 / 重新连接；或");
        ConsoleUi.Info("  · 在 2) 里对失效条目执行断开，再连可用的那条");
        _adb.Serial = _cfg.AdbSerial;
        return false;
    }

    // =====================================================================
    //  3) 提取
    // =====================================================================

    private void DoExtract()
    {
        ConsoleUi.Title("从设备提取 Arcaea 安装包");
        if (!EnsureDevice()) return;

        var remotePath = _adb!.GetPackageApkPath(_cfg.SourcePackage);
        if (remotePath == null)
        {
            ConsoleUi.Fail($"设备上未找到已安装的 {_cfg.SourcePackage}。");
            ConsoleUi.Info("请先在手机上安装 Arcaea（正式版），或确认包名是否正确（见 c) 配置）。");
            return;
        }
        ConsoleUi.Ok($"找到安装包: {remotePath}");

        var size = _adb.GetRemoteSize(remotePath);
        ConsoleUi.Info($"大小: {(size > 0 ? ConsoleUi.Human(size) : "未知")}");
        ConsoleUi.Warn("文件较大（约 2 GB），拉取需要较长时间，请耐心等待，不要拔线。");

        _cfg.EnsureWorkDir();
        if (!_adb.PullWithProgress(remotePath, _cfg.SourceApkPath, "拉取 base.apk"))
        {
            ConsoleUi.Fail("提取失败。");
            return;
        }

        _sourceApk = _cfg.SourceApkPath;
        _plan = null;
        _manifest = null;
        DumpEntriesIfPossible();

        ConsoleUi.Ok($"安装包已就绪: {_sourceApk}");
        ConsoleUi.Info("下一步：执行 5) 分析并生成替换计划。");
        ConsoleUi.Pause();
    }

    // =====================================================================
    //  4) 导入本地 APK
    // =====================================================================

    private void DoImportLocal()
    {
        ConsoleUi.Title("导入本地 APK（离线模式）");
        ConsoleUi.Dim("在手机上用 MT 管理器 / 系统“提取安装包”功能导出 Arcaea 的 APK，再拷到电脑。");

        var def = string.IsNullOrWhiteSpace(_cfg.LocalApk) ? null : _cfg.LocalApk;
        var path = ConsoleUi.AskRequired("输入 APK 完整路径", def);
        path = path.Trim().Trim('"');

        if (!File.Exists(path))
        {
            ConsoleUi.Fail($"文件不存在: {path}");
            return;
        }
        if (!LooksLikeApk(path, out var why))
        {
            ConsoleUi.Fail($"不是有效的 APK: {why}");
            return;
        }

        _cfg.LocalApk = path;
        _cfg.Save();
        _sourceApk = path;
        _plan = null;
        _manifest = null;

        var len = new FileInfo(path).Length;
        ConsoleUi.Ok($"已导入: {path} ({ConsoleUi.Human(len)})");
        ConsoleUi.Info("下一步：执行 5) 分析并生成替换计划。");
        ConsoleUi.Pause();
    }

    private static bool LooksLikeApk(string path, out string why)
    {
        why = "";
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            if (!names.Contains("AndroidManifest.xml")) { why = "缺少 AndroidManifest.xml"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    // =====================================================================
    //  5) 分析
    // =====================================================================

    private bool DoAnalyze(bool interactive = true)
    {
        if (interactive) ConsoleUi.Title("分析 APK 并生成替换计划");
        if (!EnsureSourceApk()) return false;

        ConsoleUi.Info($"源 APK: {_sourceApk}");

        if (EnsureManifestInfo())
        {
            ConsoleUi.Info($"包名  : {_manifest!.Package}");
            ConsoleUi.Info($"版本  : {SourceVersionText}");
            if (!string.Equals(_manifest.Package, _cfg.SourcePackage, StringComparison.Ordinal))
                ConsoleUi.Warn($"APK 内包名与配置中的 {_cfg.SourcePackage} 不一致，改包将以 APK 内读到的包名为准。");
        }

        using var apk = new ApkArchive(_sourceApk!);
        ConsoleUi.Info($"条目数: {apk.EntryCount}，解压后总量: {ConsoleUi.Human(apk.TotalUncompressedBytes())}");

        // 导出完整条目清单，便于人工核对规则
        try
        {
            apk.DumpEntries(_cfg.EntriesListPath);
            ConsoleUi.Dim($"已导出条目清单: {_cfg.EntriesListPath}");
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"导出条目清单失败: {ex.Message}");
        }

        var rulesPath = _cfg.ResolveRulesFile();
        if (!File.Exists(rulesPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rulesPath)!);
                File.WriteAllText(rulesPath, RuleSet.EmbeddedDefaultJson());
                ConsoleUi.Info($"已释放内置默认规则: {rulesPath}");
            }
            catch (Exception ex)
            {
                ConsoleUi.Fail($"规则文件不存在，且无法释放内置规则：{ex.Message}");
                return false;
            }
        }

        RuleSet rules;
        try
        {
            rules = RuleSet.Load(rulesPath);
        }
        catch (Exception ex)
        {
            ConsoleUi.Fail($"规则文件解析失败：{ex.Message}");
            ConsoleUi.Info($"请修正或删除该文件后重试（删除后会重新释放内置规则）：{rulesPath}");
            return false;
        }
        ConsoleUi.Info($"规则文件: {rulesPath}  (对照 {rules.Pairs.Count} 条 / 正则 {rules.Patterns.Count} 条)");

        _plan = rules.BuildPlan(apk);

        ConsoleUi.Section("替换计划");
        ConsoleUi.Ok($"将替换 {_plan.Replacements.Count} 个资源文件" +
                     (_plan.EstimatedTargetBytes > 0
                         ? $"（来源内容合计 {ConsoleUi.Human(_plan.EstimatedTargetBytes)}）"
                         : ""));

        var byKind = _plan.Replacements
            .GroupBy(r => r.Kind)
            .Select(g => $"{g.Key}={g.Count()}");
        ConsoleUi.Dim(string.Join("  ", byKind));

        if (_plan.Skips.Count > 0)
        {
            ConsoleUi.Section("跳过项（属正常，通常是原包没有对应的纷争侧文件）");
            foreach (var s in _plan.Skips.Take(40))
                ConsoleUi.Dim($"跳过 {s.From}\n      → {s.To}   原因: {s.Reason}");
            if (_plan.Skips.Count > 40) ConsoleUi.Dim($"... 其余 {_plan.Skips.Count - 40} 条见 {_cfg.PlanPath}");
        }

        if (_plan.UnhandledSideSources.Count > 0)
        {
            ConsoleUi.Section("可能遗漏的分侧资源（未被任何规则覆盖，请按需补充规则）");
            foreach (var u in _plan.UnhandledSideSources.Take(40)) ConsoleUi.Dim(u);
            if (_plan.UnhandledSideSources.Count > 40)
                ConsoleUi.Dim($"... 其余 {_plan.UnhandledSideSources.Count - 40} 条");
            ConsoleUi.Info("如需补规则，请编辑 rules/dark_rules.json 的 pairs / patterns，无需改代码。");
        }

        try
        {
            File.WriteAllText(_cfg.PlanPath, JsonSerializer.Serialize(_plan, PlanJson));
            ConsoleUi.Dim($"计划已保存: {_cfg.PlanPath}");
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"保存计划失败: {ex.Message}");
        }

        if (interactive)
        {
            ConsoleUi.Info("下一步：执行 6) 执行改包。");
            ConsoleUi.Pause();
        }
        return true;
    }

    private bool EnsureSourceApk()
    {
        if (_sourceApk != null && File.Exists(_sourceApk)) return true;

        if (File.Exists(_cfg.SourceApkPath))
        {
            _sourceApk = _cfg.SourceApkPath;
            ConsoleUi.Info($"使用已提取的安装包: {_sourceApk}");
            return true;
        }
        if (!string.IsNullOrWhiteSpace(_cfg.LocalApk) && File.Exists(_cfg.LocalApk))
        {
            _sourceApk = _cfg.LocalApk;
            ConsoleUi.Info($"使用本地导入的安装包: {_sourceApk}");
            return true;
        }

        ConsoleUi.Warn("还没有可用的源 APK。");
        ConsoleUi.Info("  3) 从设备提取   或   4) 导入本地 APK");
        var op = (ConsoleUi.Ask("现在提取/导入？(3=提取 4=导入 q=取消)", "3") ?? "q").ToLowerInvariant();
        if (op == "3") { DoExtract(); return _sourceApk != null && File.Exists(_sourceApk); }
        if (op == "4") { DoImportLocal(); return _sourceApk != null && File.Exists(_sourceApk); }
        return false;
    }

    private void DumpEntriesIfPossible()
    {
        try
        {
            using var apk = new ApkArchive(_sourceApk!);
            apk.DumpEntries(_cfg.EntriesListPath);
            ConsoleUi.Dim($"已导出条目清单: {_cfg.EntriesListPath}");
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"导出条目清单失败: {ex.Message}");
        }
    }

    /// <summary>从源 APK 的 AndroidManifest.xml 读取包名 / 版本（无需额外工具）。</summary>
    private bool EnsureManifestInfo()
    {
        if (_manifest != null) return true;
        if (!EnsureSourceApk()) return false;

        try
        {
            using var apk = new ApkArchive(_sourceApk!);
            if (!apk.Has("AndroidManifest.xml"))
            {
                ConsoleUi.Warn("APK 中找不到 AndroidManifest.xml，无法读取包名/版本。");
                return false;
            }
            _manifest = AxmlPatcher.ReadManifestInfo(apk.ReadAllBytes("AndroidManifest.xml"));
            if (_manifest == null)
            {
                ConsoleUi.Warn("未能从 AndroidManifest.xml 解析出包名/版本。");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"读取 APK 信息失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>实际用于改包的源包名（优先用 APK 里读到的）。</summary>
    private string SourcePackageName => _manifest?.Package ?? _cfg.SourcePackage;

    private string SourceVersionText
    {
        get
        {
            if (_manifest == null) return "未知";
            var name = string.IsNullOrWhiteSpace(_manifest.VersionName) ? "?" : _manifest.VersionName;
            return _manifest.VersionCode > 0 ? $"{name} (versionCode {_manifest.VersionCode})" : name;
        }
    }

    /// <summary>导出用的文件名，例如 arcaea-dark-7.0.255c.apk。</summary>
    private string BuildExportFileName()
    {
        var name = "arcaea-dark";
        var ver = _manifest?.VersionName;
        if (!string.IsNullOrWhiteSpace(ver))
        {
            var safe = new string(ver.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (safe.Length > 0) name += "-" + safe;
        }
        return name + ".apk";
    }

    // =====================================================================
    //  6) 改包
    // =====================================================================

    private bool DoRepack(bool interactive = true)
    {
        if (interactive) ConsoleUi.Title("执行改包（替换资源 + 修改包名）");
        if (!EnsureSourceApk()) return false;
        if (!EnsureManifestInfo()) return false;
        if (_plan == null && !DoAnalyze(false)) return false;
        if (_plan == null) return false;

        var sourcePkg = SourcePackageName;
        var newPkg = _cfg.NewPackage;
        if (string.Equals(newPkg, sourcePkg, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUi.Warn("新包名与原包名相同！");
            ConsoleUi.Warn("这样生成的包会因签名不同而无法与原版共存，安装前必须卸载原版（会丢存档）。");
            ConsoleUi.Info("建议改一个新包名，例如 moe.low.dark。可在 c) 配置 中修改。");
            if (!ConsoleUi.Confirm("仍要继续？", false)) return false;
        }

        ConsoleUi.Info($"源 APK : {_sourceApk}");
        ConsoleUi.Info($"版本   : {SourceVersionText}");
        ConsoleUi.Info($"输出   : {_cfg.UnsignedApkPath}");
        ConsoleUi.Info($"包名   : {sourcePkg}  →  {newPkg}");

        if (!CheckDiskSpace()) return false;

        using var apk = new ApkArchive(_sourceApk!);

        var overrides = new Dictionary<string, RepackItem>(StringComparer.Ordinal);
        foreach (var r in _plan.Replacements)
            overrides[r.From] = new RepackItem { Path = r.From, FromEntry = r.To, Note = r.Note };

        // ---- 修改包名：AndroidManifest.xml ----
        ConsoleUi.Section("修改包名");
        if (apk.Has("AndroidManifest.xml"))
        {
            var axml = apk.ReadAllBytes("AndroidManifest.xml");
            var patched = AxmlPatcher.ReplacePackageName(axml, sourcePkg, newPkg, out var r1);
            if (r1.Changed)
            {
                overrides["AndroidManifest.xml"] = new RepackItem { Path = "AndroidManifest.xml", Content = patched };
                ConsoleUi.Ok(r1.Message);
            }
            else ConsoleUi.Warn(r1.Message);
        }
        else
        {
            ConsoleUi.Fail("找不到 AndroidManifest.xml，无法修改包名。");
            return false;
        }

        // ---- 修改包名：resources.arsc ----
        if (apk.Has("resources.arsc"))
        {
            var arsc = apk.ReadAllBytes("resources.arsc");
            var pkgs = ArscPatcher.ListPackages(arsc);
            if (pkgs.Count > 0) ConsoleUi.Dim("resources.arsc 中的 package: " + string.Join(", ", pkgs));

            var patched = ArscPatcher.ReplacePackageName(arsc, sourcePkg, newPkg, out var r2);
            if (r2.Changed)
            {
                overrides["resources.arsc"] = new RepackItem { Path = "resources.arsc", Content = patched };
                ConsoleUi.Ok(r2.Message);
            }
            else ConsoleUi.Warn(r2.Message);
        }
        else
        {
            ConsoleUi.Warn("找不到 resources.arsc（跳过 ARSC 包名替换）。");
        }

        // ---- 重打包 ----
        ConsoleUi.Section("重打包");
        ConsoleUi.Warn("需要完整读写约 2 GB，耗时较长，请勿中断。");
        var bar = new ProgressBar("重打包", apk.TotalUncompressedBytes());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ApkRepacker.Repack(_sourceApk!, _cfg.UnsignedApkPath, overrides, (done, total) => bar.Set(done));
        bar.Complete();
        sw.Stop();

        var outLen = new FileInfo(_cfg.UnsignedApkPath).Length;
        ConsoleUi.Ok($"改包完成: {_cfg.UnsignedApkPath}");
        ConsoleUi.Info($"输出大小 {ConsoleUi.Human(outLen)}，耗时 {sw.Elapsed:mm\\:ss}");
        ConsoleUi.Info($"实际替换条目 {overrides.Count} 个（含 1~2 个包名补丁条目）");

        if (interactive)
        {
            ConsoleUi.Info("下一步：执行 7) 对齐并签名。");
            ConsoleUi.Pause();
        }
        return true;
    }

    private bool CheckDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_cfg.WorkDirFull);
            if (root == null) return true;
            var drive = new DriveInfo(root);
            var free = drive.AvailableFreeSpace;
            var need = new FileInfo(_sourceApk!).Length * 3L;
            ConsoleUi.Dim($"可用磁盘 {ConsoleUi.Human(free)}，预计需要 {ConsoleUi.Human(need)}（重打包+zipalign+签名）");
            if (free < need)
            {
                ConsoleUi.Fail("磁盘空间可能不足，请清理后重试。");
                return ConsoleUi.Confirm("仍要继续？", false);
            }
        }
        catch { /* 忽略 */ }
        return true;
    }

    // =====================================================================
    //  7) 签名
    // =====================================================================

    private bool DoSign(bool interactive = true)
    {
        if (interactive) ConsoleUi.Title("对齐并签名");
        if (interactive)
        {
            ToolLocator.PrintReport(_tools);
        }
        if (!_tools.HasBuildTools)
        {
            ConsoleUi.Fail($"缺少 build-tools（apksigner / zipalign）：{ToolLocator.UrlBuildTools}");
            if (interactive) ConsoleUi.Pause();
            return false;
        }
        if (!File.Exists(_cfg.UnsignedApkPath))
        {
            ConsoleUi.Fail($"找不到未签名包: {_cfg.UnsignedApkPath}");
            ConsoleUi.Info("请先执行 6) 执行改包。");
            if (interactive) ConsoleUi.Pause();
            return false;
        }

        if (!ApkSigner.EnsureKeystore(_tools, _cfg.KeystorePath, _cfg.KeystoreAlias,
                _cfg.KeystorePassword, _cfg.KeystoreDname))
            return false;

        var fp = ApkSigner.KeystoreFingerprint(_tools, _cfg.KeystorePath, _cfg.KeystoreAlias, _cfg.KeystorePassword);
        if (!string.IsNullOrEmpty(fp)) ConsoleUi.Dim($"密钥证书 SHA-256: {fp}");

        ConsoleUi.Info("升级提示：只要【包名】与【签名密钥】都不变，新包可以直接覆盖安装，");
        ConsoleUi.Info("          存档与已下载的曲目数据都会保留；换密钥或换包名就必须先卸载重来。");
        if (!IsDefaultKeystore())
            ConsoleUi.Warn("当前使用的是自定义密钥：只有同一把密钥签名的包才能覆盖安装。");

        if (!ApkSigner.Zipalign(_tools, _cfg.UnsignedApkPath, _cfg.AlignedApkPath)) return false;
        if (!ApkSigner.Sign(_tools, _cfg.AlignedApkPath, _cfg.SignedApkPath, _cfg.KeystorePath,
                _cfg.KeystoreAlias, _cfg.KeystorePassword)) return false;

        // aligned 只是 zipalign 的中间产物，签完就没用了；unsigned 留着以便重新签名
        if (File.Exists(_cfg.AlignedApkPath))
        {
            TryDelete(_cfg.AlignedApkPath);
            ConsoleUi.Dim("已删除中间产物 aligned（释放约 2GB）。");
        }

        if (ApkSigner.Verify(_tools, _cfg.SignedApkPath, out var detail))
        {
            ConsoleUi.Ok("签名校验通过。");
            foreach (var line in detail.Split('\n').Take(6)) ConsoleUi.Dim(line.Trim());
        }
        else
        {
            ConsoleUi.Warn("签名校验未通过，请检查 apksigner 输出：");
            ConsoleUi.Dim(detail.Length > 500 ? detail[..500] : detail);
        }

        if (ConsoleUi.Confirm("是否把 unsigned（约 2GB）也删掉？删后要重签需先重跑 6) 执行改包（约 8 秒）", false))
        {
            TryDelete(_cfg.UnsignedApkPath);
            ConsoleUi.Ok("已清理 unsigned。");
        }

        ConsoleUi.Ok($"最终成品: {_cfg.SignedApkPath}");
        if (interactive)
        {
            ConsoleUi.Info("下一步：8) 导出改包 APK（不安装）  或  9) 安装到设备。");
            ConsoleUi.Pause();
        }
        return true;
    }

    /// <summary>当前是否用的是内置的公共密钥（决定能否与其它用户互相覆盖更新）。</summary>
    private bool IsDefaultKeystore()
        => string.Equals(
            Path.GetFullPath(_cfg.KeystorePath),
            Path.GetFullPath(Path.Combine(_cfg.BaseDir, AppConfig.DefaultKeystoreFile)),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    // =====================================================================
    //  8) 导出 APK（不安装）
    // =====================================================================

    private bool DoExport(bool interactive = true)
    {
        if (interactive) ConsoleUi.Title("导出改包 APK（不安装）");
        if (!File.Exists(_cfg.SignedApkPath))
        {
            ConsoleUi.Fail($"找不到已签名包: {_cfg.SignedApkPath}");
            ConsoleUi.Info("请先执行 7) 对齐并签名。");
            if (interactive) ConsoleUi.Pause();
            return false;
        }

        EnsureManifestInfo();

        var defaultDir = _cfg.ExportDir;
        if (string.IsNullOrWhiteSpace(defaultDir) || !Directory.Exists(defaultDir))
        {
            defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(defaultDir) || !Directory.Exists(defaultDir))
                defaultDir = _cfg.WorkDirFull;
        }

        var fileName = BuildExportFileName();
        var defaultPath = Path.Combine(defaultDir, fileName);

        ConsoleUi.Info($"待导出源文件: {_cfg.SignedApkPath} ({ConsoleUi.Human(new FileInfo(_cfg.SignedApkPath).Length)})");
        ConsoleUi.Dim("可直接回车使用默认路径；也可输入目录或完整文件路径。");

        var input = ConsoleUi.Ask("导出到", defaultPath);
        var target = string.IsNullOrWhiteSpace(input) ? defaultPath : input.Trim().Trim('"');

        try
        {
            if (Directory.Exists(target)) target = Path.Combine(target, fileName);

            var dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (string.IsNullOrWhiteSpace(dir))
            {
                ConsoleUi.Fail("无法解析导出目录。");
                return false;
            }
            Directory.CreateDirectory(dir);

            // 用"移动"而不是"复制"：签名包约 2GB，复制会让磁盘占用翻倍
            var sameVolume = string.Equals(
                Path.GetPathRoot(Path.GetFullPath(_cfg.SignedApkPath)),
                Path.GetPathRoot(Path.GetFullPath(target)),
                StringComparison.OrdinalIgnoreCase);
            if (!sameVolume)
                ConsoleUi.Warn("目标在不同磁盘：移动需要先复制再删除，过程中会短暂多占约 2GB。");

            ConsoleUi.Dim($"正在移动（非复制）到 {target} ...");
            File.Move(_cfg.SignedApkPath, target, overwrite: true);

            _cfg.ExportDir = dir;
            _cfg.Save();

            ConsoleUi.Ok($"已导出: {target} ({ConsoleUi.Human(new FileInfo(target).Length)})");
            ConsoleUi.Info("已采用移动，work 里不再保留副本。");
            ConsoleUi.Info("如需再安装到设备，请重新执行 7) 对齐并签名，或把导出的文件拷回 work 并改名为 arc-dark-signed.apk。");
            ConsoleUi.Info("把该 APK 拷到手机后点击安装即可（需在手机上允许安装未知来源）。");
            if (interactive) ConsoleUi.Pause();
            return true;
        }
        catch (Exception ex)
        {
            ConsoleUi.Fail($"导出失败：{ex.Message}");
            if (interactive) ConsoleUi.Pause();
            return false;
        }
    }

    // =====================================================================
    //  9) 安装
    // =====================================================================

    private bool DoInstall(bool interactive = true)
    {
        if (interactive) ConsoleUi.Title("安装到设备");
        if (!File.Exists(_cfg.SignedApkPath))
        {
            ConsoleUi.Fail($"找不到已签名包: {_cfg.SignedApkPath}");
            ConsoleUi.Info("请先执行 7) 对齐并签名。");
            if (interactive) ConsoleUi.Pause();
            return false;
        }
        if (!EnsureDevice())
        {
            ConsoleUi.Info("没有 adb 也可以离线处理：用 8) 导出改包 APK，再把文件拷到手机手动点击安装。");
            ConsoleUi.Ok($"{_cfg.SignedApkPath}");
            if (interactive) ConsoleUi.Pause();
            return false;
        }

        ConsoleUi.Info($"待安装: {_cfg.SignedApkPath} ({ConsoleUi.Human(new FileInfo(_cfg.SignedApkPath).Length)})");

        if (!_adb!.PushWithProgress(_cfg.SignedApkPath, RemoteTempApk, "推送到设备"))
        {
            ConsoleUi.Fail("推送失败。");
            if (interactive) ConsoleUi.Pause();
            return false;
        }

        ConsoleUi.Line();
        ConsoleUi.Warn("================================================================");
        ConsoleUi.Warn("  请注意：手机上即将弹出安装/校验界面");
        ConsoleUi.Warn("  请解锁手机并手动点击【安装】/【允许】（可能要等几秒到几十秒）");
        if (new[] { "moe.low.dark", "moe.low.arc.dark" }.Contains(_cfg.NewPackage))
        {
            ConsoleUi.Warn("  本次包名已改为 " + _cfg.NewPackage + "，可与原版共存");
        }
        ConsoleUi.Warn("================================================================");
        ConsoleUi.Line();

        if (interactive && !ConsoleUi.Confirm("已准备好，开始安装？", true))
        {
            _adb.RemoveRemote(RemoteTempApk);
            return false;
        }

        var res = _adb.InstallWithSpinner(RemoteTempApk, "正在安装（请在手机上确认）");

        _adb.RemoveRemote(RemoteTempApk);

        var all = res.All;
        if (all.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUi.Ok("安装成功！");
            ConsoleUi.Info($"包名: {_cfg.NewPackage}");
            ConsoleUi.Info("若游戏首次启动显示异常，可尝试清除该包数据后重开。");
            if (interactive) ConsoleUi.Pause();
            return true;
        }

        ConsoleUi.Fail("安装失败。");
        ConsoleUi.Dim(all.Length > 1200 ? all[..1200] : all);
        ConsoleUi.Line();
        ConsoleUi.Info("常见原因：");
        ConsoleUi.Info("  1. 手机上未点“允许安装”（未知来源 / 安装校验）");
        ConsoleUi.Info("  2. 与已安装的同包名应用签名冲突 → 需先卸载原版（注意先云端同步存档）");
        ConsoleUi.Info("  3. 存储空间不足（安装需要数 GB 空闲）");
        ConsoleUi.Info("  也可以改用 8) 导出改包 APK，拷到手机后手动安装。");
        if (interactive) ConsoleUi.Pause();
        return false;
    }

    // =====================================================================
    //  9) 一键
    // =====================================================================

    private void DoAll()
    {
        ConsoleUi.Title("一键全流程");

        if (_sourceApk == null || !File.Exists(_sourceApk))
        {
            ConsoleUi.Info("第 1 步：获取源 APK");
            var op = (ConsoleUi.Ask("3=从设备提取  4=导入本地 APK", "3") ?? "3").ToLowerInvariant();
            if (op == "4") DoImportLocal(); else DoExtract();
            if (_sourceApk == null || !File.Exists(_sourceApk))
            {
                ConsoleUi.Fail("未取得源 APK，流程终止。");
                return;
            }
        }

        ConsoleUi.Info("第 2 步：分析");
        if (!DoAnalyze(false)) { ConsoleUi.Fail("分析失败，流程终止。"); ConsoleUi.Pause(); return; }

        if (_plan != null && _plan.Replacements.Count == 0)
        {
            ConsoleUi.Fail("没有任何可替换项，请检查规则文件。");
            ConsoleUi.Pause();
            return;
        }

        ConsoleUi.Info("第 3 步：改包");
        if (!DoRepack(false)) { ConsoleUi.Fail("改包失败，流程终止。"); ConsoleUi.Pause(); return; }

        ConsoleUi.Info("第 4 步：签名");
        if (!DoSign(false)) { ConsoleUi.Fail("签名失败，流程终止。"); ConsoleUi.Pause(); return; }

        ConsoleUi.Section("第 5 步：输出");
        ConsoleUi.Info($"成品: {_cfg.SignedApkPath}");
        ConsoleUi.Dim("默认只导出文件，不会碰你的设备；需要直接装机再选 i。");
        var act = (ConsoleUi.Ask("e=导出文件  i=安装到设备  b=两者都要", "e") ?? "e").Trim().ToLowerInvariant();

        var doExport = act is "e" or "b" or "";
        var doInstall = act is "i" or "b";

        // 先安装再导出：导出用的是"移动"，会移走 work 里的签名包
        if (doInstall) DoInstall(false);
        if (doExport) DoExport(false);
        if (!doExport && !doInstall) ConsoleUi.Info($"已跳过输出，成品保留在: {_cfg.SignedApkPath}");

        ConsoleUi.Line();
        ConsoleUi.Ok("全流程结束。");
        ConsoleUi.Pause();
    }

    // =====================================================================
    //  配置
    // =====================================================================

    private void DoConfig()
    {
        ConsoleUi.Title("配置");
        ConsoleUi.Info($"1. 源包名        : {_cfg.SourcePackage}");
        ConsoleUi.Info($"2. 新包名        : {_cfg.NewPackage}");
        ConsoleUi.Info($"3. 工作目录      : {_cfg.WorkDirFull}");
        ConsoleUi.Info($"4. 规则文件      : {_cfg.ResolveRulesFile()}");
        ConsoleUi.Info($"5. 选中设备      : {(string.IsNullOrEmpty(_cfg.AdbSerial) ? "(自动)" : _cfg.AdbSerial)}");
        ConsoleUi.Info($"6. 本地APK(离线) : {(string.IsNullOrEmpty(_cfg.LocalApk) ? "(未设置)" : _cfg.LocalApk)}");
        ConsoleUi.Info($"7. 导出目录      : {(string.IsNullOrEmpty(_cfg.ExportDir) ? "(默认: 桌面)" : _cfg.ExportDir)}");
        ConsoleUi.Info($"8. 签名密钥      : {_cfg.KeystorePath}{(IsDefaultKeystore() ? "  (内置公共密钥)" : "  (自定义)")}");
        ConsoleUi.Info($"9. 密钥别名/口令 : {_cfg.KeystoreAlias} / {_cfg.KeystorePassword}");

        var op = ConsoleUi.Ask("修改哪一项？(直接回车返回)", "");
        if (string.IsNullOrWhiteSpace(op)) return;

        switch (op.Trim())
        {
            case "1": _cfg.SourcePackage = ConsoleUi.AskRequired("源包名", _cfg.SourcePackage); break;
            case "2": _cfg.NewPackage = ConsoleUi.AskRequired("新包名", _cfg.NewPackage); break;
            case "3": _cfg.WorkDir = ConsoleUi.AskRequired("工作目录", _cfg.WorkDir); break;
            case "4": _cfg.RulesFile = ConsoleUi.AskRequired("规则文件", _cfg.RulesFile); break;
            case "5": _cfg.AdbSerial = ConsoleUi.Ask("设备序列号(空=自动)", _cfg.AdbSerial) ?? ""; break;
            case "6": _cfg.LocalApk = ConsoleUi.Ask("本地 APK 路径", _cfg.LocalApk) ?? ""; break;
            case "7": _cfg.ExportDir = ConsoleUi.Ask("导出目录(空=桌面)", _cfg.ExportDir) ?? ""; break;
            case "8":
                ConsoleUi.Warn("换成自己的密钥后，只有用同一把密钥签名的包才能覆盖安装（别人用公共密钥签的包就不行了）。");
                _cfg.KeystoreFile = ConsoleUi.AskRequired("密钥文件(相对 exe 目录)", _cfg.KeystoreFile);
                break;
            case "9":
                _cfg.KeystoreAlias = ConsoleUi.AskRequired("密钥别名", _cfg.KeystoreAlias);
                _cfg.KeystorePassword = ConsoleUi.AskRequired("密钥口令", _cfg.KeystorePassword);
                break;
            default:
                ConsoleUi.Warn("无效选项。");
                return;
        }
        _cfg.Save();
        if (_tools.HasAdb) _adb = new AdbClient(_tools.Adb!) { Serial = _cfg.AdbSerial };
        ConsoleUi.Ok("已保存到 config.json");
    }
}
