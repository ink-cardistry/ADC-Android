# Arcaea 全暗测改包工具

把 Arcaea（包名 `moe.low.arc`）的 **光芒侧 / 消色侧 / 殸(Lephon)侧** 资源，替换为原包中 **纷争(对立)侧** 的资源，
再修改包名、重新签名，得到一个可以与原版共存的"全暗"测试包。

Windows 控制台工具，基于 .NET 10，可发布为**自带运行时、免安装的单文件 exe**。

> ⚠️ **免责声明**
> 仅限个人技术研究与学习安卓打包、AI图像识别使用。改包涉及 Lowiro 的商业利益，请勿用于任何商业用途。
> 使用本工具所产生的一切后果由使用者自行承担。
> **改包前请先云端同步存档**：改包使用自有密钥签名，若改包名失败需要卸载原版才能安装，本地存档会丢失。

---
## 功能

- 通过 **adb（有线 / 无线调试）** 连接手机，提取 `moe.low.arc` 的安装包（约 2 GB，带进度条）
- 自动分析 APK 并套用规则生成替换计划：当前版本可替换 **129 个**分侧资源（详见规则文档）
- 直接从 APK 的 `AndroidManifest.xml` 读出 `package` / `versionName` / `versionCode`，无需手工填写
- 修改包名（二进制 AXML 字符串池 + `resources.arsc` 内联包名），使改包与原版**共存**
- `zipalign` + `apksigner`（v1+v2+v3）自动签名，签名密钥首次运行自动生成
- **默认只导出 APK 文件**，拷到手机自行安装；也可选择 adb 直装（带推送进度条 + 提醒你在手机上点"允许安装"）
- 没有 adb 也能用：支持手动导入 APK 的**离线模式**
- 替换规则集中在 `rules/dark_rules.json`，**改规则不需要重新编译**

## 签名与升级（重要）

工具**内置一把公共签名密钥**（编译进 exe，首次签名时释放到 `keystore\arcaea-dark.keystore`），
所有用户拿到的签名完全一致。原因：

> Android 只允许「**同一包名 + 同一签名**」的安装包互相覆盖安装。
> 一旦签名变了，就必须先卸载旧版 —— 而卸载会清掉存档，以及 Arcaea **按需下载的曲目数据（GB 级）**，
> 升级时全部要重新下载。

所以本工具**不会每次生成随机密钥**。只要满足下面两条，升级就能直接覆盖安装、什么都不丢：

| 条件 | 默认值 | 说明 |
|---|---|---|
| 包名不变 | `moe.low.dark` | 在 `c) 查看 / 修改配置` 里改过就会变成另一个 App |
| 签名密钥不变 | 内置公共密钥 | 别删 `keystore\arcaea-dark.keystore`；删了也会自动从 exe 里重新释放出同一把 |

其他说明：

- **建议备份** `keystore\arcaea-dark.keystore`（2784 字节）。即使丢了，重新运行也会释放出同一把密钥。
- 工具在签名前会打印证书 SHA-256 指纹，方便你核对是否与上次一致：
  `86:D8:E8:E2:BD:7C:0D:62:C2:FF:52:EC:BF:F8:78:8A:D8:AC:33:C4:00:6B:D7:C8:CB:2D:88:CE:09:01:44:C2`
- ⚠️ **公共密钥是公开的**：本仓库里就带着它，任何人都能为 `moe.low.dark` 这个包签名。
  这是"所有用户能互相覆盖更新"必然的代价。想自己独占一把密钥，可在 `c) 配置` 里把
  *签名密钥* 指向自己的 `.keystore`（但那就只有和你同密钥的包才能互相覆盖了）。
- 如果你曾用 v1.1.0 或更早版本（当时是随机密钥）装过包，切换到 v1.2.0 需要**卸载重装一次**；
  之后就一直能覆盖升级了。

## 依赖

程序会自动查找以下工具，缺失时给出下载地址。仅"签名 / 安装 / 提取"环节需要它们：

| 工具 | 用途 | 下载 |
|---|---|---|
| adb（platform-tools） | 连接设备、提取 / 安装 APK | <https://developer.android.com/tools/releases/platform-tools> |
| Android build-tools | `zipalign` / `apksigner` | <https://developer.android.com/tools/releases/build-tools> |
| JDK | `keytool`（生成签名密钥） | <https://adoptium.net/> |

