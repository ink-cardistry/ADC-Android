package com.arcaeadark.tool;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.nio.channels.FileChannel;
import java.util.HashMap;
import java.util.Map;

/**
 * Framework-agnostic APK repackaging pipeline:
 *   read source (SAF document or file) -> analyse rules -> rewrite entries + patch package name
 *   -> emit an already zipaligned unsigned APK -> sign with the embedded keystore.
 *
 * No Android classes are referenced, so this exact code can also be exercised by a desktop
 * JVM test harness on Termux.
 */
public final class ApkPipeline {
    public interface Log {
        void log(String message);
        void progress(long done, long total);
    }

    public static final class Options {
        public String newPackage = "moe.low.dark";
        public InputStream keystoreStream;
        public String keystoreType = "PKCS12";
        public String keystorePassword = "arcaeadark";
        public String keystoreAlias = "arcaeadark";
        public Rules rules;
        public int minSdk = 21;
        public boolean forceStoreArsc = true;
    }

    public static final class Result {
        public String sourcePackage = "";
        public String versionName = "";
        public long versionCode = -1;
        public String newPackage = "";
        public int entryCount;
        public int replacements;
        public int skips;
        public int unhandled;
        public long estimatedTargetBytes;
        public String manifestMessage = "";
        public String arscMessage = "";
        public File unsignedApk;
        public File signedApk;
    }

    private static final String MANIFEST = "AndroidManifest.xml";
    private static final String ARSC = "resources.arsc";

    private ApkPipeline() {}

