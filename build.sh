#!/data/data/com.dsharnessmobile.shell/files/usr/bin/bash
# Builds the standalone Android APK of the Arcaea dark repackager on the device itself
# (Termux toolchain: openjdk-17 + aapt2 + d8 + apksigner, and a local android.jar).
set -e

PREFIX="${PREFIX:-/data/data/com.dsharnessmobile.shell/files/usr}"
export PATH="$PREFIX/bin:/system/bin"
export JAVA_HOME="$PREFIX/lib/jvm/java-17-openjdk"

cd "$(dirname "$0")"
AJ="${ANDROID_JAR:-/storage/emulated/0/MT2/mcp/sdk/platform35/android-35/android.jar}"
OUT=out
APKSIG=libs/apksig.jar

if [ ! -f "$AJ" ]; then echo "android.jar not found at $AJ"; exit 1; fi
if [ ! -f "$APKSIG" ]; then echo "apksig.jar missing in libs/"; exit 1; fi

echo "== 1/6 aapt2 link (manifest + assets) =="
rm -rf "$OUT"; mkdir -p "$OUT/classes" "$OUT/dex" "$OUT/tools"
aapt2 compile --dir res -o "$OUT/res.zip" >/dev/null
aapt2 link -o "$OUT/base.apk" --manifest AndroidManifest.xml -A assets -R "$OUT/res.zip" -I "$AJ" \
    --min-sdk-version 26 --target-sdk-version 35

echo "== 2/6 javac (app + ported core) =="
mkdir -p "$OUT/core" "$OUT/app"
javac -encoding UTF-8 -source 8 -target 8 -bootclasspath "$AJ" -cp "$APKSIG" \
    -d "$OUT/core" src/com/arcaeadark/tool/Bytes.java src/com/arcaeadark/tool/Json.java \
    src/com/arcaeadark/tool/AxmlPatcher.java src/com/arcaeadark/tool/ArscPatcher.java \
    src/com/arcaeadark/tool/Rules.java src/com/arcaeadark/tool/ZipReader.java \
    src/com/arcaeadark/tool/ZipWriter.java src/com/arcaeadark/tool/Signer.java \
    src/com/arcaeadark/tool/ApkPipeline.java 2>&1 | grep -v "^Note:" || true
javac -encoding UTF-8 -source 8 -target 8 -bootclasspath "$AJ" -cp "$APKSIG:$OUT/core" \
    -d "$OUT/app" src/com/arcaeadark/tool/Job.java src/com/arcaeadark/tool/MainActivity.java \
    src/com/arcaeadark/tool/KeepAliveService.java src/com/arcaeadark/tool/InstallReceiver.java 2>&1 | grep -v "^Note:" || true
cp -r "$OUT/core/." "$OUT/classes/"
cp -r "$OUT/app/." "$OUT/classes/"
jar cf "$OUT/classes.jar" -C "$OUT/classes" .

echo "== 3/6 d8 =="
d8 --release --min-api 26 --lib "$AJ" --output "$OUT/dex" "$OUT/classes.jar" "$APKSIG"

echo "== 4/6 pack (aligned) =="
javac -encoding UTF-8 -source 8 -target 8 -cp "$OUT/core:$APKSIG" -d "$OUT/tools" jvmtest/PackApk.java 2>&1 | grep -v "^Note:" || true
java -cp "$OUT/core:$OUT/tools" PackApk "$OUT/base.apk" "$OUT/unsigned.apk" "classes.dex=$OUT/dex/classes.dex"

echo "== 5/6 keystore =="
if [ ! -f build/tool.keystore ]; then
  mkdir -p build
  keytool -genkeypair -keystore build/tool.keystore -alias arcaeadarktool -keyalg RSA -keysize 2048 \
    -validity 10000 -storepass arcaeadark -keypass arcaeadark \
    -dname "CN=Arcaea Dark Android Tool, OU=Local, O=Local, C=CN" -storetype PKCS12
fi

echo "== 6/6 sign =="
apksigner sign --ks build/tool.keystore --ks-key-alias arcaeadarktool \
  --ks-pass pass:arcaeadark --key-pass pass:arcaeadark \
  --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true \
  --out out/ArcaeaDarkTool.apk "$OUT/unsigned.apk"

echo "== verify =="
apksigner verify --min-sdk-version 26 -v out/ArcaeaDarkTool.apk
ls -la out/ArcaeaDarkTool.apk
echo "DONE: $(pwd)/out/ArcaeaDarkTool.apk"
