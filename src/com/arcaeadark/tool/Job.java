package com.arcaeadark.tool;

import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;

/** Holds the single long-running repackaging job and bridges it to the UI. */
public final class Job {
    private static final String TAG = "ArcaeaDark";

    public interface Listener {
        void onLog(String wholeLog);
        void onProgress(long done, long total);
        void onFinished(boolean ok, String summary, String signedPath);
    }

    private static volatile Listener listener;
    private static volatile boolean running;
    private static final StringBuilder LOG = new StringBuilder();
    private static volatile String signedPath;
    private static volatile String summary = "";
    private static volatile boolean lastOk;
    private static volatile boolean hasResult;

    private Job() {}

    public static void setListener(Listener l) {
        listener = l;
        if (l != null) {
            l.onLog(logText());
            if (!running && hasResult) l.onFinished(lastOk, summary, signedPath);
        }
    }

    public static boolean isRunning() { return running; }
    public static String signedPath() { return signedPath; }
    public static String summary() { return summary; }

    public static String logText() {
        synchronized (LOG) { return LOG.toString(); }
    }

    private static void append(String line) {
        synchronized (LOG) {
            LOG.append(line).append('\n');
            if (LOG.length() > 40000) LOG.delete(0, LOG.length() - 30000);
        }
        Listener l = listener;
        if (l != null) l.onLog(logText());
    }

    public static void clearLog() {
        synchronized (LOG) { LOG.setLength(0); }
        Listener l = listener;
        if (l != null) l.onLog("");
    }

    public static File workDir(Context ctx) {
        File ext = ctx.getExternalFilesDir(null);
        File base = ext != null ? ext : ctx.getFilesDir();
        return new File(base, "work");
    }

    public static String outputFileName(String version) {
        String name = "arcaea-dark";
        if (version != null && version.trim().length() > 0) {
            String safe = version.replaceAll("[\\\\/:*?\"<>|]", "_").trim();
            if (safe.length() > 0) name += "-" + safe;
        }
        return name + ".apk";
    }

    public static synchronized void start(final Context ctx, final Uri uri, final String newPkg) {
        if (running) {
            append("[!] a job is already running");
            return;
        }
        running = true;
        lastOk = false;
        clearLog();
        append("[i] source: " + uri);
        append("[i] new package: " + newPkg);
        append("[i] work dir: " + workDir(ctx));

        try {
            Intent svc = new Intent(ctx, KeepAliveService.class);
            svc.putExtra("text", "Arcaea 全暗改包 正在处理中…");
            ctx.startForegroundService(svc);
        } catch (Exception e) {
            Log.w(TAG, "cannot start keep-alive service", e);
        }

        Thread t = new Thread(new Runnable() {
            public void run() { runJob(ctx.getApplicationContext(), uri, newPkg); }
        }, "arcaea-dark-job");
        t.setDaemon(false);
        t.start();
    }

    private static void runJob(Context ctx, Uri uri, String newPkg) {
        ParcelFileDescriptor pfd = null;
        boolean ok = false;
        String msg = "";
        try {
            File work = workDir(ctx);
            if (!work.exists() && !work.mkdirs()) throw new IOException("cannot create work dir " + work);
            cleanOldOutputs(work);

            StringBuilder rulesTxt = new StringBuilder();
            readAsset(ctx, "dark_rules.json", rulesTxt);
            Rules rules = Rules.parse(rulesTxt.toString());
            append("[i] rules: " + rules.pairs.size() + " pairs / " + rules.patterns.size() + " patterns");

            pfd = ctx.getContentResolver().openFileDescriptor(uri, "r");
            if (pfd == null) throw new IOException("cannot open the selected document");
            FileChannel channel;
            try {
                channel = new FileInputStream(pfd.getFileDescriptor()).getChannel();
            } catch (Exception e) {
                throw new IOException("cannot read the selected document: " + e.getMessage());
            }

            long size = 0;
            try { size = channel.size(); } catch (Exception ignore) { }
            File copied = null;
            if (size <= 0) {
                append("[i] document is not seekable; copying it into app storage first…");
                copied = new File(work, "source.apk");
                copyFromUri(ctx, uri, copied);
                channel = new FileInputStream(copied).getChannel();
                append("[i] copied " + ApkPipeline.human(copied.length()));
            }
            append("[i] source size: " + ApkPipeline.human(size > 0 ? size : copied.length()));

            ApkPipeline.Options o = new ApkPipeline.Options();
            o.rules = rules;
            o.newPackage = newPkg;
            o.keystoreStream = ctx.getAssets().open("arcaea-dark.keystore");
            o.keystoreType = "PKCS12";
            o.keystorePassword = "arcaeadark";
            o.keystoreAlias = "arcaeadark";
            o.minSdk = 21;

            final long[] lastCb = { 0 };
            ApkPipeline.Result res = ApkPipeline.run(channel, work, o, new ApkPipeline.Log() {
                public void log(String m) { append(m); }
                public void progress(long done, long total) {
                    Listener l = listener;
                    if (l != null && (done - lastCb[0] > 2_000_000L || done == total)) {
                        lastCb[0] = done;
                        l.onProgress(done, total);
                    }
                }
            });

            if (res.signedApk == null || !res.signedApk.exists()) {
                throw new IOException("signing produced no output");
            }
            signedPath = res.signedApk.getAbsolutePath();
            summary = "完成：" + res.replacements + " 个资源已替换；包名 " + res.sourcePackage + " → " + res.newPackage
                    + "；体积 " + ApkPipeline.human(res.signedApk.length());
            append("[✓] " + summary);
            append("[i] signed APK: " + signedPath);
            ok = true;
        } catch (Throwable e) {
            Log.e(TAG, "job failed", e);
            msg = e.getClass().getSimpleName() + ": " + e.getMessage();
            append("[✗] failed: " + msg);
        } finally {
            try { if (pfd != null) pfd.close(); } catch (Exception ignore) { }
            running = false;
            lastOk = ok;
            hasResult = true;
            if (!ok) summary = "失败：" + msg;
            try { ctx.stopService(new Intent(ctx, KeepAliveService.class)); } catch (Exception ignore) { }
            Listener l = listener;
            if (l != null) l.onFinished(ok, summary, signedPath);
        }
    }

    private static void cleanOldOutputs(File work) {
        String[] names = { "arc-dark-unsigned.apk", "arc-dark-signed.apk", "source.apk" };
        for (String n : names) {
            File f = new File(work, n);
            if (f.exists()) f.delete();
        }
    }

    private static void readAsset(Context ctx, String name, StringBuilder out) throws IOException {
        InputStream in = ctx.getAssets().open(name);
        try {
            byte[] buf = new byte[1 << 16];
            int r;
            java.io.ByteArrayOutputStream bos = new java.io.ByteArrayOutputStream();
            while ((r = in.read(buf)) > 0) bos.write(buf, 0, r);
            out.append(new String(bos.toByteArray(), StandardCharsets.UTF_8));
        } finally {
            in.close();
        }
    }

    private static void copyFromUri(Context ctx, Uri uri, File dst) throws IOException {
        InputStream in = ctx.getContentResolver().openInputStream(uri);
        if (in == null) throw new IOException("cannot open input stream");
        OutputStream os = new FileOutputStream(dst);
        try {
            byte[] buf = new byte[1 << 20];
            int r;
            long done = 0;
            while ((r = in.read(buf)) > 0) {
                os.write(buf, 0, r);
                done += r;
            }
        } finally {
            try { in.close(); } catch (Exception ignore) { }
            try { os.close(); } catch (Exception ignore) { }
        }
    }
}
