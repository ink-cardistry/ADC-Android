import com.arcaeadark.tool.ArscPatcher;
import java.nio.file.*; import java.io.*; import java.util.*; import java.util.zip.*;
public class ProbeArsc {
  public static void main(String[] a) throws Exception {
    ZipFile z = new ZipFile(a[0]);
    ZipEntry e = z.getEntry("resources.arsc");
    ByteArrayOutputStream bos = new ByteArrayOutputStream();
    InputStream in = z.getInputStream(e); byte[] b=new byte[8192]; int r;
    while((r=in.read(b))>0) bos.write(b,0,r);
    byte[] arsc = bos.toByteArray();
    System.out.println("arsc size="+arsc.length+" packages="+ArscPatcher.listPackages(arsc));
  }
}
