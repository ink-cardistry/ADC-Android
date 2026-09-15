import com.arcaeadark.tool.ApkPipeline;
import com.arcaeadark.tool.Rules;

import java.io.File;
import java.io.FileInputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

public class TestMain {
    public static void main(String[] args) throws Exception {
        File source = new File(args[0]);
        File work = new File(args[1]);
        File rulesFile = new File(args[2]);
        File ks = new File(args[3]);
        String newPkg = args.length > 4 ? args[4] : "moe.low.dark";

        Rules rules = Rules.parse(new String(Files.readAllBytes(rulesFile.toPath()), StandardCharsets.UTF_8));
        ApkPipeline.Options o = new ApkPipeline.Options();
        o.rules = rules;
        o.newPackage = newPkg;
        o.keystoreStream = new FileInputStream(ks);
        o.keystoreType = "PKCS12";
        o.keystorePassword = "arcaeadark";
        o.keystoreAlias = "arcaeadark";

        final long[] last = { -1 };
        ApkPipeline.Result res = ApkPipeline.run(new FileInputStream(source).getChannel(), work, o,
                new ApkPipeline.Log() {
                    public void log(String m) { System.out.println("[log] " + m); }
                    public void progress(long d, long t) {
                        if (d - last[0] > 1_000_000L || d == t) {
                            System.out.println("[progress] " + d + "/" + t);
                            last[0] = d;
                        }
                    }
                });
        System.out.println("RESULT sourcePkg=" + res.sourcePackage + " -> " + res.newPackage
                + " version=" + res.versionName + " repl=" + res.replacements
                + " skips=" + res.skips + " unhandled=" + res.unhandled);
        System.out.println("RESULT unsigned=" + res.unsignedApk + " signed=" + res.signedApk);
    }
}
