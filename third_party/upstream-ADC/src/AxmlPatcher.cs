using System.Text;

namespace ArcaeaDarkApkCreator;

/// <summary>
/// 修改二进制 AndroidManifest.xml（AXML）中的包名。
///
/// 原理：AXML = XML根块 → 字符串池 → 资源映射 → 节点。节点全部用"字符串池下标"引用字符串，
/// 不存在跨区绝对偏移。因此只要重写字符串池（把包含旧包名的字符串做子串替换），
/// 再把"根块头 + 新字符串池 + 原字符串池之后的字节"拼接并修正根块 size 即可，无需改动任何节点。
/// </summary>
internal static class AxmlPatcher
{
    private const ushort ChunkStringPool = 0x0001;
    private const ushort ChunkXml = 0x0003;
    private const ushort ChunkStartElement = 0x0102;
    private const uint FlagSorted = 0x00000001;
    private const uint FlagUtf8 = 0x00000100;

    private const byte TypeString = 0x03;
    private const byte TypeIntDec = 0x10;
    private const byte TypeIntHex = 0x11;

    /// <summary>从 APK 的 AndroidManifest.xml 中读出的基本信息。</summary>
    public sealed record ManifestInfo(string Package, string? VersionName, long VersionCode);

    public sealed record Result(bool Changed, int ReplacementCount, string Message);

    /// <summary>读取 AndroidManifest.xml 中的 package / versionName / versionCode（读不到返回 null）。</summary>
    public static ManifestInfo? ReadManifestInfo(byte[] axml)
    {
        if (axml.Length < 12 || U16(axml, 0) != ChunkXml) return null;

        int spOff = 8;
        if (U16(axml, spOff) != ChunkStringPool) return null;

        uint spSize = U32(axml, spOff + 4);
        uint stringCount = U32(axml, spOff + 8);
        uint flags = U32(axml, spOff + 16);
        uint stringsStart = U32(axml, spOff + 20);
        if (spOff + spSize > axml.Length) return null;

        bool utf8 = (flags & FlagUtf8) != 0;
        var strings = new string[stringCount];
        int offsetsBase = spOff + 28;
        int dataBase = spOff + (int)stringsStart;
        for (uint i = 0; i < stringCount; i++)
        {
            uint off = U32(axml, offsetsBase + (int)(i * 4));
            strings[i] = utf8 ? DecodeUtf8(axml, dataBase + (int)off) : DecodeUtf16(axml, dataBase + (int)off);
        }

        string? package = null;
        string? versionName = null;
        long versionCode = -1;

        int pos = spOff + (int)spSize;
        while (pos + 8 <= axml.Length)
        {
            ushort type = U16(axml, pos);
            ushort headerSize = U16(axml, pos + 2);
            uint size = U32(axml, pos + 4);
            if (size < 8 || pos + size > axml.Length) break;

            if (type == ChunkStartElement)
            {
                uint nameIdx = U32(axml, pos + headerSize + 4);
                if (nameIdx >= stringCount) break;
                if (strings[nameIdx] == "manifest")
                {
                    int attrStart = U16(axml, pos + headerSize + 8);
                    int attrSize = U16(axml, pos + headerSize + 10);
                    int attrCount = U16(axml, pos + headerSize + 12);
                    for (int i = 0; i < attrCount; i++)
                    {
                        int ao = pos + headerSize + attrStart + i * attrSize;
                        if (ao + 20 > axml.Length) break;

                        uint an = U32(axml, ao + 4);
                        uint raw = U32(axml, ao + 8);
                        byte dataType = axml[ao + 15];
                        uint data = U32(axml, ao + 16);
                        if (an >= stringCount) continue;

                        switch (strings[an])
                        {
                            case "package":
                                package = dataType == TypeString && data < stringCount ? strings[data] : null;
                                break;
                            case "versionName":
                                if (dataType == TypeString && data < stringCount) versionName = strings[data];
                                else if (raw != 0xFFFFFFFF && raw < stringCount) versionName = strings[raw];
                                break;
                            case "versionCode":
                                if (dataType is TypeIntDec or TypeIntHex) versionCode = data;
                                break;
                        }
                    }
                    break;
                }
            }
            pos += (int)size;
        }

        if (package == null) return null;
        return new ManifestInfo(package, versionName, versionCode);
    }