> 只做"导入本地 APK → 改包 → 导出"的话，没有 adb 也可以，只需要 build-tools + JDK。

## 快速上手

## 可以直接去release下载

### 方式一：构建单文件 exe（推荐分发方式）

```powershell
dotnet publish ArcaeaDarkApkCreator.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

产物 `dist\ArcaeaDarkApkCreator.exe`（约 36 MB）双击即用，不需要预装 .NET。

首次运行会在 **exe 同目录**自动生成：

- `config.json` —— 配置（包名、设备、导出目录、密钥口令等）
- `rules\dark_rules.json` —— 替换规则，可直接编辑
- `work\` —— 工作目录（提取的 APK、中间产物、keystore）

> 建议放在**可写目录**（如 D 盘自建文件夹），不要放 `C:\Program Files`。

### 方式二：从源码运行

```powershell
dotnet build -c Release
dotnet run -c Release
```

## 菜单

```
1) 检测环境                  检查 adb / build-tools / JDK，给出缺失项的下载地址
2) 查看 / 连接设备           有线设备、无线配对、无线连接、选择设备
3) 从设备提取 Arcaea 安装包   带进度条
4) 导入本地 APK（离线模式）   没有 adb 时使用
5) 分析并生成替换计划         输出：替换 N 项 / 跳过 M 项 / "可能遗漏"清单
6) 执行改包                  替换资源 + 修改包名 → work\arc-dark-unsigned.apk
7) 对齐并签名                → work\arc-dark-signed.apk
8) 导出改包 APK（不安装）     默认输出到桌面，文件名带版本，如 arcaea-dark-7.0.255c.apk
9) 安装到设备                需要 adb，会提醒你在手机上手动同意安装
a) 一键全流程                3/4 → 5 → 6 → 7 → 8 或 9（默认 8，不碰设备）
c) 查看 / 修改配置
0) 退出
```

## 文档

| 文档 | 内容 |
|---|---|
| [设计文档](docs/设计文档.md) | 逆向结论、技术选型、重打包 / 改包名 / 签名的实现要点、使用说明 |
| [资源替换规则](docs/资源替换规则.md) | 分侧命名规律、完整对照表、正则规则、已知跳过项、实测统计 |

## 目录结构

```
ArcaeaDarkApkCreator.csproj     项目文件（net10.0，无第三方依赖）
Program.cs                      入口
src/
  App.cs                        菜单 / 一键流程 / 编排
  AppConfig.cs                  配置模型与读写
  ConsoleUi.cs                  彩色输出、询问、进度条
  ToolLocator.cs                定位 adb / apksigner / zipalign / keytool
  ProcessRunner.cs              统一进程调用（含流式与百分比解析）
  AdbClient.cs                  设备管理 / 拉取 / 推送 / 安装
  ApkArchive.cs                 源 APK 只读封装
  RuleSet.cs                    规则加载 + 替换计划生成
  ApkRepacker.cs                重打包（保持原压缩方式）
  AxmlPatcher.cs                AndroidManifest.xml：包名替换 + 读取包名 / 版本
  ArscPatcher.cs                resources.arsc：包名替换
  ApkSigner.cs                  zipalign + apksigner + keytool
