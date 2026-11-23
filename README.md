# BOTW Extractor

一个用于从 Nintendo Switch 游戏安装包（`NSP` / `NSZ`）中提取 `exefs`（可执行文件系统）与 `romfs`（只读资源文件系统）的工具集，面向《塞尔达传说：旷野之息》（The Legend of Zelda: Breath of the Wild）的**合法自有备份**整理与研究用途。

项目由两部分组成：

- **`Extractor.Core`**：基于 .NET 8 的跨平台提取核心库，封装 NSP/NSZ 解析、NCZ 解压、AES-CTR 解密与文件导出逻辑。
- **`BotwExtractor`**：基于 .NET 8 for Android（`net8.0-android`）的移动端应用，提供图形化提取流程，并内置暗黑模式、日志导出、主题色自定义、缓存清理等系统设置能力。

> 本仓库仅包含**源代码、工程配置与构建所需的开源依赖**，不包含任何游戏本体、受版权保护的资源、主机密钥或签名证书。详见下方[免责声明](#免责声明)。

---

## 功能特性

### 提取核心（Extractor.Core）

- 支持 `NSP`（PFS0 容器）与 `NSZ`（压缩 NCA）输入，自动识别并解压 `NCZ` 段。
- 支持本体（base）+ 更新补丁（update/patch）合并提取，输出标准 `exefs` / `romfs` 目录结构。
- 基于 LibHac 的 NCA 解析与 AES-CTR 解密，流式写出，支持进度回调与取消。
- 提供 PC 端验证入口（`PcVerify`）与命令行提取示例（`extractor`）。

### Android 应用（BotwExtractor，v1.4）

- **暗黑模式**：支持「跟随系统主题」自动适配与手动切换，切换带淡入淡出过渡动画；明暗两套配色均按 WCAG 2.x 对比度公式校验文字可读性。
- **日志导出**：设置区提供导出入口，流程为「时间范围选择 → 内容预览 → 生成标准 `.log` 文件」，导出内容包含完整运行上下文。
- **主题色自定义**：可视化取色面板，支持主色调 / 辅助色 / 强调色实时调整，配色方案本地持久化，可一键重置为默认值，调整即时全局生效。
- **缓存清理**：清理前展示缓存总占用与可清理项明细的二次确认弹窗；完成后反馈释放空间大小；任务运行中的临时目录受保护、不被清理，日志窗口保持固定尺寸不受影响。
- 上述功能均适配桌面 / 移动端响应式布局，并配有单元测试与集成测试。

---

## 免责声明

**请在下载、构建、使用本项目前仔细阅读本章节。一旦使用本项目，即视为你已理解并接受以下全部条款。**

### 1. 合法使用与风险提示

- 本项目仅供**学习、研究以及对你本人合法拥有的游戏备份进行整理**之用。你必须在所在司法辖区法律允许的范围内使用本工具。
- 提取所需的密钥文件（`prod.keys`、`title.keys` 等）**必须由你从自己合法购买的主机中自行导出**。本仓库**不提供、不分发**任何密钥、游戏本体、受版权保护的资源或下载链接。
- 严禁将本工具用于盗版、侵犯知识产权或任何违反任天堂用户协议 / 服务条款的行为。由此产生的一切法律后果由使用者自行承担。
- 本项目与 Nintendo（任天堂）无任何关联，未获得其授权、赞助或背书。「The Legend of Zelda」「Breath of the Wild」及相关商标、版权均归 Nintendo 所有。仓库名称中涉及的游戏名称仅为描述用途。

### 2. 责任界定

- 本软件按「**现状（AS IS）**」提供，**不附带任何明示或暗示的担保**，包括但不限于对适销性、特定用途适用性、不侵权的担保。
- 在任何情况下，作者或贡献者均**不对因使用或无法使用本软件而产生的任何直接、间接、偶然、特殊、惩罚性或后果性损害**（包括但不限于数据丢失、设备损坏、存档损坏、业务中断、法律纠纷）承担责任，无论其基于合同、侵权（含过失）或其他法律理论，也无论是否已被告知此类损害的可能性。
- 提取与缓存清理操作会**读写大量文件并删除缓存目录**，存在数据丢失风险。请在操作前自行备份重要数据，并确认输出 / 清理路径正确。使用者需自行评估并承担全部操作风险。

### 3. 安全提示

- 仓库中的 Android 签名配置（`BotwExtractor.csproj` 内的 `AndroidSigning*` 字段）仅为**开发 / 调试示例**，对应的签名证书（`*.keystore`）**已被排除在仓库之外**。正式发布前请务必生成并使用你自己的私有签名证书，切勿使用任何公开或示例密钥进行生产分发。
- 请勿将你的主机密钥、签名证书、个人凭据提交到任何公共仓库。本项目 `.gitignore` 已默认排除 `*.keys`、`*.keystore`、`*.jks` 等敏感文件。
- 工程文件中出现的本机绝对路径（如 SDK / JDK / 密钥路径）为开发环境示例，克隆后需按你自己的环境修改，详见[构建与运行](#构建与运行)。

### 4. 第三方组件许可

- 本项目依赖 [LibHac](https://github.com/Thealexbarney/LibHac)（LGPL-3.0）与 [ZstdSharp.Port](https://github.com/oleg-sta/ZstdSharp)（MIT）等开源组件，相关权利归各自作者所有。仓库内附带的 `LibHac.dll` 仅为构建便利而提供，其使用须遵循 LGPL-3.0 许可。
- 使用本项目即表示你同意同时遵守上述第三方组件的许可条款。

---

## 项目结构说明

```text
.
├── README.md                     # 项目说明、免责声明与结构文档（本文件）
├── .gitignore                    # 忽略 SDK / 构建产物 / 密钥 / 签名证书等
│
├── core/                         # 跨平台提取核心（.NET 8）
│   ├── Extractor.Core/           # 提取核心类库
│   │   ├── Extractor.Core.csproj # 类库工程文件（引用 LibHac / ZstdSharp）
│   │   ├── GameExtractor.cs      # 提取主流程：解析输入→解密→导出 exefs/romfs，含 ExtractOptions
│   │   ├── NspHelper.cs          # NSP(PFS0) 容器解析，提取 Program NCA，识别 NCZ
│   │   ├── NszDecompressor.cs    # NSZ/NCZ（压缩 NCA）解压还原为标准 NCA
│   │   ├── AesCtr.cs             # 基于 AES-ECB 实现的 AES-CTR 解密器
│   │   ├── IFileAsStream.cs      # 将 LibHac IFile 适配为只读 .NET Stream
│   │   └── Progress.cs           # 进度模型 ExtractProgress 与上报器 ProgressReporter
│   └── PcVerify/                 # PC 端验证入口（调用核心库跑通完整提取）
│       ├── PcVerify.csproj
│       └── Program.cs
│
├── extractor/                    # 命令行提取示例（直接基于 LibHac 的 NCA 提取）
│   ├── extractor.csproj
│   └── Program.cs
│
├── android/                      # Android 应用与测试
│   ├── BotwExtractor/            # .NET 8 for Android 应用（com.botw.extractor）
│   │   ├── BotwExtractor.csproj  # 应用工程文件（版本、签名、依赖、SDK 路径）
│   │   ├── MainActivity.cs       # 主界面：文件选择、提取流程、固定日志窗口、设置入口
│   │   ├── SettingsActivity.cs   # 设置界面：暗黑模式 / 日志导出 / 主题色 / 缓存清理四区块
│   │   ├── ThemeManager.cs       # 主题管理：明暗切换、实时着色、过渡动画
│   │   ├── Ui/                   # 与 Android 解耦的纯逻辑层（可被测试工程共享）
│   │   │   ├── AppState.cs       # 跨 Activity 运行态（提取中标志、共享日志库）
│   │   │   ├── ColorMath.cs      # WCAG 亮度 / 对比度计算与颜色工具
│   │   │   ├── LogStore.cs       # 线程安全的运行日志环形存储（导出完整历史）
│   │   │   ├── CacheScanner.cs   # 缓存扫描 / 明细 / 受保护项 / 清理逻辑
│   │   │   └── ThemePrefs.cs     # 主题偏好与配色持久化（ISettingsStore 抽象）
│   │   ├── Properties/
│   │   │   └── AndroidManifest.xml  # 应用清单：包名、权限、Activity 声明
│   │   └── Resources/            # Android 资源
│   │       ├── layout/           # 界面布局（主界面 / 设置 / 取色弹窗 / 日志预览弹窗）
│   │       ├── values/           # 默认 colors / dimens / themes
│   │       ├── values-night/     # 暗黑模式配色
│   │       ├── values-sw600dp/   # 平板 / 大屏尺寸适配
│   │       ├── anim/             # 淡入 / 淡出过渡动画
│   │       ├── menu/             # 主菜单
│   │       └── drawable/         # 图标等图形资源
│   └── BotwExtractor.Tests/      # xUnit 单元 + 集成测试（net8.0，共享 Ui 纯逻辑）
│       ├── BotwExtractor.Tests.csproj
│       ├── ColorMathTests.cs     # 颜色 / 对比度测试
│       ├── LogStoreTests.cs      # 日志存储 / 导出测试
│       ├── CacheScannerTests.cs  # 缓存扫描 / 清理 / 保护测试
│       ├── ThemePrefsTests.cs    # 主题持久化 / 重置测试
│       └── IntegrationTests.cs   # 跨功能集成测试
│
└── tools/                        # 构建所需第三方依赖
    └── libhac_pkg/
        ├── LibHac.nuspec         # LibHac 包元数据
        └── lib/net7.0/
            ├── LibHac.dll        # LibHac 程序集（被各工程以 HintPath 引用）
            └── LibHac.xml        # LibHac API 文档
```

> 说明：`core`、`android`、`tools` 三者通过工程文件中的相对路径（`..\..\core`、`..\..\tools\libhac_pkg`）相互引用，克隆时请保持完整目录结构，否则将无法构建。

---

## 构建与运行

### 环境要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 构建 Android 应用还需：.NET Android 工作负载（`dotnet workload install android`）、JDK 17、Android SDK（含对应 API 平台与构建工具）。
- 运行提取功能需自备：从自有主机导出的 `prod.keys` / `title.keys`，以及合法拥有的游戏 `NSP` / `NSZ` 文件。

### 准备本机环境

工程文件中包含开发机绝对路径（`JavaSdkDirectory`、`AndroidSdkDirectory`、`AndroidSigningKeyStore` 及示例程序中的输入 / 密钥路径）。克隆后请按你自己的环境修改这些路径，或通过环境变量 / 命令行参数覆盖。

- **签名证书**：仓库不含 `*.keystore`。Release 构建前请生成你自己的证书，例如：

  ```bash
  keytool -genkeypair -v -keystore my.keystore -alias myalias \
    -keyalg RSA -keysize 2048 -validity 10000
  ```

  随后更新 `BotwExtractor.csproj` 中的 `AndroidSigningKeyStore` / `AndroidSigningKeyAlias` / `AndroidSigningStorePass` / `AndroidSigningKeyPass`。

- **密钥文件**：将自行导出的 `prod.keys` / `title.keys` 放到本机安全位置，并在运行入口中指向它们；切勿提交到仓库。

### 构建与测试命令

```bash
# 还原并构建提取核心库
dotnet build core/Extractor.Core/Extractor.Core.csproj -c Release

# 运行单元 + 集成测试（无需 Android SDK / 密钥 / 签名证书）
dotnet test android/BotwExtractor.Tests/BotwExtractor.Tests.csproj -c Release

# 构建 Android 应用（需已配置 Android 工作负载 / JDK / SDK / 签名证书）
dotnet build android/BotwExtractor/BotwExtractor.csproj -c Release
```

> 在本项目的开发环境中，上述命令通过本地工具链执行（`dotnet` 位于 `E:\botw_work\dotnet\dotnet.exe`，并设置 `NUGET_PACKAGES`、`DOTNET_CLI_HOME`、`JAVA_HOME`、`ANDROID_HOME` 等环境变量）。这些工具链目录已被 `.gitignore` 排除，不随仓库分发。

---

## 测试说明

测试工程 `BotwExtractor.Tests` 基于 **xUnit**，通过 `Compile Include` 共享应用中的 `Ui/*.cs` 纯逻辑层，从而在**不依赖 Android 运行时**的情况下验证核心行为。覆盖范围包括：

- **正常流程**：颜色对比度计算、日志写入与导出、缓存扫描与清理、主题持久化。
- **边界场景**：空日志、容量上限、非法颜色值、空缓存目录。
- **异常 / 保护场景**：任务运行中临时目录受保护不被清理、重置默认值、跨功能集成一致性。

运行 `dotnet test android/BotwExtractor.Tests/BotwExtractor.Tests.csproj -c Release` 即可执行全部测试。

---

## 许可与致谢

- 本项目源代码的使用请遵循仓库后续补充的许可声明；在未明确许可前，默认保留所有权利。
- 提取能力建立在 [LibHac](https://github.com/Thealexbarney/LibHac)（LGPL-3.0）之上，NSZ 解压参考了 [nsz](https://github.com/nicoboss/nsz) 项目，压缩由 [ZstdSharp.Port](https://github.com/oleg-sta/ZstdSharp)（MIT）提供。感谢上述开源项目及其作者。