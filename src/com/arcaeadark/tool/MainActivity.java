package com.arcaeadark.tool;

import android.Manifest;
import android.app.Activity;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.method.ScrollingMovementMethod;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Single-screen UI. The source APK is imported manually through the system document picker
 * (Storage Access Framework) — no adb, no device connection of any kind.
 */
public class MainActivity extends Activity implements Job.Listener {
    private static final int REQ_PICK = 1001;
    private static final int REQ_EXPORT = 1002;
    private static final int REQ_NOTIF = 1003;

    private final Handler ui = new Handler(Looper.getMainLooper());

    private TextView sourceInfo;
    private TextView status;
    private TextView logView;
    private ProgressBar progress;
    private EditText newPkg;
    private Button startBtn;
    private Button installBtn;
    private Button exportBtn;

    private Uri sourceUri;
    private String signedPath;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(buildUi());

        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, REQ_NOTIF);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        Job.setListener(this);
        refreshButtons();
    }

    @Override
    protected void onPause() {
        super.onPause();
        Job.setListener(null);
    }

    // ------------------------------------------------------------------
    //  UI
    // ------------------------------------------------------------------

    private View buildUi() {
        int pad = dp(16);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(pad, pad, pad, pad);
        root.setBackgroundColor(Color.parseColor("#101418"));

        TextView title = new TextView(this);
        title.setText("Arcaea 全暗改包（本地版）");
        title.setTextColor(Color.WHITE);
        title.setTextSize(20);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        root.addView(title);

        TextView sub = new TextView(this);
        sub.setText("手动导入原版 APK → 替换为纷争侧资源 → 修改包名 → 对齐签名。\n无需 adb，全程在本机完成。");
        sub.setTextColor(Color.parseColor("#9AA7B4"));
        sub.setTextSize(12);
        sub.setPadding(0, dp(4), 0, dp(10));
        root.addView(sub);

        Button pick = new Button(this);
        pick.setText("1. 手动导入 APK（选择文件）");
        pick.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { pickApk(); }
        });
        root.addView(pick);

        sourceInfo = new TextView(this);
        sourceInfo.setText("未选择文件");
        sourceInfo.setTextColor(Color.parseColor("#C8D2DC"));
        sourceInfo.setTextSize(12);
        sourceInfo.setPadding(dp(4), dp(6), dp(4), dp(10));
        root.addView(sourceInfo);

        TextView pkgLabel = new TextView(this);
        pkgLabel.setText("2. 新包名（与原版共存，默认 moe.low.dark）");
        pkgLabel.setTextColor(Color.parseColor("#C8D2DC"));
        pkgLabel.setTextSize(12);
        root.addView(pkgLabel);

        newPkg = new EditText(this);
        newPkg.setText("moe.low.dark");
        newPkg.setTextColor(Color.WHITE);
        newPkg.setHintTextColor(Color.GRAY);
        newPkg.setSingleLine(true);
        root.addView(newPkg);

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(0, dp(10), 0, 0);
        startBtn = new Button(this);
        startBtn.setText("3. 开始改包");
        startBtn.setEnabled(false);
        startBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { startJob(); }
        });
        row.addView(startBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        exportBtn = new Button(this);
        exportBtn.setText("导出");
        exportBtn.setEnabled(false);
        exportBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { exportApk(); }
        });
        row.addView(exportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        installBtn = new Button(this);
        installBtn.setText("安装");
        installBtn.setEnabled(false);
        installBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { installApk(); }
        });
        row.addView(installBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        root.addView(row);

        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(1000);
        LinearLayout.LayoutParams plp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        plp.topMargin = dp(10);
        root.addView(progress, plp);

        status = new TextView(this);
        status.setText("就绪");
        status.setTextColor(Color.parseColor("#8FD3A0"));
        status.setTextSize(12);
        status.setPadding(0, dp(6), 0, dp(6));
        root.addView(status);

        ScrollView scroll = new ScrollView(this);
        logView = new TextView(this);
        logView.setTextColor(Color.parseColor("#B9C6D3"));
        logView.setTextSize(11);
        logView.setTypeface(Typeface.MONOSPACE);
        logView.setMovementMethod(new ScrollingMovementMethod());
        scroll.addView(logView);
        root.addView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));
        return root;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    private void refreshButtons() {
        boolean running = Job.isRunning();
        startBtn.setEnabled(!running && sourceUri != null);
        boolean has = signedPath != null && new File(signedPath).exists();
        installBtn.setEnabled(!running && has);
        exportBtn.setEnabled(!running && has);
    }

    // ------------------------------------------------------------------
    //  Pick / export / install
    // ------------------------------------------------------------------

    private void pickApk() {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        i.addCategory(Intent.CATEGORY_OPENABLE);
        i.setType("*/*");
        i.putExtra(Intent.EXTRA_MIME_TYPES, new String[] {
                "application/vnd.android.package-archive", "application/zip", "application/octet-stream", "*/*"
        });
        try {
            startActivityForResult(i, REQ_PICK);
        } catch (Exception e) {
            toast("无法打开文件选择器：" + e.getMessage());
        }
    }

    private void startJob() {
        if (sourceUri == null) { toast("请先导入 APK"); return; }
        String pkg = newPkg.getText().toString().trim();
        if (pkg.length() < 3 || !pkg.contains(".")) { toast("新包名不合法"); return; }
        signedPath = null;
        progress.setProgress(0);
        refreshButtons();
        Job.start(getApplicationContext(), sourceUri, pkg);
    }

    private void exportApk() {
        if (signedPath == null) return;
        Intent i = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        i.addCategory(Intent.CATEGORY_OPENABLE);
        i.setType("application/vnd.android.package-archive");
        i.putExtra(Intent.EXTRA_TITLE, new File(signedPath).getName());
        try {
            startActivityForResult(i, REQ_EXPORT);
        } catch (Exception e) {
            toast("无法打开保存对话框：" + e.getMessage());
        }
    }

    private void installApk() {
        if (signedPath == null) return;
        final File apk = new File(signedPath);
        status.setText("正在准备安装包…");
        new Thread(new Runnable() {
            public void run() {
                try {
                    installWithSession(apk);
                } catch (final Exception e) {
                    ui.post(new Runnable() { public void run() {
                        status.setText("安装失败：" + e.getMessage());
                        toast("安装失败：" + e.getMessage());
                    }});
                }
            }
        }, "install").start();
    }

    private void installWithSession(File apk) throws Exception {
        PackageInstaller pi = getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams sp =
                new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        int id = pi.createSession(sp);
        PackageInstaller.Session session = pi.openSession(id);
        try {
            InputStream in = new FileInputStream(apk);
            OutputStream out = session.openWrite("base.apk", 0, apk.length());
            try {
                byte[] buf = new byte[1 << 20];
                int r;
                while ((r = in.read(buf)) > 0) out.write(buf, 0, r);
                session.fsync(out);
            } finally {
                try { in.close(); } catch (Exception ignore) { }
                try { out.close(); } catch (Exception ignore) { }
            }
        } finally {
            session.close();
        }
        Intent intent = new Intent(this, InstallReceiver.class);
        intent.setAction(InstallReceiver.ACTION_RESULT);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= 31) flags |= PendingIntent.FLAG_MUTABLE;
        PendingIntent pending = PendingIntent.getBroadcast(this, id, intent, flags);
        session.commit(pending.getIntentSender());
        ui.post(new Runnable() { public void run() {
            status.setText("已提交安装，请在系统弹窗中确认");
        }});
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_PICK && resultCode == RESULT_OK && data != null && data.getData() != null) {
            sourceUri = data.getData();
            try {
                getContentResolver().takePersistableUriPermission(sourceUri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION);
            } catch (Exception ignore) { }
            sourceInfo.setText("已选择：" + sourceUri.toString());
            progress.setProgress(0);
            refreshButtons();
            return;
        }
        if (requestCode == REQ_EXPORT && resultCode == RESULT_OK && data != null && data.getData() != null) {
            final Uri dst = data.getData();
            final String src = signedPath;
            status.setText("正在导出…");
            new Thread(new Runnable() {
                public void run() {
                    try {
                        copyToUri(src, dst);
                        ui.post(new Runnable() { public void run() {
                            status.setText("已导出到所选位置");
                            toast("导出完成");
                        }});
                    } catch (final Exception e) {
                        ui.post(new Runnable() { public void run() {
                            status.setText("导出失败：" + e.getMessage());
                        }});
                    }
                }
            }, "export").start();
        }
    }

    private void copyToUri(String src, Uri dst) throws Exception {
        InputStream in = new FileInputStream(src);
        OutputStream out = getContentResolver().openOutputStream(dst);
        if (out == null) throw new Exception("无法写入目标位置");
        try {
            byte[] buf = new byte[1 << 20];
            int r;
            while ((r = in.read(buf)) > 0) out.write(buf, 0, r);
        } finally {
            try { in.close(); } catch (Exception ignore) { }
            try { out.close(); } catch (Exception ignore) { }
        }
    }

    // ------------------------------------------------------------------
    //  Job.Listener
    // ------------------------------------------------------------------

    @Override
    public void onLog(final String wholeLog) {
        ui.post(new Runnable() { public void run() {
            logView.setText(wholeLog);
            scrollLogToBottom();
        }});
    }

    private void scrollLogToBottom() {
        View parent = (View) logView.getParent();
        if (parent instanceof ScrollView) {
            final ScrollView sv = (ScrollView) parent;
            sv.post(new Runnable() { public void run() { sv.fullScroll(View.FOCUS_DOWN); }});
        }
    }

    @Override
    public void onProgress(final long done, final long total) {
        ui.post(new Runnable() { public void run() {
            if (total > 0) progress.setProgress((int) (done * 1000 / total));
            status.setText("处理中… " + ApkPipeline.human(done) + " / " + ApkPipeline.human(total));
        }});
    }

    @Override
    public void onFinished(final boolean ok, final String summary, final String signedPath) {
        this.signedPath = signedPath;
        ui.post(new Runnable() { public void run() {
            status.setText(summary == null ? (ok ? "完成" : "失败") : summary);
            status.setTextColor(ok ? Color.parseColor("#8FD3A0") : Color.parseColor("#FF8A80"));
            if (ok) progress.setProgress(1000);
            refreshButtons();
        }});
    }

    private void toast(String s) {
        Toast.makeText(this, s, Toast.LENGTH_LONG).show();
    }
}