    public static byte[] ReplacePackageName(byte[] axml, string oldName, string newName, out Result result)
    {
        if (axml.Length < 12)
            throw new InvalidDataException("AndroidManifest.xml 过小，不是有效 AXML");

        if (U16(axml, 0) != ChunkXml)
            throw new InvalidDataException($"AndroidManifest.xml 不是二进制 AXML(根块类型=0x{U16(axml, 0):X4})");

        int spOff = 8; // 根块 headerSize 固定 8
        if (U16(axml, spOff) != ChunkStringPool)
            throw new InvalidDataException("AXML 中未在预期位置找到字符串池");

        ushort spHeaderSize = U16(axml, spOff + 2);
        uint spSize = U32(axml, spOff + 4);
        uint stringCount = U32(axml, spOff + 8);
        uint styleCount = U32(axml, spOff + 12);
        uint flags = U32(axml, spOff + 16);
        uint stringsStart = U32(axml, spOff + 20);
        uint stylesStart = U32(axml, spOff + 24);

        if (spHeaderSize != 28)
            throw new InvalidDataException($"字符串池 headerSize 异常: {spHeaderSize}（期望 28）");
        if (spOff + spSize > axml.Length)
            throw new InvalidDataException("字符串池长度越界，AXML 可能已损坏");

        bool utf8 = (flags & FlagUtf8) != 0;

        // ---- 解析字符串 ----
        var strings = new string[stringCount];
        int offsetsBase = spOff + 28;
        int dataBase = spOff + (int)stringsStart;

        for (uint i = 0; i < stringCount; i++)
        {
            uint off = U32(axml, offsetsBase + (int)(i * 4));
            int p = dataBase + (int)off;
            strings[i] = utf8 ? DecodeUtf8(axml, p) : DecodeUtf16(axml, p);
        }

        // ---- 替换包名 ----
        int replaced = 0;
        for (int i = 0; i < strings.Length; i++)
        {
            var s = strings[i];
            if (s.Length == 0) continue;
            if (s.Contains(oldName, StringComparison.Ordinal))
            {
                strings[i] = s.Replace(oldName, newName, StringComparison.Ordinal);
                replaced++;
            }
        }

        if (replaced == 0)
        {
            result = new Result(false, 0, $"AndroidManifest.xml 中未出现包名 {oldName}（未修改）");
            return axml;
        }

        // ---- 重建字符串池 ----
        int newStringsStart = 28 + (int)stringCount * 4 + (int)styleCount * 4;

        var data = new List<byte>(1024);
        var newOffsets = new uint[stringCount];
        for (int i = 0; i < strings.Length; i++)
        {
            newOffsets[i] = (uint)data.Count;
            if (utf8) EncodeUtf8(data, strings[i]);
            else EncodeUtf16(data, strings[i]);
        }
        while (data.Count % 4 != 0) data.Add(0);

        // 原 styles 区域（通常 styleCount=0）
        byte[] styles = Array.Empty<byte>();
        int newStylesStart = 0;
        if (styleCount > 0 && stylesStart > 0)
        {
            int stLen = (int)(spSize - stylesStart);
            styles = new byte[stLen];
            Array.Copy(axml, spOff + (int)stylesStart, styles, 0, stLen);
            newStylesStart = newStringsStart + data.Count;
        }

        int newSpSize = newStringsStart + data.Count + styles.Length;

        var newSp = new byte[newSpSize];
        int w = 0;
        W16(newSp, ref w, ChunkStringPool);
        W16(newSp, ref w, 28);
        W32(newSp, ref w, (uint)newSpSize);
        W32(newSp, ref w, stringCount);
        W32(newSp, ref w, styleCount);
        W32(newSp, ref w, flags & ~FlagSorted); // 去掉 SORTED 标记（顺序可能已被破坏）
        W32(newSp, ref w, (uint)newStringsStart);
        W32(newSp, ref w, (uint)newStylesStart);

        for (int i = 0; i < stringCount; i++) W32(newSp, ref w, newOffsets[i]);
        for (uint i = 0; i < styleCount; i++)
            W32(newSp, ref w, U32(axml, offsetsBase + (int)(stringCount * 4 + i * 4)));

        // w 现在应等于 newStringsStart
        if (w != newStringsStart)
            throw new InvalidDataException($"字符串池头部长度计算异常: {w} != {newStringsStart}");

        data.CopyTo(newSp, w);
        w += data.Count;
        if (styles.Length > 0)
        {
            Array.Copy(styles, 0, newSp, w, styles.Length);
            w += styles.Length;
        }

        // ---- 拼接 ----
        int tailStart = spOff + (int)spSize;
        int tailLen = axml.Length - tailStart;
        var output = new byte[spOff + newSpSize + tailLen];

        Array.Copy(axml, 0, output, 0, spOff);
        Array.Copy(newSp, 0, output, spOff, newSpSize);
        if (tailLen > 0) Array.Copy(axml, tailStart, output, spOff + newSpSize, tailLen);

        // 修正根块 size
        WriteU32(output, 4, (uint)output.Length);

        result = new Result(true, replaced,
            $"AndroidManifest.xml 已替换 {replaced} 处包名: {oldName} -> {newName}（{axml.Length} -> {output.Length} 字节）");
        return output;
    }

