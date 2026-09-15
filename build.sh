#!/usr/bin/env bash
# Builds the standalone Android APK of the Arcaea dark repackager.
#
# Works both on-device (Termux: openjdk-17 + aapt2 + d8 + apksigner) and on CI
# (JDK 17 + Android SDK build-tools + platforms/android-XX/android.jar).
#
#   ANDROID_JAR=/path/to/android.jar ./build.sh
#   OUT=out
set -e

cd "$(dirname "$0")"

# Termux on-device locations (harmless / ignored elsewhere)
if [ -n "$PREFIX" ] && [ -d "$PREFIX/bin" ]; then export PATH="$PREFIX/bin:$PATH"; fi
if [ -n "$JAVA_HOME" ] && [ -d "$JAVA_HOME/bin" ]; then export PATH="$JAVA_HOME/bin:$PATH"; fi

OUT="${OUT:-out}"
APKSIG=libs/apksig.jar
AJ="${ANDROID_JAR:-/storage/emulated/0/MT2/mcp/sdk/platform35/android-35/android.jar}"

compile() {
  local rc
  set +e
  javac "$@" 2>&1 | grep -v '^Note:'
  rc=${PIPESTATUS[0]}
  set -e
  if [ "$rc" -ne 0 ]; then echo "javac failed (exit $rc)"; exit 1; fi
}

for t in javac jar keytool d8 aapt2 apksigner; do
  command -v "$t" >/dev/null 2>&1 || { echo "missing tool: $t"; exit 1; }
done
[ -f "$AJ" ] || { echo "android.jar not found: $AJ (set ANDROID_JAR)"; exit 1; }
[ -f "$APKSIG" ] || { echo "apksig.jar missing in libs/"; exit 1; }

echo "== 1/6 aapt2 link (manifest + resources + assets) =="
rm -rf "$OUT"; mkdir -p "$OUT/classes" "$OUT/dex" "$OUT/tools"
aapt2 compile --dir res -o "$OUT/res.zip" >/dev/null
aapt2 link -o "$OUT/base.apk" --manifest AndroidManifest.xml -A assets -R "$OUT/res.zip" -I "$AJ" \
    --min-sdk-version 26 --target-sdk-version 35

echo "== 2/6 javac (ported core) =="
mkdir -p "$OUT/core" "$OUT/app"
compile -encoding UTF-8 -source 8 -target 8 -bootclasspath "$AJ" -cp "$APKSIG" -d "$OUT/core" \
    src/com/arcaeadark/tool/Bytes.java src/com/arcaeadark/tool/Json.java \
    src/com/arcaeadark/tool/AxmlPatcher.java src/com/arcaeadark/tool/ArscPatcher.java \
    src/com/arcaeadark/tool/Rules.java src/com/arcaeadark/tool/ZipReader.java \
    src/com/arcaeadark/tool/ZipWriter.java src/com/arcaeadark/tool/Signer.java \
    src/com/arcaeadark/tool/ApkPipeline.java

echo "== 3/6 javac (Android UI) =="
compile -encoding UTF-8 -source 8 -target 8 -bootclasspath "$AJ" -cp "$APKSIG:$OUT/core" -d "$OUT/app" \
    src/com/arcaeadark/tool/Job.java src/com/arcaeadark/tool/MainActivity.java \
    src/com/arcaeadark/tool/KeepAliveService.java src/com/arcaeadark/tool/InstallReceiver.java

cp -r "$OUT/core/." "$OUT/classes/"
cp -r "$OUT/app/." "$OUT/classes/"
jar cf "$OUT/classes.jar" -C "$OUT/classes" .

echo "== 4/6 d8 =="
d8 --release --min-api 26 --lib "$AJ" --output "$OUT/dex" "$OUT/classes.jar" "$APKSIG"

echo "== 5/6 pack (already byte-aligned) =="
compile -encoding UTF-8 -source 8 -target 8 -cp "$OUT/core:$APKSIG" -d "$OUT/tools" jvmtest/PackApk.java
java -cp "$OUT/core:$OUT/tools" PackApk "$OUT/base.apk" "$OUT/unsigned.apk" "classes.dex=$OUT/dex/classes.dex"

echo "== 6/6 keystore + sign =="
if [ ! -f build/tool.keystore ]; then
  mkdir -p build
  keytool -genkeypair -keystore build/tool.keystore -alias arcaeadarktool -keyalg RSA -keysize 2048 \
    -validity 10000 -storepass arcaeadark -keypass arcaeadark \
    -dname "CN=Arcaea Dark Android Tool, OU=Local, O=Local, C=CN" -storetype PKCS12
fi
apksigner sign --ks build/tool.keystore --ks-key-alias arcaeadarktool \
  --ks-pass pass:arcaeadark --key-pass pass:arcaeadark \
  --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true \
  --out "$OUT/ArcaeaDarkTool.apk" "$OUT/unsigned.apk"

echo "== verify =="
apksigner verify --min-sdk-version 26 -v "$OUT/ArcaeaDarkTool.apk"
ls -la "$OUT/ArcaeaDarkTool.apk"
echo "DONE: $(pwd)/$OUT/ArcaeaDarkTool.apk"
