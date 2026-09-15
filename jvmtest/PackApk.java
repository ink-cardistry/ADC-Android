import com.arcaeadark.tool.ZipReader;
import com.arcaeadark.tool.ZipWriter;

import java.io.File;
import java.io.FileInputStream;
import java.util.LinkedHashMap;
import java.util.Map;

/** Repackages an APK produced by aapt2, adding/replacing entries (already byte-aligned). */
public class PackApk {
    public static void main(String[] args) throws Exception {
        File in = new File(args[0]);
        File out = new File(args[1]);
        Map<String, File> extras = new LinkedHashMap<String, File>();
        for (int i = 2; i < args.length; i++) {
            int eq = args[i].indexOf('=');
            extras.put(args[i].substring(0, eq), new File(args[i].substring(eq + 1)));
        }

        ZipReader r = ZipReader.open(new FileInputStream(in).getChannel());
        ZipWriter w = new ZipWriter(out);
        try {
            for (ZipReader.Entry e : r.entries()) {
                if (e.isDirectory()) continue;
                if (extras.containsKey(e.name)) continue;
                w.addRaw(e.name, e.method, e.dosTime, e.dosDate, e.flags, e.crc, e.size, r.openRaw(e));
            }
            for (Map.Entry<String, File> x : extras.entrySet()) {
                FileInputStream fis = new FileInputStream(x.getValue());
                try {
                    // dosDate 0x21 == 1980-01-01, deflated
                    w.addUncompressed(x.getKey(), 8, 0, 0x21, 0, fis);
                } finally {
                    fis.close();
                }
            }
        } finally {
            w.close();
            r.close();
        }
        System.out.println("packed " + out.getName() + " " + out.length() + " bytes");
    }
}
