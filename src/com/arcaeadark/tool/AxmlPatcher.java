package com.arcaeadark.tool;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Patches the package name inside a binary AndroidManifest.xml (AXML).
 *
 * AXML = XML root chunk -> string pool -> resource map -> nodes. Every node refers to
 * strings by *string-pool index*, so there are no cross-section absolute offsets. We only
 * rewrite the string pool (substring-replacing the old package name), then concatenate
 * "root header + new pool + bytes after the old pool" and fix the root chunk size.
 */
public final class AxmlPatcher {
    private static final int CHUNK_STRING_POOL = 0x0001;
    private static final int CHUNK_XML = 0x0003;
    private static final int CHUNK_START_ELEMENT = 0x0102;
    private static final long FLAG_SORTED = 0x00000001L;
    private static final long FLAG_UTF8 = 0x00000100L;
    private static final int TYPE_STRING = 0x03;
    private static final int TYPE_INT_DEC = 0x10;
    private static final int TYPE_INT_HEX = 0x11;

    private AxmlPatcher() {}

    public static final class ManifestInfo {
        public final String pkg;
        public final String versionName;
        public final long versionCode;
        public ManifestInfo(String pkg, String versionName, long versionCode) {
            this.pkg = pkg;
            this.versionName = versionName;
            this.versionCode = versionCode;
        }
    }

    public static final class Result {
        public final boolean changed;
        public final int replacementCount;
        public final String message;
        public final byte[] data;
        public Result(boolean changed, int replacementCount, String message, byte[] data) {
            this.changed = changed; this.replacementCount = replacementCount;
            this.message = message; this.data = data;
        }
    }

    public static ManifestInfo readManifestInfo(byte[] axml) {
        if (axml.length < 12 || Bytes.u16(axml, 0) != CHUNK_XML) return null;

        int spOff = 8;
        if (Bytes.u16(axml, spOff) != CHUNK_STRING_POOL) return null;

        long spSize = Bytes.u32(axml, spOff + 4);
        long stringCount = Bytes.u32(axml, spOff + 8);
        long flags = Bytes.u32(axml, spOff + 16);
        long stringsStart = Bytes.u32(axml, spOff + 20);
        if (spOff + spSize > axml.length) return null;

        boolean utf8 = (flags & FLAG_UTF8) != 0;
        String[] strings = new String[(int) stringCount];
        int offsetsBase = spOff + 28;
        int dataBase = spOff + (int) stringsStart;
        for (int i = 0; i < stringCount; i++) {
            long off = Bytes.u32(axml, offsetsBase + i * 4);
            strings[i] = utf8 ? decodeUtf8(axml, dataBase + (int) off) : decodeUtf16(axml, dataBase + (int) off);
        }

        String pkg = null;
        String versionName = null;
        long versionCode = -1;

        int pos = spOff + (int) spSize;
        while (pos + 8 <= axml.length) {
            int type = Bytes.u16(axml, pos);
            int headerSize = Bytes.u16(axml, pos + 2);
            long size = Bytes.u32(axml, pos + 4);
            if (size < 8 || pos + size > axml.length) break;

            if (type == CHUNK_START_ELEMENT) {
                long nameIdx = Bytes.u32(axml, pos + headerSize + 4);
                if (nameIdx >= stringCount) break;
                if ("manifest".equals(strings[(int) nameIdx])) {
                    int attrStart = Bytes.u16(axml, pos + headerSize + 8);
                    int attrSize = Bytes.u16(axml, pos + headerSize + 10);
                    int attrCount = Bytes.u16(axml, pos + headerSize + 12);
                    for (int i = 0; i < attrCount; i++) {
                        int ao = pos + headerSize + attrStart + i * attrSize;
                        if (ao + 20 > axml.length) break;

                        long an = Bytes.u32(axml, ao + 4);
                        long raw = Bytes.u32(axml, ao + 8);
                        int dataType = axml[ao + 15] & 0xFF;
                        long data = Bytes.u32(axml, ao + 16);
                        if (an >= stringCount) continue;

                        String attr = strings[(int) an];
                        if ("package".equals(attr)) {
                            pkg = (dataType == TYPE_STRING && data < stringCount) ? strings[(int) data] : null;
                        } else if ("versionName".equals(attr)) {
                            if (dataType == TYPE_STRING && data < stringCount) versionName = strings[(int) data];
                            else if (raw != 0xFFFFFFFFL && raw < stringCount) versionName = strings[(int) raw];
                        } else if ("versionCode".equals(attr)) {
                            if (dataType == TYPE_INT_DEC || dataType == TYPE_INT_HEX) versionCode = data;
                        }
                    }
                    break;
                }
            }
            pos += (int) size;
        }

        if (pkg == null) return null;
        return new ManifestInfo(pkg, versionName, versionCode);
    }

