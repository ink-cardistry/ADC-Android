package com.arcaeadark.tool;

import java.io.ByteArrayInputStream;
import java.io.Closeable;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.zip.CRC32;
import java.util.zip.Deflater;

/**
 * Streaming ZIP writer that emits already-aligned (zipalign) APKs in a single pass.
 *
 * Alignment matters because Android mmaps resources.arsc and uncompressed native libraries;
 * native libs get page alignment, everything uncompressed gets 4-byte alignment. The padding
 * is carried in an extra field (id 0xD935, the id zipalign itself uses), exactly like the
 * official {@code zipalign} tool. The local header is written with placeholders and patched
 * afterwards, so no temp files or data descriptors are needed.
 */
public final class ZipWriter implements Closeable {
    private static final int ALIGN_DEFAULT = 4;
    private static final int ALIGN_PAGE = 16384;

    private final FileChannel ch;
    private long pos;
    private final List<CD> central = new ArrayList<CD>();
    private boolean closed;

    private static final class CD {
        String name;
        int method;
        int dosTime;
        int dosDate;
        int flags;
        long crc;
        long csize;
        long usize;
        long offset;
        long dataStart;
        byte[] extra;
    }

    public ZipWriter(File f) throws IOException {
        RandomAccessFile raf = new RandomAccessFile(f, "rw");
        raf.setLength(0);
        this.ch = raf.getChannel();
        this.pos = 0;
    }

    /** Copy an already-compressed entry verbatim (keeps method, crc and sizes). */
    public void addRaw(String name, int method, int dosTime, int dosDate, int flags,
                       long crc, long size, InputStream raw) throws IOException {
        CD c = begin(name, method, dosTime, dosDate, flags);
        long csize = 0;
        byte[] buf = new byte[1 << 20];
        int r;
        while ((r = raw.read(buf)) > 0) {
            writeBytes(buf, 0, r);
            csize += r;
        }
        finish(c, crc, csize, size);
    }

    /** Add an entry from an uncompressed stream, compressing with the requested method. */
    public void addUncompressed(String name, int method, int dosTime, int dosDate, int flags,
                                InputStream in) throws IOException {
        CD c = begin(name, method, dosTime, dosDate, flags);
        if (method == 0) {
            CRC32 crc = new CRC32();
            long n = 0;
            byte[] buf = new byte[1 << 20];
            int r;
            while ((r = in.read(buf)) > 0) {
                crc.update(buf, 0, r);
                writeBytes(buf, 0, r);
                n += r;
            }
            finish(c, crc.getValue(), n, n);
        } else if (method == 8) {
            CRC32 crc = new CRC32();
            Deflater def = new Deflater(Deflater.BEST_SPEED, true);
            byte[] inBuf = new byte[1 << 20];
            byte[] outBuf = new byte[1 << 20];
            long usize = 0, csize = 0;
            int r;
            while ((r = in.read(inBuf)) > 0) {
                crc.update(inBuf, 0, r);
                usize += r;
                def.setInput(inBuf, 0, r);
                while (!def.needsInput()) {
                    int k = def.deflate(outBuf);
                    if (k > 0) { writeBytes(outBuf, 0, k); csize += k; }
                }
            }
            def.finish();
            while (!def.finished()) {
                int k = def.deflate(outBuf);
                if (k > 0) { writeBytes(outBuf, 0, k); csize += k; }
            }
            def.end();
            finish(c, crc.getValue(), csize, usize);
        } else {
            throw new IOException("unsupported method " + method + " for " + name);
        }
    }

    public void addBytes(String name, int method, int dosTime, int dosDate, int flags, byte[] content)
            throws IOException {
        addUncompressed(name, method, dosTime, dosDate, flags, new ByteArrayInputStream(content));
    }

