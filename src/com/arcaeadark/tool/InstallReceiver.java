package com.arcaeadark.tool;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.widget.Toast;

/** Receives the result of a PackageInstaller session. */
public class InstallReceiver extends BroadcastReceiver {
    public static final String ACTION_RESULT = "com.arcaeadark.tool.INSTALL_RESULT";

    @Override
    public void onReceive(Context context, Intent intent) {
        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
        String text;
        if (status == PackageInstaller.STATUS_SUCCESS) {
            text = "安装完成";
        } else {
            text = "安装未完成（" + status + "）：" + message;
        }
        Toast.makeText(context, text, Toast.LENGTH_LONG).show();
    }
}
