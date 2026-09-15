using System.Text;

namespace ArcaeaDarkApkCreator;

/// <summary>
/// 修改 resources.arsc 中的包名。
///
/// 原理：ARSC 里应用包名不在字符串池中，而是 ResTable_package 块内一个
/// **定长 char16_t name[128]（256 字节）内联字段**。
/// 因此只需定位包名等于旧包名的 0x0200 块，把该 256 字节字段重写为新包名 + \0 填充。
/// 长度变化不影响块大小（新包名 ≤127 字符即可），没有任何偏移需要修正。
/// </summary>
internal static class ArscPatcher
{
    private const ushort ChunkTable = 0x0002;
    private const ushort ChunkPackage = 0x0200;

    private const int PackageNameOffset = 12; // header(8) + id(4)
    private const int PackageNameBytes = 256; // char16_t name[128]

    public sealed record Result(bool Changed, string Message);

    public static byte[] ReplacePackageName(byte[] arsc, string oldName, string newName, out Result result)
    {
        if (arsc.Length < 12)
            throw new InvalidDataException("resources.arsc 过小");

        if (U16(arsc, 0) != ChunkTable)
            throw new InvalidDataException($"resources.arsc 头部类型异常: 0x{U16(arsc, 0):X4}");

        if (newName.Length > 127)
        {
            result = new Result(false, $"新包名过长（{newName.Length} > 127），已跳过 resources.arsc");
            return arsc;
        }

        int tableHeaderSize = U16(arsc, 2);
        int pos = tableHeaderSize;
        while (pos + 8 <= arsc.Length)
        {
            ushort type = U16(arsc, pos);
            uint size = U32(arsc, pos + 4);
            if (size < 8 || pos + size > arsc.Length) break;

            if (type == ChunkPackage)
            {
                if (pos + PackageNameOffset + PackageNameBytes <= arsc.Length)
                {
                    var name = Encoding.Unicode.GetString(arsc, pos + PackageNameOffset, PackageNameBytes)
                        .TrimEnd('\0');
                    if (string.Equals(name, oldName, StringComparison.Ordinal))
                    {
                        var copy = (byte[])arsc.Clone();
                        var buf = new byte[PackageNameBytes];
                        var nb = Encoding.Unicode.GetBytes(newName);
                        Array.Copy(nb, 0, buf, 0, nb.Length);
                        Array.Copy(buf, 0, copy, pos + PackageNameOffset, PackageNameBytes);
                        result = new Result(true,
                            $"resources.arsc 包名已替换: {oldName} -> {newName}");
                        return copy;
                    }
                }
            }
            pos += (int)size;
        }

        result = new Result(false, $"resources.arsc 中未找到包名 {oldName} 对应的 package 块（已跳过）");
        return arsc;
    }

    /// <summary>列出 ARSC 中所有 package 块的名字（诊断用）。</summary>
    public static List<string> ListPackages(byte[] arsc)
    {
        var list = new List<string>();
        if (arsc.Length < 12 || U16(arsc, 0) != ChunkTable) return list;

        int pos = U16(arsc, 2);
        while (pos + 8 <= arsc.Length)
        {
            ushort type = U16(arsc, pos);
            uint size = U32(arsc, pos + 4);
            if (size < 8 || pos + size > arsc.Length) break;
            if (type == ChunkPackage && pos + PackageNameOffset + PackageNameBytes <= arsc.Length)
            {
                list.Add(Encoding.Unicode.GetString(arsc, pos + PackageNameOffset, PackageNameBytes).TrimEnd('\0'));
            }
            pos += (int)size;
        }
        return list;
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    private static uint U32(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}
