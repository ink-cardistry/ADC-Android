using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaeaDarkApkCreator;

/// <summary>应用配置，持久化到工作目录下的 config.json。</summary>
internal sealed class AppConfig
{
    public string SourcePackage { get; set; } = "moe.low.arc";

    /// <summary>改包后的新包名（用于与原版共存）。</summary>
    public string NewPackage { get; set; } = "moe.low.dark";

    /// <summary>工作目录（相对路径以程序当前目录为基准）。</summary>
    public string WorkDir { get; set; } = "work";

    /// <summary>替换规则文件。</summary>
    public string RulesFile { get; set; } = Path.Combine("rules", "dark_rules.json");

    /// <summary>选中的 adb 设备序列号，空 = 自动（仅当只有一台设备时）。</summary>
    public string AdbSerial { get; set; } = "";

    /// <summary>本地导入的 APK 路径（离线模式）。</summary>
    public string LocalApk { get; set; } = "";

    /// <summary>导出目录（空 = 桌面）。</summary>
    public string ExportDir { get; set; } = "";

    public string KeystoreAlias { get; set; } = "arcaeadark";
    public string KeystorePassword { get; set; } = "arcaeadark";
    public string KeystoreDname { get; set; } = "CN=Arcaea Dark (Public Build), OU=Community, O=Arcaea Dark, L=Earth, ST=Earth, C=CN";

    /// <summary>
    /// 签名密钥文件（相对 exe 目录）。默认是随程序内置的**公共密钥**，
    /// 所有用户一致，升级时可覆盖安装、不会丢存档与已下载的曲目数据。
    /// 想换成自己的密钥，把它指向自己的 .keystore 即可（但只有同一把密钥签名的包才能互相覆盖更新）。
    /// </summary>
    public string KeystoreFile { get; set; } = Path.Combine("keystore", "arcaea-dark.keystore");

    public const string DefaultKeystoreFile = "keystore\\arcaea-dark.keystore";

    [JsonIgnore]
    public string BaseDir { get; set; } = AppContext.BaseDirectory;

    [JsonIgnore] public string WorkDirFull => Path.GetFullPath(Path.Combine(BaseDir, WorkDir));
    [JsonIgnore] public string SourceApkPath => Path.Combine(WorkDirFull, "base.apk");
    [JsonIgnore] public string EntriesListPath => Path.Combine(WorkDirFull, "entries.txt");
    [JsonIgnore] public string PlanPath => Path.Combine(WorkDirFull, "replacement_plan.json");
    [JsonIgnore] public string UnsignedApkPath => Path.Combine(WorkDirFull, "arc-dark-unsigned.apk");
    [JsonIgnore] public string AlignedApkPath => Path.Combine(WorkDirFull, "arc-dark-aligned.apk");
    [JsonIgnore] public string SignedApkPath => Path.Combine(WorkDirFull, "arc-dark-signed.apk");
    [JsonIgnore] public string KeystorePath => Path.GetFullPath(Path.Combine(BaseDir, KeystoreFile));

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>配置文件路径（固定放在 exe 同目录，双击运行/终端运行都一致）。</summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"读取 config.json 失败，使用默认配置：{ex.Message}");
        }
        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn($"保存 config.json 失败：{ex.Message}");
        }
    }

    private void Normalize()
    {
        BaseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(SourcePackage)) SourcePackage = "moe.low.arc";
        if (string.IsNullOrWhiteSpace(NewPackage)) NewPackage = "moe.low.dark";
        if (string.IsNullOrWhiteSpace(WorkDir)) WorkDir = "work";
        if (string.IsNullOrWhiteSpace(RulesFile)) RulesFile = Path.Combine("rules", "dark_rules.json");
        if (string.IsNullOrWhiteSpace(KeystoreFile)) KeystoreFile = Path.Combine("keystore", "arcaea-dark.keystore");
    }

    /// <summary>规则文件真实路径（相对路径一律相对于 exe 所在目录）。</summary>
    public string ResolveRulesFile() => Path.GetFullPath(Path.Combine(BaseDir, RulesFile));

    public void EnsureWorkDir() => Directory.CreateDirectory(WorkDirFull);
}
