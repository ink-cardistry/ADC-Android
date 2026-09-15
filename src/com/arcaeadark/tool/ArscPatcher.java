package com.arcaeadark.tool;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * Patches the app package name inside resources.arsc.
 *
 * ARSC stores the package name not in a string pool but as a fixed-length
 * char16_t name[128] (256 bytes) inline field inside a ResTable_package block (0x0200).
 * So we locate the block whose name equals the old package and rewrite those 256 bytes.
 * The length never changes, so no offsets need adjusting.
 */
public final class ArscPatcher {
    private static final int CHUNK_TABLE = 0x0002;
    private static final int CHUNK_PACKAGE = 0x0200;
    private static final int PACKAGE_NAME_OFFSET = 12; // header(8) + id(4)
    private static final int PACKAGE_NAME_BYTES = 256; // char16_t name[128]

    private ArscPatcher() {}

    public static final class Result {
        public final boolean changed;
        public final String message;
        public final byte[] data;
        public Result(boolean changed, String message, byte[] data) {
            this.changed = changed; this.message = message; this.data = data;
        }
    }

    public static Result replacePackageName(byte[] arsc, String oldName, String newName) {
        if (arsc.length < 12) throw new IllegalArgumentException("resources.arsc too small");
        if (Bytes.u16(arsc, 0) != CHUNK_TABLE) {
            throw new IllegalArgumentException(String.format(
                    "resources.arsc bad chunk type: 0x%04X", Bytes.u16(arsc, 0)));
        }
        if (newName.length() > 127) {
            return new Result(false, "new package name too long (" + newName.length() + " > 127), resources.arsc skipped", arsc);
        }

        int tableHeaderSize = Bytes.u16(arsc, 2);
        int pos = tableHeaderSize;
        while (pos + 8 <= arsc.length) {
            int type = Bytes.u16(arsc, pos);
            long size = Bytes.u32(arsc, pos + 4);
            if (size < 8 || pos + size > arsc.length) break;

            if (type == CHUNK_PACKAGE) {
                if (pos + PACKAGE_NAME_OFFSET + PACKAGE_NAME_BYTES <= arsc.length) {
                    String name = new String(arsc, pos + PACKAGE_NAME_OFFSET, PACKAGE_NAME_BYTES, StandardCharsets.UTF_16LE);
                    int end = name.indexOf('\0');
                    if (end >= 0) name = name.substring(0, end);
                    if (name.equals(oldName)) {
                        byte[] copy = arsc.clone();
                        byte[] buf = new byte[PACKAGE_NAME_BYTES];
                        byte[] nb = newName.getBytes(StandardCharsets.UTF_16LE);
                        System.arraycopy(nb, 0, buf, 0, Math.min(nb.length, buf.length));
                        System.arraycopy(buf, 0, copy, pos + PACKAGE_NAME_OFFSET, PACKAGE_NAME_BYTES);
                        return new Result(true, "resources.arsc package name replaced: " + oldName + " -> " + newName, copy);
                    }
                }
            }
            pos += (int) size;
        }

        return new Result(false, "resources.arsc: no package block named " + oldName + " (skipped)", arsc);
    }

    public static List<String> listPackages(byte[] arsc) {
        List<String> list = new ArrayList<String>();
        if (arsc.length < 12 || Bytes.u16(arsc, 0) != CHUNK_TABLE) return list;

        int pos = Bytes.u16(arsc, 2);
        while (pos + 8 <= arsc.length) {
            int type = Bytes.u16(arsc, pos);
            long size = Bytes.u32(arsc, pos + 4);
            if (size < 8 || pos + size > arsc.length) break;
            if (type == CHUNK_PACKAGE && pos + PACKAGE_NAME_OFFSET + PACKAGE_NAME_BYTES <= arsc.length) {
                String name = new String(arsc, pos + PACKAGE_NAME_OFFSET, PACKAGE_NAME_BYTES, StandardCharsets.UTF_16LE);
                int end = name.indexOf('\0');
                if (end >= 0) name = name.substring(0, end);
                list.add(name);
            }
            pos += (int) size;
        }
        return list;
    }
}