    // ---------------- 编解码 ----------------

    private static string DecodeUtf8(byte[] b, int off)
    {
        int len = b[off++];
        if ((len & 0x80) != 0) len = ((len & 0x7F) << 8) | b[off++];
        int len2 = b[off++];
        if ((len2 & 0x80) != 0) len2 = ((len2 & 0x7F) << 8) | b[off++];
        if (len2 == 0) return string.Empty;
        return Encoding.UTF8.GetString(b, off, len2);
    }

    private static string DecodeUtf16(byte[] b, int off)
    {
        int len = U16(b, off);
        off += 2;
        if ((len & 0x8000) != 0)
        {
            len = ((len & 0x7FFF) << 16) | U16(b, off);
            off += 2;
        }
        if (len == 0) return string.Empty;
        return Encoding.Unicode.GetString(b, off, len * 2);
    }

    private static void EncodeUtf8(List<byte> outb, string s)
    {
        var utf8 = Encoding.UTF8.GetBytes(s);
        WriteLen8(outb, s.Length);
        WriteLen8(outb, utf8.Length);
        outb.AddRange(utf8);
        outb.Add(0);
    }

    private static void WriteLen8(List<byte> outb, int len)
    {
        if (len > 0x7F)
        {
            outb.Add((byte)((len >> 8) | 0x80));
            outb.Add((byte)(len & 0xFF));
        }
        else
        {
            outb.Add((byte)len);
        }
    }

    private static void EncodeUtf16(List<byte> outb, string s)
    {
        int len = s.Length;
        if (len > 0x7FFF)
        {
            // 两个 u16：高16位(带0x8000标记) + 低16位
            AddU16(outb, (ushort)((len >> 16) | 0x8000));
            AddU16(outb, (ushort)(len & 0xFFFF));
        }
        else
        {
            AddU16(outb, (ushort)len);
        }
        var bytes = Encoding.Unicode.GetBytes(s);
        outb.AddRange(bytes);
        outb.Add(0);
        outb.Add(0);
    }

    // ---------------- 字节读写 ----------------

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    private static uint U32(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void W16(byte[] b, ref int o, ushort v)
    {
        b[o++] = (byte)(v & 0xFF);
        b[o++] = (byte)((v >> 8) & 0xFF);
    }

    private static void AddU16(List<byte> b, ushort v)
    {
        b.Add((byte)(v & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
    }

    private static void W32(byte[] b, ref int o, uint v)
    {
        b[o++] = (byte)(v & 0xFF);
        b[o++] = (byte)((v >> 8) & 0xFF);
        b[o++] = (byte)((v >> 16) & 0xFF);
        b[o++] = (byte)((v >> 24) & 0xFF);
    }
}