assets/app.ico                  程序图标
rules/dark_rules.json           替换规则（编译时作为嵌入式资源打进 exe）
docs/                           文档
```

## 实现要点（简要）

- **替换语义**：保留文件名、替换文件内容。游戏按固定的侧逻辑读取固定文件名，因此让某一侧"显示为纷争侧"就是把它对应文件的内容换成纷争侧文件的内容。
- **重打包**：`System.IO.Compression.ZipArchive` 流式逐条目复制，输出保持每个条目原先的压缩方式（`resources.arsc` 必须仍为 Stored，否则高版本 Android 安装失败）。
- **改包名**：AXML 后续节点只用字符串池下标引用字符串，因此重写字符串池 + 修正根块 `size` 即可；ARSC 的包名是 `ResTable_package` 里定长的 `char16_t name[128]`，原地覆写零风险。
- **验证**：包名补丁用 Google 官方 `aapt2 dump badging` 校验通过（`package: name='moe.low.dark' versionName='7.0.255c'`）。

## 已知限制

- **消色侧轨道**按需求映射到**风暴对立(Tempestissimo)侧**的 `track_tempestissimo.png`（v1.1.0）。
  消色侧的其它贴图（音弧粒子等）没有"风暴"版本，仍走默认纷争侧。
- `assets/img/bg_light.jpg`、`bg_colorless.jpg`（界面整屏背景）在原包中**没有** `bg_dark.jpg`，映射到同族 1280×960 的 `bg_byd_dark.jpg`。
- `Ether Strike` 的背景 `etherstrike` 是独立命名且**没有对立侧版本**，默认改用同曲包 `rei` 的纷争侧背景 `yugamu.jpg`；
  三个特效叠加层保持原样。依据见[规则文档 §3.7](docs/资源替换规则.md)。
- 少数资源（如 `track_critical_line_colorless.png`）原包确实没有暗侧版本，会被跳过并在分析报告里列出。
- **音弧贴图**（`arc_body.png` 等）与**打击特效粒子**（`note_sfx.png`）原包只有单版本，侧色由引擎按曲目染色，无法通过资源替换改变。
- 游戏大版本更新后资源命名可能变化：跑一次 `5) 分析`，按"可能遗漏"清单补 `rules/dark_rules.json` 即可，不需要改代码。

## 更新记录

### v1.2.1

- **修复"推送到设备卡住不动"**。根因有两个：
  - 无线调试的**端口会变**，`adb devices` 里会残留失效条目，对它下任何命令都会**永久挂起**。
    现在所有 adb 调用都带超时，并且**用之前先探测设备是否真的响应**，不响应就自动换下一个连接。
  - 推送原先用 `adb exec-in`，实测在本机会**静默失败**（文件根本没写进去）。改为官方 `adb push`，
    进度靠轮询远端文件大小得到，并带 45 秒"停滞"看门狗 —— 卡住会明确报错并给出排查建议，不再无限等待。
- **导出改用"移动"而不是"复制"**：签名包约 2GB，复制会让磁盘占用翻倍。移动后 `work\` 不再保留副本。
- 签名时自动删除中间产物 `aligned`（zipalign 的产物，签完即无用），峰值磁盘占用约从 6GB 降到 4GB。
- 修复 stdin 读完后主菜单空转刷屏的问题（仅在重定向输入时出现）。
- 一键流程里改为**先安装、后导出**（因为导出是移动）。

### v1.2.0

- **改用内置的公共签名密钥**（不再每次生成随机密钥）：
  Android 只允许同一包名 + 同一签名的包互相覆盖安装，签名一变就得先卸载，
  会连带清掉存档与按需下载的曲目数据。现在所有用户签名一致，升级可直接覆盖安装。
- 密钥文件固定在 exe 同目录的 `keystore\arcaea-dark.keystore`（不再放在 `work\` 里，避免被清理掉）
- 配置菜单新增「签名密钥」，可换成自己的密钥；签名前会打印证书 SHA-256 指纹便于核对
- 主界面显示当前使用的是内置公共密钥还是自定义密钥

### v1.1.0

- 修复特殊命名资源漏替换（此前导致部分曲目"看起来没变"）：
  - `assets/img/track_rei.png` —— `rei` 曲包专用的**亮白轨道**（含 Ether Strike），已映射到 `track_dark.png`
  - 由曲目数据的 `bg_inverse` 字段自动推导出的 **6 组特殊命名背景对**，例如 `rei → yugamu`（被 13+ 首曲目共用）、
    `lanota-light → lanota-conflict`、`tonesphere-solarsphere → tonesphere-darksphere`、`zettai_light → zettai`
- `Ether Strike` 的独立背景改用同曲包的纷争侧背景
- **消色侧轨道**改用风暴对立(Tempestissimo)侧贴图
- 主界面显示版本号；版本号不再拼接 git commit hash

### v1.0.0

- 首个版本：adb 提取/安装、资源替换、改包名、签名、导出

## 许可

[GPL-3.0](LICENSE)
