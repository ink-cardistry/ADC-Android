package com.arcaeadark.tool;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.os.Build;
import android.util.Log;
import android.widget.Toast;

/**
 * Receives the result of a PackageInstaller session.
 *
 * Important: for a normal (non-system) app the first commit usually comes back as
 * {@link PackageInstaller#STATUS_PENDING_USER_ACTION} with a null status message and the
 * confirmation Intent in {@link Intent#EXTRA_INTENT}. That Intent MUST be launched, otherwise
 * the install silently does nothing — which is what previously looked like "install returns null".
 */
public class InstallReceiver extends BroadcastReceiver {
    public static final String ACTION_RESULT = "com.arcaeadark.tool.INSTALL_RESULT";
    private static final String TAG = "ArcaeaDark";

    /** Lets the Activity surface the final result in its status line. */
    public interface Listener { void onInstallResult(boolean ok, String text); }
    private static volatile Listener listener;

    public static void setListener(Listener l) { listener = l; }

    @Override
    public void onReceive(Context context, Intent intent) {
        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        Log.i(TAG, "install callback status=" + status + " message=" + message);

        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirm = extractIntent(intent);
            if (confirm != null) {
                confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                try {
                    context.startActivity(confirm);
                    notify(false, "请在系统弹窗中确认安装");
                    return;
                } catch (Exception e) {
                    notify(false, "无法打开安装确认界面：" + e);
                    return;
                }
            }
            notify(false, "系统要求确认安装，但没有返回确认界面");
            return;
        }

        if (status == PackageInstaller.STATUS_SUCCESS) {
            toast(context, "安装完成");
            notify(true, "安装完成");
        } else {
            String text = "安装未完成：" + statusName(status)
                    + (message == null ? "" : "（" + message + "）");
            toast(context, text);
            notify(false, text);
        }
    }

    @SuppressWarnings("deprecation")
    private static Intent extractIntent(Intent intent) {
        if (Build.VERSION.SDK_INT >= 33) return intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent.class);
        return (Intent) intent.getParcelableExtra(Intent.EXTRA_INTENT);
    }

    private static void notify(boolean ok, String text) {
        Listener l = listener;
        if (l != null) l.onInstallResult(ok, text);
    }

    private static String statusName(int s) {
        switch (s) {
            case PackageInstaller.STATUS_FAILURE: return "失败";
            case PackageInstaller.STATUS_FAILURE_BLOCKED:
                return "被系统阻止（请允许本应用安装未知应用）";
            case PackageInstaller.STATUS_FAILURE_ABORTED: return "已中止";
            case PackageInstaller.STATUS_FAILURE_INVALID: return "安装包无效或已损坏";
            case PackageInstaller.STATUS_FAILURE_CONFLICT:
                return "与已安装应用冲突（同包名不同签名，需先卸载原版，注意先同步存档）";
            case PackageInstaller.STATUS_FAILURE_STORAGE: return "存储空间不足";
            case PackageInstaller.STATUS_FAILURE_INCOMPATIBLE: return "与设备不兼容";
            default: return "状态码 " + s;
        }
    }

    private static void toast(Context c, String s) {
        Toast.makeText(c.getApplicationContext(), s, Toast.LENGTH_LONG).show();
    }
}
