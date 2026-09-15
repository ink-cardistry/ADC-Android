using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ArcaeaDarkApkCreator;

internal sealed class RulePair
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Note { get; set; }
}

internal sealed class RulePattern
{
    public string Match { get; set; } = "";
    public string Replace { get; set; } = "";
    public string? Note { get; set; }
}

internal sealed class Replacement
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = "pair";
    public string? Note { get; set; }
}

internal sealed class SkipRecord
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Reason { get; set; } = "";
}

internal sealed class ReplacementPlan
{
    public List<Replacement> Replacements { get; } = new();
    public List<SkipRecord> Skips { get; } = new();
    public List<string> UnhandledSideSources { get; } = new();
    public int TotalEntries { get; set; }
    public long EstimatedTargetBytes { get; set; }

    public int SourceMissingCount => Skips.Count(s => s.Reason.Contains("源文件"));
    public int TargetMissingCount => Skips.Count(s => s.Reason.Contains("目标"));
}

/// <summary>替换规则集合：加载 JSON、编译正则、生成替换计划。</summary>
internal sealed class RuleSet
{
    public int Version { get; set; } = 1;
    public string? Description { get; set; }
    public string? TargetSide { get; set; }
    public List<RulePair> Pairs { get; set; } = new();
    public List<RulePattern> Patterns { get; set; } = new();

    [JsonIgnore] private readonly List<(Regex Rx, string Replace, string? Note)> _compiled = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>看起来是"光芒侧/消色侧/殸侧"资源（可能仍需替换）的候选目录。</summary>
    private static readonly string[] SideAssetDirs =
    {
        "assets/img/", "assets/models/", "assets/particle/", "assets/layouts/",
    };

    private static readonly Regex SideSourceToken = new(
        @"_light(?![a-z])|-light(?![a-z])|_colorless|_colourless|-colorless|-colourless|_lephon(?![a-z])|-lephon(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DarkToken = new(
        @"dark|conflict|_conf(?![a-z])|-conf(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RuleSet Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"规则文件不存在: {path}");

        var json = File.ReadAllText(path);
        var rs = JsonSerializer.Deserialize<RuleSet>(json, JsonOpts)
                 ?? throw new InvalidDataException("规则文件解析结果为空");
        rs.Compile();
        return rs;
    }

    /// <summary>取出编译进 exe 的默认规则 JSON（用于首次运行时释放到 rules/）。</summary>
    public static string EmbeddedDefaultJson()
    {
        var asm = typeof(RuleSet).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("dark_rules.json", StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException("程序内未内置默认规则文件");

        using var s = asm.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException("无法读取内置默认规则文件");
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void Compile()
    {
        _compiled.Clear();
        foreach (var p in Patterns)
        {
            if (string.IsNullOrWhiteSpace(p.Match) || string.IsNullOrWhiteSpace(p.Replace)) continue;
            _compiled.Add((new Regex(p.Match, RegexOptions.Compiled), p.Replace, p.Note));
        }
    }

    /// <summary>基于原包条目生成替换计划（所有内容都取自源 APK，不会链式替换）。</summary>
    public ReplacementPlan BuildPlan(ApkArchive apk)
    {
        var plan = new ReplacementPlan { TotalEntries = apk.EntryCount };
        var handled = new HashSet<string>(StringComparer.Ordinal);

        // 1) 精确对照
        foreach (var p in Pairs)
        {
            if (string.IsNullOrWhiteSpace(p.From) || string.IsNullOrWhiteSpace(p.To)) continue;

            if (!apk.Has(p.From))
            {
                plan.Skips.Add(new SkipRecord { From = p.From, To = p.To, Reason = "源文件不在本包中(可能版本不同)，已跳过" });
                continue;
            }
            if (!apk.Has(p.To))
            {
                plan.Skips.Add(new SkipRecord { From = p.From, To = p.To, Reason = "目标(纷争侧)文件不存在，已跳过" });
                continue;
            }
            if (!handled.Add(p.From)) continue;

            plan.Replacements.Add(new Replacement { From = p.From, To = p.To, Kind = "pair", Note = p.Note });
        }

        // 2) 正则族
        var patternTargetMissing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rx, replace, note) in _compiled)
        {
            foreach (var entry in apk.Index.Keys)
            {
                if (handled.Contains(entry)) continue;
                if (!rx.IsMatch(entry)) continue;

                var target = rx.Replace(entry, replace);
                if (string.Equals(target, entry, StringComparison.Ordinal)) continue;

                if (!apk.Has(target))
                {
                    var key = $"{entry} => {target}";
                    if (patternTargetMissing.Add(key))
                        plan.Skips.Add(new SkipRecord { From = entry, To = target, Reason = "目标(纷争侧)文件不存在，已跳过" });
                    continue;
                }

                if (!handled.Add(entry)) continue;
                plan.Replacements.Add(new Replacement { From = entry, To = target, Kind = "pattern", Note = note });
            }
        }

        // 3) 统计 + 未处理的"分侧"资源（供人工补规则）
        foreach (var r in plan.Replacements)
        {
            var e = apk.Get(r.To);
            if (e != null) plan.EstimatedTargetBytes += e.Length;
        }

        foreach (var entry in apk.Index.Keys)
        {
            if (handled.Contains(entry)) continue;
            if (!SideAssetDirs.Any(d => entry.StartsWith(d, StringComparison.Ordinal))) continue;

            var file = entry.Substring(entry.LastIndexOf('/') + 1);
            if (!SideSourceToken.IsMatch(file)) continue;
            if (DarkToken.IsMatch(file)) continue; // 本身就是暗侧文件（作为替换来源）

            plan.UnhandledSideSources.Add(entry);
        }
        plan.UnhandledSideSources.Sort(StringComparer.Ordinal);

        return plan;
    }
}