    public static Result replacePackageName(byte[] axml, String oldName, String newName) {
        if (axml.length < 12) throw new IllegalArgumentException("AndroidManifest.xml too small to be valid AXML");
        if (Bytes.u16(axml, 0) != CHUNK_XML) {
            throw new IllegalArgumentException(String.format(
                    "AndroidManifest.xml is not binary AXML (root type=0x%04X)", Bytes.u16(axml, 0)));
        }

        int spOff = 8;
        if (Bytes.u16(axml, spOff) != CHUNK_STRING_POOL) {
            throw new IllegalArgumentException("no string pool at expected position in AXML");
        }

        int spHeaderSize = Bytes.u16(axml, spOff + 2);
        long spSize = Bytes.u32(axml, spOff + 4);
        long stringCount = Bytes.u32(axml, spOff + 8);
        long styleCount = Bytes.u32(axml, spOff + 12);
        long flags = Bytes.u32(axml, spOff + 16);
        long stringsStart = Bytes.u32(axml, spOff + 20);
        long stylesStart = Bytes.u32(axml, spOff + 24);

        if (spHeaderSize != 28) throw new IllegalArgumentException("unexpected string pool headerSize: " + spHeaderSize);
        if (spOff + spSize > axml.length) throw new IllegalArgumentException("string pool out of bounds, AXML corrupt");

        boolean utf8 = (flags & FLAG_UTF8) != 0;

        String[] strings = new String[(int) stringCount];
        int offsetsBase = spOff + 28;
        int dataBase = spOff + (int) stringsStart;
        for (int i = 0; i < stringCount; i++) {
            long off = Bytes.u32(axml, offsetsBase + i * 4);
            int p = dataBase + (int) off;
            strings[i] = utf8 ? decodeUtf8(axml, p) : decodeUtf16(axml, p);
        }

        int replaced = 0;
        for (int i = 0; i < strings.length; i++) {
            String s = strings[i];
            if (s.length() == 0) continue;
            if (s.contains(oldName)) {
                strings[i] = s.replace(oldName, newName);
                replaced++;
            }
        }

        if (replaced == 0) {
            return new Result(false, 0,
                    "AndroidManifest.xml does not contain package name " + oldName + " (unchanged)", axml);
        }

        int newStringsStart = 28 + (int) stringCount * 4 + (int) styleCount * 4;

        ByteArrayOutputStream data = new ByteArrayOutputStream(1024);
        long[] newOffsets = new long[(int) stringCount];
        for (int i = 0; i < strings.length; i++) {
            newOffsets[i] = data.size();
            if (utf8) encodeUtf8(data, strings[i]);
            else encodeUtf16(data, strings[i]);
        }
        while (data.size() % 4 != 0) data.write(0);

        byte[] styles = new byte[0];
        int newStylesStart = 0;
        if (styleCount > 0 && stylesStart > 0) {
            int stLen = (int) (spSize - stylesStart);
            styles = new byte[stLen];
            System.arraycopy(axml, spOff + (int) stylesStart, styles, 0, stLen);
            newStylesStart = newStringsStart + data.size();
        }

        int newSpSize = newStringsStart + data.size() + styles.length;

        byte[] newSp = new byte[newSpSize];
        int w = 0;
        Bytes.w16(newSp, w, CHUNK_STRING_POOL); w += 2;
        Bytes.w16(newSp, w, 28); w += 2;
        Bytes.w32(newSp, w, newSpSize); w += 4;
        Bytes.w32(newSp, w, stringCount); w += 4;
        Bytes.w32(newSp, w, styleCount); w += 4;
        Bytes.w32(newSp, w, flags & ~FLAG_SORTED); w += 4;
        Bytes.w32(newSp, w, newStringsStart); w += 4;
        Bytes.w32(newSp, w, newStylesStart); w += 4;

        for (int i = 0; i < stringCount; i++) { Bytes.w32(newSp, w, newOffsets[i]); w += 4; }
        for (int i = 0; i < styleCount; i++) {
            Bytes.w32(newSp, w, Bytes.u32(axml, offsetsBase + (int) stringCount * 4 + i * 4));
            w += 4;
        }

        if (w != newStringsStart) throw new IllegalArgumentException("string pool header length mismatch: " + w + " != " + newStringsStart);

        System.arraycopy(data.toByteArray(), 0, newSp, w, data.size());
        w += data.size();
        if (styles.length > 0) {
            System.arraycopy(styles, 0, newSp, w, styles.length);
            w += styles.length;
        }

        int tailStart = spOff + (int) spSize;
        int tailLen = axml.length - tailStart;
        byte[] output = new byte[spOff + newSpSize + tailLen];

        System.arraycopy(axml, 0, output, 0, spOff);
        System.arraycopy(newSp, 0, output, spOff, newSpSize);
        if (tailLen > 0) System.arraycopy(axml, tailStart, output, spOff + newSpSize, tailLen);

        Bytes.w32(output, 4, output.length);

        return new Result(true, replaced, "AndroidManifest.xml package name replaced at " + replaced
                + " string(s): " + oldName + " -> " + newName
                + " (" + axml.length + " -> " + output.length + " bytes)", output);
    }

