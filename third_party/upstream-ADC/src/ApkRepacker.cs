using System.IO.Compression;

namespace ArcaeaDarkApkCreator;

/// <summary>单条替换：把 <see cref="Path"/> 的内容替换为 <see cref="FromEntry"/> 的内容，或直接给定 <see cref="Content"/>。</summary>
internal sealed class RepackItem
{
    public string Path { get; set; } = "";
    public string? FromEntry { get; set; }
    public byte[]? Content { get; set; }
    public string? Note { get; set; }
}

/// <summary>重打包 APK：逐条目复制，替换项写入新内容，保持原压缩方式。</summary>
internal static class ApkRepacker
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>必须保持"不压缩"的条目（Android 高版本要求，改错会导致安装失败）。</summary>
    private static readonly HashSet<string> ForceStored = new(StringComparer.Ordinal)
    {
        "resources.arsc",
    };

    public static void Repack(
        string sourceApk,
        string outputApk,
        IReadOnlyDictionary<string, RepackItem> overrides,
        Action<long, long>? onProgress = null)
    {
        if (File.Exists(outputApk)) File.Delete(outputApk);

        using var srcFs = File.Open(sourceApk, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var src = new ZipArchive(srcFs, ZipArchiveMode.Read);

        long total = 0;
        foreach (var e in src.Entries) total += e.Length;
        long done = 0;

        // 预先把替换来源条目解析出来（避免循环中反复查找）
        var resolved = new Dictionary<string, ZipArchiveEntry?>(StringComparer.Ordinal);
        foreach (var kv in overrides)
        {
            var item = kv.Value;
            if (item.Content != null) { resolved[kv.Key] = null; continue; }
            if (item.FromEntry == null)
                throw new InvalidDataException($"替换项 {kv.Key} 既没有内容也没有来源条目");
            var se = src.GetEntry(item.FromEntry)
                     ?? throw new InvalidDataException($"源 APK 中找不到替换来源条目: {item.FromEntry}");
            resolved[kv.Key] = se;
        }

        using var outFs = File.Create(outputApk);
        using var dst = new ZipArchive(outFs, ZipArchiveMode.Create);

        var buffer = new byte[BufferSize];

        foreach (var entry in src.Entries)
        {
            var name = entry.FullName;

            // 目录条目
            if (name.EndsWith('/'))
            {
                dst.CreateEntry(name);
                continue;
            }

            var originalStored = entry.CompressedLength == entry.Length;
            var mustStore = originalStored || ForceStored.Contains(name);

            ZipArchiveEntry newEntry;
            if (overrides.TryGetValue(name, out var ov))
            {
                newEntry = dst.CreateEntry(name, mustStore ? CompressionLevel.NoCompression : CompressionLevel.Optimal);

                using var os = newEntry.Open();
                if (ov.Content != null)
                {
                    os.Write(ov.Content, 0, ov.Content.Length);
                    done += ov.Content.Length;
                }
                else
                {
                    var se = resolved[name]!;
                    using var ins = se.Open();
                    CopyStream(ins, os, buffer);
                    done += se.Length;
                }
            }
            else
            {
                newEntry = dst.CreateEntry(name, mustStore ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                using var os = newEntry.Open();
                using var ins = entry.Open();
                CopyStream(ins, os, buffer);
                done += entry.Length;
            }

            onProgress?.Invoke(done, total);
        }

        onProgress?.Invoke(total, total);
    }

    private static void CopyStream(Stream src, Stream dst, byte[] buffer)
    {
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            dst.Write(buffer, 0, n);
    }
}
