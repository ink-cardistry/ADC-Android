package com.arcaeadark.tool;

import java.io.Closeable;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.zip.Inflater;
import java.util.zip.InflaterInputStream;

/**
 * Random-access ZIP reader over a {@link FileChannel}.
 *
 * Java's ZipFile only accepts a File, but a manually imported (SAF) 2 GB APK should not be
 * copied into app storage. A channel is seekable and lets us both stream the ~2 GB from the
 * original document and serve entries in arbitrary order.
 */
public final class ZipReader implements Closeable {
    private static final long SIG_EOCD = 0x06054b50L;
    private static final long SIG_ZIP64_EOCD = 0x06064b50L;
    private static final long SIG_ZIP64_LOCATOR = 0x07064b50L;
    private static final long SIG_CENTRAL = 0x02014b50L;
    private static final long SIG_LOCAL = 0x04034b50L;

    public static final class Entry {
        public final String name;
        public final int method;
        public final long crc;
        public final long compressedSize;
        public final long size;
        public final long localHeaderOffset;
        public final int dosTime;
        public final int dosDate;
        public final int flags;

        Entry(String name, int method, long crc, long compressedSize, long size,
              long localHeaderOffset, int dosTime, int dosDate, int flags) {
            this.name = name; this.method = method; this.crc = crc;
            this.compressedSize = compressedSize; this.size = size;
            this.localHeaderOffset = localHeaderOffset;
            this.dosTime = dosTime; this.dosDate = dosDate; this.flags = flags;
        }

        public boolean isDirectory() { return name.endsWith("/"); }
    }

    private final FileChannel ch;
    private final List<Entry> entries = new ArrayList<Entry>();
    private final Map<String, Entry> byName = new LinkedHashMap<String, Entry>();
    private final long fileSize;

    private ZipReader(FileChannel ch) throws IOException {
        this.ch = ch;
        this.fileSize = ch.size();
        readCentralDirectory();
    }

    public static ZipReader open(FileChannel ch) throws IOException {
        return new ZipReader(ch);
    }

    public int entryCount() { return entries.size(); }
    public List<Entry> entries() { return Collections.unmodifiableList(entries); }
    public Iterable<String> names() { return byName.keySet(); }
    public boolean has(String name) { return byName.containsKey(name); }
    public Entry get(String name) { return byName.get(name); }

    public InputStream openRaw(Entry e) throws IOException {
        long lho = e.localHeaderOffset;
        byte[] h = new byte[30];
        readFully(lho, h, 0, 30);
        if (Bytes.u32(h, 0) != SIG_LOCAL) throw new IOException("bad local header for " + e.name);
        int nameLen = Bytes.u16(h, 26);
        int extraLen = Bytes.u16(h, 28);
        long dataStart = lho + 30 + nameLen + extraLen;
        return new BoundedChannelInputStream(ch, dataStart, e.compressedSize);
    }

    public InputStream openUncompressed(Entry e) throws IOException {
        if (e.method == 0) return openRaw(e);
        if (e.method == 8) return new InflaterInputStream(openRaw(e), new Inflater(true), 1 << 16);
        throw new IOException("unsupported compression method " + e.method + " for " + e.name);
    }

