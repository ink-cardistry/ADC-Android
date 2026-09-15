using System.IO.Compression;

namespace ArcaeaDarkApkCreator;

/// <summary>源 APK 的只读封装（保留条目索引，支持按名取内容）。</summary>
internal sealed class ApkArchive : IDisposable
{
    private readonly FileStream _fs;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _index;
    private long _totalUncompressed = -1;

    public string Path { get; }
    public IReadOnlyDictionary<string, ZipArchiveEntry> Index => _index;
    public int EntryCount => _index.Count;

    public ApkArchive(string path)
    {
        Path = path;
        _fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _zip = new ZipArchive(_fs, ZipArchiveMode.Read);
        _index = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            _index[e.FullName] = e;
        }
    }

    public bool Has(string name) => _index.ContainsKey(name);

    public ZipArchiveEntry? Get(string name)
        => _index.TryGetValue(name, out var e) ? e : null;

    public byte[] ReadAllBytes(string name)
    {
        var e = Get(name) ?? throw new FileNotFoundException($"APK 中不存在条目: {name}");
        using var s = e.Open();
        using var ms = new MemoryStream(e.Length > 0 && e.Length < int.MaxValue ? (int)e.Length : 0);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public long TotalUncompressedBytes()
    {
        if (_totalUncompressed >= 0) return _totalUncompressed;
        long sum = 0;
        foreach (var e in _zip.Entries) sum += e.Length;
        _totalUncompressed = sum;
        return sum;
    }

    public IEnumerable<ZipArchiveEntry> Entries => _zip.Entries;

    /// <summary>导出完整条目清单到文本（供人工核对规则）。</summary>
    public void DumpEntries(string outPath)
    {
        using var w = new StreamWriter(outPath, false);
        foreach (var e in _zip.Entries.OrderBy(x => x.FullName, StringComparer.Ordinal))
            w.WriteLine(e.FullName);
    }

    public void Dispose()
    {
        _zip.Dispose();
        _fs.Dispose();
    }
}
