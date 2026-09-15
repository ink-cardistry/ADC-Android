[![build](https://github.com/ink-cardistry/ADC-Android/actions/workflows/build.yml/badge.svg)](https://github.com/ink-cardistry/ADC-Android/actions/workflows/build.yml)

# Arcaea 全暗改包工具 · Android 本地版

把 [335589054/ADC](https://github.com/335589054/ADC)（Windows / .NET 10 控制台工具）改写成一个
**可直接安装在 Android 手机上、完全本机运行**的 APK。

与原版最大的区别：

- **不再使用 adb 提取安装包**。原 APK 由用户在应用内**手动导入**（系统文件选择器 / SAF）。
  应用不连接、也不需要任何设备调试通道。
- 处理逻辑全部用纯 Java 重写并打包进 APK，**不依赖 adb / JDK / build-tools / .NET 运行时**。
- 对齐（zipalign）与签名（v1+v2+v3）直接在应用内完成，签名引擎内嵌 Google 的
  [apksig](https://maven.google.com/web/index.html#com.android.tools.build:apksig) 库。
- 生成结果可直接在本机**安装**（PackageInstaller）或**导出**到任意位置（SAF）。

## 功能

1. 手动导入原版 Arcaea APK（约 2 GB）——**通过文件选择器，不用 adb**。
2. 按 `assets/dark_rules.json` 规则生成替换计划：把 光芒侧 / 消色侧 / 殸(Lephon)侧 资源
   替换为原包中 纷争(对立)侧 的内容（精确对照 + 正则族，规则可改，无需改代码）。
3. 修改包名：二进制 `AndroidManifest.xml` 字符串池 + `resources.arsc` 内联包名，
   使改包与原版**共存**。
4. 一次性重打包并**对齐**（未压缩条目 4 字节、`.so` 16 KB 页对齐，单遍流式写出）。
5. 用内置的**公共签名密钥**签名（v1 + v2 + v3），保证升级时可覆盖安装、不丢存档。
6. 本机安装或导出成品 APK。

> ⚠️ 免责声明：仅限个人技术研究与学习安卓打包使用。改包涉及 Lowiro 的商业利益，请勿商用。
> 改包前请先云端同步存档；改包使用自有密钥签名，若包名失败需卸载原版才能安装，本地存档会丢失。

## 目录结构

```
ADC-Android/
├── AndroidManifest.xml          应用清单（minSdk 26 / target 35）
├── assets/
│   ├── dark_rules.json          替换规则（与原仓库一致）
│   └── arcaea-dark.keystore     内置公共签名密钥（PKCS12，口令 arcaeadark）
├── libs/apksig.jar              Google apksig 签名库（内嵌）
├── src/com/arcaeadark/tool/
│   ├── Bytes.java               little-endian 字节工具
│   ├── Json.java                零依赖 JSON 读写
│   ├── AxmlPatcher.java         二进制 AndroidManifest.xml 包名替换
│   ├── ArscPatcher.java         resources.arsc 包名替换
│   ├── Rules.java               规则解析 + 替换计划（对应原 RuleSet.cs）
│   ├── ZipReader.java           基于 FileChannel 的随机访问 ZIP 读取（可不复制源文件）
│   ├── ZipWriter.java           单遍流式写出、自带 zipalign 的 ZIP 写入
│   ├── Signer.java              apksig 封装 + keystore 加载
│   ├── ApkPipeline.java         编排：分析→改包→对齐→签名（无 Android 依赖）
│   ├── Job.java                 后台任务与 UI 桥接
│   ├── MainActivity.java        单屏 UI（导入 / 改包 / 安装 / 导出）
│   ├── KeepAliveService.java    前台服务，避免大文件处理被系统杀掉
│   └── InstallReceiver.java     PackageInstaller 结果回调
├── jvmtest/                     宿主 JVM 端到端测试（合成 APK）
└── build.sh                     在设备上用 Termux 工具链构建 APK
```

## 构建（在手机上完成）

需要 Termux 工具链：`openjdk-17`、`aapt2`、`d8`、`apksigner`，以及一份 `android.jar`
（如 `platforms/android-35/android.jar`）。

```bash
# 依赖（若 apt 因前缀问题装不上，可直接 dpkg -i 缓存里的 .deb 后再合并）
pkg install openjdk-17 aapt2 d8 apksigner

# android.jar
curl -L -o android-platform-35.zip https://dl.google.com/android/repository/platform-35_r02.zip
unzip android-platform-35.zip -d sdk   # -> sdk/android-35/android.jar

# apksig
curl -L -o libs/apksig.jar https://dl.google.com/dl/android/maven2/com/android/tools/build/apksig/8.13.2/apksig-8.13.2.jar

ANDROID_JAR=$PWD/sdk/android-35/android.jar bash build.sh
# 产物：out/ArcaeaDarkTool.apk（仓库根目录另附一份已构建好的 ArcaeaDarkTool.apk）
```

## 使用

1. 安装并打开 **Arcaea 全暗改包**。
2. 点「1. 手动导入 APK」，在系统文件选择器中选中 Arcaea 的原版 APK（`moe.low.arc`）。
   - 应用只读取所选文档，**不会复制整包**（除非该文档不可随机读取，此时才会先复制一份）。
3. 确认新包名（默认 `moe.low.dark`，可与原版共存）。
4. 点「3. 开始改包」。处理约 2 GB，期间请保持应用在前台（已启用前台服务保活）。
5. 完成后可：
   - **安装**：走系统 PackageInstaller，按提示允许「安装未知应用」。
   - **导出**：保存到任意目录，自行安装或备份。

输出默认位于应用专属目录：`Android/data/com.arcaeadark.tool/files/work/arc-dark-signed.apk`。

## 签名与升级

应用内置与原项目**完全相同**的公共密钥，因此只要包名（`moe.low.dark`）不变，
新版本可以直接覆盖安装，**存档与已下载曲目数据都会保留**。
证书 SHA-256 指纹与上游一致：

```
86:D8:E8:E2:BD:7C:0D:62:C2:FF:52:EC:BF:F8:78:8A:D8:AC:33:C4:00:6B:D7:C8:CB:2D:88:CE:09:01:44:C2
```

## 与上游实现的对应关系

| 上游 (.NET) | 本移植 (Java) | 说明 |
|---|---|---|
| `AxmlPatcher.cs` | `AxmlPatcher.java` | 重写字符串池并拼接，逻辑逐行对齐 |
| `ArscPatcher.cs` | `ArscPatcher.java` | 重写 `ResTable_package` 的 `name[128]` |
| `RuleSet.cs` | `Rules.java` | 精确对照 + 正则族 + 未处理项统计 |
| `ApkRepacker.cs` | `ZipWriter.java` | 单遍写出，同时完成对齐；未改动条目 **原样拷贝压缩数据** |
| `ApkSigner.cs`（外部 apksigner/zipalign/keytool） | `Signer.java` + `ZipWriter` | 内嵌 apksig，v1+v2+v3；对齐由写入器完成 |
| `AdbClient.cs` | — | **删除**：改为手动导入，不再使用 adb |

## 已验证

在合成 APK 上端到端验证（`jvmtest/run_test.sh`）：

- 包名在 `AndroidManifest.xml` 与 `resources.arsc` 中均已替换为 `moe.low.dark`；
- 精确对照与正则族替换均生效（`note_light.png` 内容变为纷争侧内容）；
- 未压缩条目数据偏移均 4 字节对齐；
- `apksigner verify` 通过 v1 / v2 / v3 三种签名方案；
- 源 APK 已带原厂签名时，旧的 `META-INF/*.SF|*.RSA` 会被剥离，只保留本工具的新签名；
- 成品证书 SHA-256 与上游公共密钥一致。


## 上游来源与许可

本项目是 [335589054/ADC](https://github.com/335589054/ADC) 的 Android 移植版，属于**衍生作品**。

- 上游：https://github.com/335589054/ADC —— *Arcaea 全暗测改包工具*（.NET 10 控制台程序）
- 上游许可证：**GNU GPL v3**（注意：不是 MIT）
- 本仓库许可证：**GNU GPL v3**，见 [LICENSE](LICENSE)
- 上游源码快照完整收录于 [third_party/upstream-ADC/](third_party/upstream-ADC/)（未修改），用于满足 GPL 的源码提供与署名要求
- 内置的替换规则 assets/dark_rules.json 与公共签名密钥 assets/arcaea-dark.keystore 均来自上游

移植部分（src/、AndroidManifest.xml、build.sh、jvmtest/）由本仓库重新实现，同样以 GPL-3.0 分发。

## 自动构建（GitHub Actions）

[.github/workflows/build.yml](.github/workflows/build.yml) 在每次 push / PR 时自动：

1. 安装 JDK 17 + Android SDK（platform 35 / build-tools 35.0.0）
2. 执行 build.sh（aapt2 → javac → d8 → 打包 → 签名）
3. 运行 jvmtest/run_test.sh 核心回归测试
4. 上传产物 ArcaeaDarkTool-apk（out/ArcaeaDarkTool.apk）

打 v* 标签也会触发构建，可在 Actions 页面下载 artifact。
