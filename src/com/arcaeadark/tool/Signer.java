package com.arcaeadark.tool;

import com.android.apksig.ApkSigner;
import com.android.apksig.ApkVerifier;

import java.io.File;
import java.io.InputStream;
import java.security.Key;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.cert.Certificate;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;
import java.util.Locale;

/**
 * APK signing through Google's apksig library (the same engine apksigner uses), embedded in
 * the app so no external build-tools / JDK are needed on the device.
 */
public final class Signer {
    private Signer() {}

    public static final class KeyMaterial {
        public final PrivateKey privateKey;
        public final List<X509Certificate> certs;
        public final String alias;
        public KeyMaterial(PrivateKey privateKey, List<X509Certificate> certs, String alias) {
            this.privateKey = privateKey; this.certs = certs; this.alias = alias;
        }
    }

    /**
     * Loads a PKCS12 / JKS keystore from a stream. If {@code alias} is null or not present the
     * first key entry is used. The same password is used for the store and the key.
     */
    public static KeyMaterial loadKeyStore(InputStream in, String type, char[] password, String alias)
            throws Exception {
        KeyStore ks;
        try {
            ks = KeyStore.getInstance(type == null ? "PKCS12" : type);
        } catch (Exception e) {
            ks = KeyStore.getInstance("PKCS12");
        }
        ks.load(in, password);

        String use = null;
        if (alias != null && ks.containsAlias(alias) && ks.isKeyEntry(alias)) {
            use = alias;
        } else {
            Enumeration<String> aliases = ks.aliases();
            while (aliases.hasMoreElements()) {
                String a = aliases.nextElement();
                if (ks.isKeyEntry(a)) { use = a; break; }
            }
        }
        if (use == null) throw new IllegalArgumentException("keystore contains no private key entry");

        Key key = ks.getKey(use, password);
        if (!(key instanceof PrivateKey)) throw new IllegalArgumentException("entry " + use + " is not a private key");

        Certificate[] chain = ks.getCertificateChain(use);
        if (chain == null || chain.length == 0) throw new IllegalArgumentException("no certificate chain for " + use);

        List<X509Certificate> certs = new ArrayList<X509Certificate>();
        for (Certificate c : chain) {
            if (!(c instanceof X509Certificate)) throw new IllegalArgumentException("non-X509 certificate in chain");
            certs.add((X509Certificate) c);
        }
        return new KeyMaterial((PrivateKey) key, certs, use);
    }

    public static void sign(File input, File output, KeyMaterial km, int minSdk) throws Exception {
        String signerName = km.alias == null ? "CERT" : km.alias.toUpperCase(Locale.US);
        ApkSigner.SignerConfig cfg =
                new ApkSigner.SignerConfig.Builder(signerName, km.privateKey, km.certs).build();

        ApkSigner.Builder b = new ApkSigner.Builder(Collections.singletonList(cfg));
        b.setInputApk(input);
        b.setOutputApk(output);
        b.setV1SigningEnabled(true);
        b.setV2SigningEnabled(true);
        b.setV3SigningEnabled(true);
        b.setOtherSignersSignaturesPreserved(false);
        if (minSdk > 0) b.setMinSdkVersion(minSdk);
        b.build().sign();
    }

    /** Verifies the produced APK with the same engine apksigner uses (diagnostics before install). */
    public static String verifySummary(File apk) throws Exception {
        ApkVerifier.Result r = new ApkVerifier.Builder(apk).build().verify();
        StringBuilder sb = new StringBuilder();
        sb.append(r.isVerified() ? "通过" : "未通过");
        sb.append(" (v1=").append(r.isVerifiedUsingV1Scheme())
          .append(", v2=").append(r.isVerifiedUsingV2Scheme())
          .append(", v3=").append(r.isVerifiedUsingV3Scheme()).append(")");
        return sb.toString();
    }
}