    private static String decodeUtf8(byte[] b, int off) {
        int len = b[off++] & 0xFF;
        if ((len & 0x80) != 0) len = ((len & 0x7F) << 8) | (b[off++] & 0xFF);
        int len2 = b[off++] & 0xFF;
        if ((len2 & 0x80) != 0) len2 = ((len2 & 0x7F) << 8) | (b[off++] & 0xFF);
        if (len2 == 0) return "";
        return new String(b, off, len2, StandardCharsets.UTF_8);
    }

    private static String decodeUtf16(byte[] b, int off) {
        int len = Bytes.u16(b, off);
        off += 2;
        if ((len & 0x8000) != 0) {
            len = ((len & 0x7FFF) << 16) | Bytes.u16(b, off);
            off += 2;
        }
        if (len == 0) return "";
        return new String(b, off, len * 2, StandardCharsets.UTF_16LE);
    }

    private static void encodeUtf8(ByteArrayOutputStream out, String s) {
        byte[] utf8 = s.getBytes(StandardCharsets.UTF_8);
        writeLen8(out, s.length());
        writeLen8(out, utf8.length);
        out.write(utf8, 0, utf8.length);
        out.write(0);
    }

    private static void writeLen8(ByteArrayOutputStream out, int len) {
        if (len > 0x7F) {
            out.write(((len >> 8) | 0x80) & 0xFF);
            out.write(len & 0xFF);
        } else {
            out.write(len & 0xFF);
        }
    }

    private static void encodeUtf16(ByteArrayOutputStream out, String s) {
        int len = s.length();
        if (len > 0x7FFF) {
            Bytes.addU16(out, (len >> 16) | 0x8000);
            Bytes.addU16(out, len & 0xFFFF);
        } else {
            Bytes.addU16(out, len);
        }
        byte[] bytes = s.getBytes(StandardCharsets.UTF_16LE);
        out.write(bytes, 0, bytes.length);
        out.write(0);
        out.write(0);
    }
}
