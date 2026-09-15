#!/usr/bin/env bash
# End-to-end validation of the repackaging core on a host JVM (no Android needed).
#
#   ANDROID_JAR=/path/to/android.jar ./jvmtest/run_test.sh
set -e
cd "$(dirname "$0")/.."

if [ -n "$PREFIX" ] && [ -d "$PREFIX/bin" ]; then export PATH="$PREFIX/bin:$PATH"; fi
if [ -n "$JAVA_HOME" ] && [ -d "$JAVA_HOME/bin" ]; then export PATH="$JAVA_HOME/bin:$PATH"; fi

AJ="${ANDROID_JAR:-/storage/emulated/0/MT2/mcp/sdk/platform35/android-35/android.jar}"
KS="${ARCAEA_KEYSTORE:-assets/arcaea-dark.keystore}"
T=jvmtest

compile() {
  local rc
  set +e
  javac "$@" 2>&1 | grep -v '^Note:'
  rc=${PIPESTATUS[0]}
  set -e
  if [ "$rc" -ne 0 ]; then echo "javac failed (exit $rc)"; exit 1; fi
}

rm -rf "$T/tbase" "$T/work"; mkdir -p "$T/tbase/assets/img/bg/1080" "$T/tbase/res/values"

cat > "$T/tbase/AndroidManifest.xml" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="moe.low.arc" android:versionCode="7" android:versionName="7.0.255c">
  <uses-sdk android:minSdkVersion="21" android:targetSdkVersion="35"/>
  <application android:label="ArcaeaTest"/>
</manifest>
EOF
cat > "$T/tbase/res/values/strings.xml" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<resources><string name="app_name">Arcaea Test</string></resources>
EOF
printf 'LIGHT'   > "$T/tbase/assets/img/note_light.png"
printf 'DARK'    > "$T/tbase/assets/img/note_dark.png"
printf 'BGLIGHT' > "$T/tbase/assets/img/bg/1080/song_light.png"
printf 'BGDARK'  > "$T/tbase/assets/img/bg/1080/song_conflict.png"

( cd "$T/tbase" \
  && mkdir -p resout \
  && aapt2 compile --dir res -o resout >/dev/null \
  && aapt2 link -o "../base.apk" --manifest AndroidManifest.xml -A assets -I "$AJ" $(find resout -name '*.flat') >/dev/null )

echo "== compile core + test harness =="
rm -rf build/tclasses build/ttest; mkdir -p build/tclasses build/ttest
compile -encoding UTF-8 -d build/tclasses -cp libs/apksig.jar \
  src/com/arcaeadark/tool/Bytes.java src/com/arcaeadark/tool/Json.java \
  src/com/arcaeadark/tool/AxmlPatcher.java src/com/arcaeadark/tool/ArscPatcher.java \
  src/com/arcaeadark/tool/Rules.java src/com/arcaeadark/tool/ZipReader.java \
  src/com/arcaeadark/tool/ZipWriter.java src/com/arcaeadark/tool/Signer.java \
  src/com/arcaeadark/tool/ApkPipeline.java
compile -encoding UTF-8 -d build/ttest -cp build/tclasses:libs/apksig.jar jvmtest/TestMain.java jvmtest/ProbeArsc.java

echo "== run pipeline =="
mkdir -p "$T/work"
java -cp build/tclasses:build/ttest:libs/apksig.jar TestMain "$T/base.apk" "$T/work" "$T/test_rules.json" "$KS" moe.low.dark

echo "== checks =="
S="$T/work/arc-dark-signed.apk"
apksigner verify --min-sdk-version 21 --max-sdk-version 35 -v "$S" | head -5
java -cp build/tclasses:build/ttest ProbeArsc "$S"
rm -rf "$T/verify"; mkdir -p "$T/verify"; (cd "$T/verify" && unzip -q -o "../work/arc-dark-signed.apk")
printf 'note_light.png = '; cat "$T/verify/assets/img/note_light.png"; echo
echo "PASS if package=moe.low.dark, signatures true and note_light.png=DARK"
