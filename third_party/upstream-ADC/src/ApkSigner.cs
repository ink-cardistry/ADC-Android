namespace ArcaeaDarkApkCreator;

/// <summary>zipalign 对齐 + apksigner 签名 + keytool 生成密钥。</summary>
internal static class ApkSigner
{
    // 注意：apksigner.bat 不读取 JAVA_OPTS，而是把 -Jxxx 透传给 java（默认堆只有 1G）。
    private static readonly string[] ApksignerJavaArgs = { "-JXmx4096m", "-JXss4m" };

    // ---------------- 密钥 ----------------

    /// <summary>
    /// 确保签名密钥可用。优先释放**内置的公共密钥**（所有用户一致），而不是现场生成随机密钥：
    /// Android 只允许"同一把密钥 + 同一包名"的包覆盖安装，密钥一变就必须先卸载，
    /// 而卸载会清掉本地存档以及 arcaea 按需下载的曲目数据（GB 级，需要重新下载）。
    /// 若目标路径已存在密钥则直接沿用（允许用户换成自己的）。
    /// </summary>
    public static bool EnsureKeystore(ToolPaths tools, string keystorePath, string alias,
        string password, string dname)
    {
        if (File.Exists(keystorePath))
        {
            ConsoleUi.Ok($"已存在签名密钥: {keystorePath}");
            ConsoleUi.Dim("将直接沿用该密钥，签名与上一次保持一致（可覆盖更新）。");
            return true;
        }

        if (TryReleaseEmbeddedKeystore(keystorePath, out var message))
        {
            ConsoleUi.Ok(message);
            return true;
        }

        ConsoleUi.Warn($"未能释放内置公共密钥（{message}），需要现场生成一把随机密钥。");
        ConsoleUi.Warn("随机密钥与其它用户不一致：你的包无法覆盖别人的安装，别人的包也无法覆盖你的，");
        ConsoleUi.Warn("升级时 Android 会要求先卸载，本地存档与已下载的曲目数据都会丢失。");
        if (!ConsoleUi.Confirm("确定要生成随机密钥吗？", false)) return false;

        if (tools.Keytool == null)
        {
            ConsoleUi.Fail($"未找到 keytool，无法生成密钥。请安装 JDK: {ToolLocator.UrlJdk}");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keystorePath))!);

        var args = new List<string>
        {
            "-genkeypair", "-v",
            "-keystore", keystorePath,
            "-alias", alias,
            "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
            "-storepass", password,
            "-keypass", password,
            "-dname", dname,
            "-storetype", "PKCS12",
        };
        var res = ProcessRunner.RunWithSpinner(tools.Keytool, args, "正在生成随机签名密钥");
        if (!res.Ok || !File.Exists(keystorePath))
        {
            ConsoleUi.Fail("生成签名密钥失败");
            ConsoleUi.Dim(res.All.Length > 600 ? res.All[..600] : res.All);
            return false;
        }
        ConsoleUi.Ok($"已生成随机签名密钥: {keystorePath}  (别名 {alias} / 口令 {password})");
        ConsoleUi.Info("请备份这个文件：以后要用同一把密钥签名才能覆盖更新。");
        return true;
    }

    /// <summary>把编译进 exe 的公共密钥释放到磁盘。</summary>
    private static bool TryReleaseEmbeddedKeystore(string destPath, out string message)
    {
        message = "";
        try
        {
            var asm = typeof(ApkSigner).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("arcaea-dark.keystore", StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                message = "程序内未内置密钥";
                return false;
            }

            using var src = asm.GetManifestResourceStream(resName)
                            ?? throw new InvalidOperationException("无法读取内置密钥资源");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
            using var fs = File.Create(destPath);
            src.CopyTo(fs);

            message = $"已释放内置公共签名密钥: {destPath}";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>读取密钥证书的 SHA-256 指纹（用于确认签名一致）。</summary>
    public static string? KeystoreFingerprint(ToolPaths tools, string keystorePath, string alias, string password)
    {
        if (tools.Keytool == null || !File.Exists(keystorePath)) return null;
        var res = ProcessRunner.Run(tools.Keytool, new[]
        {
            "-list", "-v", "-keystore", keystorePath, "-storepass", password, "-alias", alias,
        });
        if (!res.Ok) return null;
        var m = System.Text.RegularExpressions.Regex.Match(res.All, @"SHA256:\s*([0-9A-Fa-f:]{60,})");
        return m.Success ? m.Groups[1].Value : null;
    }


    // ---------------- 对齐 ----------------

    public static bool Zipalign(ToolPaths tools, string input, string output)
    {
        if (tools.Zipalign == null)
        {
            ConsoleUi.Fail($"未找到 zipalign。请安装 Android build-tools: {ToolLocator.UrlBuildTools}");
            return false;
        }
        if (File.Exists(output)) File.Delete(output);

        var args = new[] { "-f", "-p", "4", input, output };
        var res = ProcessRunner.RunWithSpinner(tools.Zipalign, args, "正在对齐 (zipalign)");
        if (!res.Ok || !File.Exists(output))
        {
            ConsoleUi.Fail("zipalign 失败");
            ConsoleUi.Dim(res.All.Length > 600 ? res.All[..600] : res.All);
            return false;
        }
        ConsoleUi.Ok($"对齐完成: {output} ({ConsoleUi.Human(new FileInfo(output).Length)})");
        return true;
    }

    // ---------------- 签名 ----------------

    public static bool Sign(ToolPaths tools, string input, string output, string keystorePath,
        string alias, string password)
    {
        if (tools.Apksigner == null)
        {
            ConsoleUi.Fail($"未找到 apksigner。请安装 Android build-tools: {ToolLocator.UrlBuildTools}");
            return false;
        }
        if (File.Exists(output)) File.Delete(output);

        var args = new List<string>(ApksignerJavaArgs)
        {
            "sign",
            "--ks", keystorePath,
            "--ks-key-alias", alias,
            "--ks-pass", $"pass:{password}",
            "--key-pass", $"pass:{password}",
            "--v1-signing-enabled", "true",
            "--v2-signing-enabled", "true",
            "--v3-signing-enabled", "true",
            "--out", output,
            input,
        };
        var res = ProcessRunner.RunWithSpinner(tools.Apksigner, args, "正在签名 (apksigner)");
        if (!res.Ok || !File.Exists(output))
        {
            ConsoleUi.Fail("apksigner 签名失败");
            ConsoleUi.Dim(res.All.Length > 800 ? res.All[..800] : res.All);
            return false;
        }
        ConsoleUi.Ok($"签名完成: {output} ({ConsoleUi.Human(new FileInfo(output).Length)})");
        return true;
    }

    public static bool Verify(ToolPaths tools, string apk, out string detail)
    {
        detail = "";
        if (tools.Apksigner == null) return false;
        var args = new List<string>(ApksignerJavaArgs) { "verify", "--print-certs", apk };
        var res = ProcessRunner.Run(tools.Apksigner, args);
        detail = res.All;
        return res.Ok;
    }
}