    private void readCentralDirectory() throws IOException {
        if (fileSize < 22) throw new IOException("file too small to be a ZIP");
        int back = (int) Math.min(fileSize, 22 + 0xFFFF + 20);
        byte[] tail = new byte[back];
        readFully(fileSize - back, tail, 0, back);

        int eocd = -1;
        for (int i = back - 22; i >= 0; i--) {
            if (Bytes.u32(tail, i) == SIG_EOCD) { eocd = i; break; }
        }
        if (eocd < 0) throw new IOException("end-of-central-directory record not found");

        long entryCount = Bytes.u16(tail, eocd + 10);
        long cdSize = Bytes.u32(tail, eocd + 12);
        long cdOffset = Bytes.u32(tail, eocd + 16);

        long eocdAbs = fileSize - back + eocd;
        if (eocdAbs >= 20) {
            byte[] loc = new byte[20];
            readFully(eocdAbs - 20, loc, 0, 20);
            if (Bytes.u32(loc, 0) == SIG_ZIP64_LOCATOR) {
                long z64 = readU64(loc, 8);
                byte[] z = new byte[56];
                readFully(z64, z, 0, 56);
                if (Bytes.u32(z, 0) == SIG_ZIP64_EOCD) {
                    entryCount = readU64(z, 32);
                    cdSize = readU64(z, 40);
                    cdOffset = readU64(z, 48);
                }
            }
        }

        if (cdOffset < 0 || cdOffset + cdSize > fileSize) throw new IOException("central directory out of bounds");
        if (entryCount > 1_000_000L) throw new IOException("unreasonable entry count " + entryCount);

        byte[] cd = new byte[(int) cdSize];
        readFully(cdOffset, cd, 0, cd.length);

        int p = 0;
        for (long k = 0; k < entryCount; k++) {
            if (p + 46 > cd.length) throw new IOException("truncated central directory");
            if (Bytes.u32(cd, p) != SIG_CENTRAL) throw new IOException("bad central header at " + p);

            int flags = Bytes.u16(cd, p + 8);
            int method = Bytes.u16(cd, p + 10);
            int dosTime = Bytes.u16(cd, p + 12);
            int dosDate = Bytes.u16(cd, p + 14);
            long crc = Bytes.u32(cd, p + 16);
            long csize = Bytes.u32(cd, p + 20);
            long usize = Bytes.u32(cd, p + 24);
            int nameLen = Bytes.u16(cd, p + 28);
            int extraLen = Bytes.u16(cd, p + 30);
            int commentLen = Bytes.u16(cd, p + 32);
            long lho = Bytes.u32(cd, p + 42);

            String name = new String(cd, p + 46, nameLen, StandardCharsets.UTF_8);

            int ex = p + 46 + nameLen;
            int exEnd = ex + extraLen;
            while (ex + 4 <= exEnd) {
                int id = Bytes.u16(cd, ex);
                int sz = Bytes.u16(cd, ex + 2);
                if (ex + 4 + sz > exEnd) break;
                if (id == 0x0001) {
                    int q = ex + 4;
                    if (usize == 0xFFFFFFFFL && q + 8 <= exEnd) { usize = readU64(cd, q); q += 8; }
                    if (csize == 0xFFFFFFFFL && q + 8 <= exEnd) { csize = readU64(cd, q); q += 8; }
                    if (lho == 0xFFFFFFFFL && q + 8 <= exEnd) { lho = readU64(cd, q); q += 8; }
                }
                ex += 4 + sz;
            }

            Entry e = new Entry(name, method, crc, csize, usize, lho, dosTime, dosDate, flags);
            entries.add(e);
            byName.put(name, e);
            p += 46 + nameLen + extraLen + commentLen;
        }
    }

    private void readFully(long pos, byte[] buf, int off, int len) throws IOException {
        ByteBuffer bb = ByteBuffer.wrap(buf, off, len);
        long p = pos;
        while (bb.hasRemaining()) {
            int n = ch.read(bb, p);
            if (n < 0) throw new EOFException("unexpected EOF at " + p);
            p += n;
        }
    }

    private static long readU64(byte[] b, int o) {
        return Bytes.u32(b, o) | (Bytes.u32(b, o + 4) << 32);
    }

    @Override
    public void close() throws IOException { ch.close(); }

    /** InputStream over a fixed byte range of a channel, using positional reads (thread safe). */
    private static final class BoundedChannelInputStream extends InputStream {
        private final FileChannel ch;
        private long pos;
        private final long end;

        BoundedChannelInputStream(FileChannel ch, long start, long length) {
            this.ch = ch; this.pos = start; this.end = start + length;
        }

        @Override
        public int read() throws IOException {
            byte[] b = new byte[1];
            int n = read(b, 0, 1);
            return n < 0 ? -1 : (b[0] & 0xFF);
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            if (pos >= end) return -1;
            int want = (int) Math.min(len, end - pos);
            ByteBuffer bb = ByteBuffer.wrap(b, off, want);
            int n = ch.read(bb, pos);
            if (n < 0) return -1;
            pos += n;
            return n;
        }

        @Override
        public long skip(long n) throws IOException {
            long want = Math.min(n, end - pos);
            if (want <= 0) return 0;
            pos += want;
            return want;
        }

        @Override
        public int available() {
            return (int) Math.min(Integer.MAX_VALUE, end - pos);
        }
    }
}