    public static Result run(FileChannel source, File workDir, Options opts, Log log) throws Exception {
        if (!workDir.exists() && !workDir.mkdirs()) throw new java.io.IOException("cannot create work dir " + workDir);

        Result res = new Result();
        res.newPackage = opts.newPackage;

        ZipReader reader = ZipReader.open(source);
        try {
            res.entryCount = reader.entryCount();

            // ---- read manifest ----
            AxmlPatcher.ManifestInfo info = null;
            byte[] manifestBytes = null;
            ZipReader.Entry manifestEntry = reader.get(MANIFEST);
            if (manifestEntry != null) {
                manifestBytes = readAll(reader.openUncompressed(manifestEntry));
                info = AxmlPatcher.readManifestInfo(manifestBytes);
            }
            if (info != null) {
                res.sourcePackage = info.pkg;
                res.versionName = info.versionName == null ? "" : info.versionName;
                res.versionCode = info.versionCode;
            }
            log.log("source package: " + res.sourcePackage
                    + "   version: " + (res.versionName.isEmpty() ? "?" : res.versionName)
                    + (res.versionCode > 0 ? " (" + res.versionCode + ")" : ""));

            // ---- plan ----
            Rules rules = opts.rules;
            if (rules == null) throw new IllegalArgumentException("no replacement rules loaded");
            Rules.Plan plan = rules.buildPlan(reader);
            res.replacements = plan.replacements.size();
            res.skips = plan.skips.size();
            res.unhandled = plan.unhandledSideSources.size();
            res.estimatedTargetBytes = plan.estimatedTargetBytes;
            log.log("plan: " + res.replacements + " replacement(s), " + res.skips + " skipped, "
                    + res.unhandled + " possibly unhandled side resource(s)");

            // ---- overrides ----
            Map<String, Ov> overrides = new HashMap<String, Ov>();
            for (Rules.Replacement r : plan.replacements) overrides.put(r.from, Ov.fromEntry(r.to));
            for (Rules.SkipRecord s : plan.skips) {
                if (res.skips <= 60) log.log("  skip " + s.from + " -> " + s.to + "  (" + s.reason + ")");
            }

            // ---- patch package name ----
            if (manifestEntry != null && manifestBytes != null && info != null) {
                AxmlPatcher.Result r1 = AxmlPatcher.replacePackageName(manifestBytes, res.sourcePackage, opts.newPackage);
                res.manifestMessage = r1.message;
                if (r1.changed) overrides.put(MANIFEST, Ov.content(r1.data));
                log.log(r1.message);
            } else {
                res.manifestMessage = "AndroidManifest.xml missing or not parseable; package name was not changed";
                log.log(res.manifestMessage);
            }

            ZipReader.Entry arscEntry = reader.get(ARSC);
            if (arscEntry != null) {
                byte[] arsc = readAll(reader.openUncompressed(arscEntry));
                java.util.List<String> pkgs = ArscPatcher.listPackages(arsc);
                if (!pkgs.isEmpty()) log.log("resources.arsc packages: " + pkgs);
                ArscPatcher.Result r2 = ArscPatcher.replacePackageName(arsc, res.sourcePackage, opts.newPackage);
                res.arscMessage = r2.message;
                if (r2.changed) overrides.put(ARSC, Ov.content(r2.data));
                log.log(r2.message);
            } else {
                res.arscMessage = "resources.arsc not present (skipped)";
                log.log(res.arscMessage);
            }

            // ---- repack ----
            res.unsignedApk = new File(workDir, "arc-dark-unsigned.apk");
            deleteIfExists(res.unsignedApk);

            long total = 0;
            for (ZipReader.Entry e : reader.entries()) if (!e.isDirectory()) total += e.size;
            long done = 0;

            ZipWriter writer = new ZipWriter(res.unsignedApk);
            try {
                for (ZipReader.Entry e : reader.entries()) {
                    if (e.isDirectory()) continue;
                    Ov ov = overrides.get(e.name);
                    int method = e.method;
                    if (opts.forceStoreArsc && ARSC.equals(e.name)) method = 0;

                    if (ov == null) {
                        writer.addRaw(e.name, e.method, e.dosTime, e.dosDate, e.flags,
                                e.crc, e.size, reader.openRaw(e));
                    } else if (ov.content != null) {
                        writer.addBytes(e.name, method, e.dosTime, e.dosDate, e.flags, ov.content);
                    } else {
                        ZipReader.Entry src = reader.get(ov.fromEntry);
                        if (src == null) throw new java.io.IOException("missing source entry " + ov.fromEntry);
                        writer.addUncompressed(e.name, method, e.dosTime, e.dosDate, e.flags,
                                reader.openUncompressed(src));
                    }
                    done += e.size;
                    log.progress(done, total);
                }
            } finally {
                writer.close();
            }
            log.log("repacked -> " + res.unsignedApk.getName() + " (" + human(res.unsignedApk.length()) + ")");

            // ---- sign ----
            if (opts.keystoreStream != null) {
                Signer.KeyMaterial km = Signer.loadKeyStore(opts.keystoreStream, opts.keystoreType,
                        opts.keystorePassword.toCharArray(), opts.keystoreAlias);
                log.log("keystore alias: " + km.alias + "  certs: " + km.certs.size());
                res.signedApk = new File(workDir, "arc-dark-signed.apk");
                deleteIfExists(res.signedApk);
                Signer.sign(res.unsignedApk, res.signedApk, km, opts.minSdk);
                log.log("signed -> " + res.signedApk.getName() + " (" + human(res.signedApk.length()) + ")");
            }

            return res;
        } finally {
            reader.close();
        }
    }

    private static final class Ov {
        final String fromEntry;
        final byte[] content;
        private Ov(String fromEntry, byte[] content) { this.fromEntry = fromEntry; this.content = content; }
        static Ov fromEntry(String name) { return new Ov(name, null); }
        static Ov content(byte[] c) { return new Ov(null, c); }
    }

    private static byte[] readAll(InputStream in) throws java.io.IOException {
        try {
            ByteArrayOutputStream bos = new ByteArrayOutputStream(1 << 16);
            byte[] buf = new byte[1 << 16];
            int r;
            while ((r = in.read(buf)) > 0) bos.write(buf, 0, r);
            return bos.toByteArray();
        } finally {
            try { in.close(); } catch (Exception ignore) { }
        }
    }

    public static FileChannel openChannel(File f) throws java.io.IOException {
        return new FileInputStream(f).getChannel();
    }

    private static void deleteIfExists(File f) { if (f.exists() && !f.delete()) f.deleteOnExit(); }

    public static String human(long n) {
        if (n < 1024) return n + " B";
        double v = n;
        String[] u = { "KB", "MB", "GB", "TB" };
        int i = -1;
        while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
        return String.format(java.util.Locale.US, "%.1f %s", v, u[i]);
    }
}