    private CD begin(String name, int method, int dosTime, int dosDate, int flags) throws IOException {
        byte[] nameBytes = name.getBytes(StandardCharsets.UTF_8);
        CD c = new CD();
        c.name = name;
        c.method = method;
        c.dosTime = dosTime;
        c.dosDate = dosDate;
        c.flags = flags & ~0x0008;
        if (!isAscii(name)) c.flags |= 0x0800;
        c.offset = pos;

        byte[] extra = new byte[0];
        if (method == 0) {
            int align = name.endsWith(".so") ? ALIGN_PAGE : ALIGN_DEFAULT;
            long base = pos + 30 + nameBytes.length;
            int need = (int) ((align - ((base + 4) % align)) % align);
            // cap padding so the 16-bit extra length stays valid
            if (need > 65000) need = (int) ((align - ((base + 4) % align)) % align);
            int extraLen = 4 + need;
            extra = new byte[extraLen];
            Bytes.w16(extra, 0, 0xD935);
            Bytes.w16(extra, 2, extraLen - 4);
        }
        c.extra = extra;

        ByteBuffer hb = ByteBuffer.allocate(30);
        putU32(hb, 0x04034b50L);
        putU16(hb, method == 0 ? 10 : 20);
        putU16(hb, c.flags);
        putU16(hb, method);
        putU16(hb, dosTime);
        putU16(hb, dosDate);
        putU32(hb, 0);
        putU32(hb, 0);
        putU32(hb, 0);
        putU16(hb, nameBytes.length);
        putU16(hb, extra.length);
        writeBytes(hb.array(), 0, 30);
        writeBytes(nameBytes, 0, nameBytes.length);
        if (extra.length > 0) writeBytes(extra, 0, extra.length);
        c.dataStart = pos;
        return c;
    }

    private void finish(CD c, long crc, long csize, long usize) throws IOException {
        byte[] patch = new byte[12];
        Bytes.w32(patch, 0, crc);
        Bytes.w32(patch, 4, csize);
        Bytes.w32(patch, 8, usize);
        writeAt(c.offset + 14, patch, 0, 12);
        c.crc = crc;
        c.csize = csize;
        c.usize = usize;
        central.add(c);
    }

    @Override
    public void close() throws IOException {
        if (closed) return;
        closed = true;

        long cdStart = pos;
        for (CD c : central) {
            if (c.offset > 0xFFFFFFFFL || c.csize > 0xFFFFFFFFL || c.usize > 0xFFFFFFFFL) {
                throw new IOException("entry too large for non-Zip64 archive: " + c.name);
            }
            byte[] nameBytes = c.name.getBytes(StandardCharsets.UTF_8);
            ByteBuffer hb = ByteBuffer.allocate(46);
            putU32(hb, 0x02014b50L);
            putU16(hb, 20);                       // version made by
            putU16(hb, c.method == 0 ? 10 : 20);  // version needed
            putU16(hb, c.flags);
            putU16(hb, c.method);
            putU16(hb, c.dosTime);
            putU16(hb, c.dosDate);
            putU32(hb, c.crc);
            putU32(hb, c.csize);
            putU32(hb, c.usize);
            putU16(hb, nameBytes.length);
            putU16(hb, c.extra.length);
            putU16(hb, 0);                        // comment length
            putU16(hb, 0);                        // disk number start
            putU16(hb, 0);                        // internal attributes
            putU32(hb, 0);                        // external attributes
            putU32(hb, c.offset);
            writeBytes(hb.array(), 0, 46);
            writeBytes(nameBytes, 0, nameBytes.length);
            if (c.extra.length > 0) writeBytes(c.extra, 0, c.extra.length);
        }
        long cdSize = pos - cdStart;

        int count = central.size();
        if (count > 0xFFFF) throw new IOException("too many entries for non-Zip64 archive: " + count);
        if (cdStart > 0xFFFFFFFFL) throw new IOException("archive too large for non-Zip64 output");

        ByteBuffer eo = ByteBuffer.allocate(22);
        putU32(eo, 0x06054b50L);
        putU16(eo, 0);
        putU16(eo, 0);
        putU16(eo, count);
        putU16(eo, count);
        putU32(eo, cdSize);
        putU32(eo, cdStart);
        putU16(eo, 0);
        writeBytes(eo.array(), 0, 22);

        ch.force(true);
        ch.close();
    }

    private void writeBytes(byte[] b, int off, int len) throws IOException {
        writeAt(pos, b, off, len);
        pos += len;
    }

    private void writeAt(long p, byte[] b, int off, int len) throws IOException {
        ByteBuffer bb = ByteBuffer.wrap(b, off, len);
        long q = p;
        while (bb.hasRemaining()) q += ch.write(bb, q);
    }

    private static boolean isAscii(String s) {
        for (int i = 0; i < s.length(); i++) if (s.charAt(i) > 127) return false;
        return true;
    }

    private static void putU16(ByteBuffer b, int v) { b.put((byte) (v & 0xFF)); b.put((byte) ((v >>> 8) & 0xFF)); }

    private static void putU32(ByteBuffer b, long v) {
        b.put((byte) (v & 0xFF));
        b.put((byte) ((v >>> 8) & 0xFF));
        b.put((byte) ((v >>> 16) & 0xFF));
        b.put((byte) ((v >>> 24) & 0xFF));
    }
}
