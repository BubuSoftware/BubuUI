# BubuUI for Visual Studio

Visual Studio 的 VSIX 客户端。`.bui` / `.bss` 复用仓库现有的
`BubuUI.LanguageServer`、TextMate grammar 与 language configuration。
模板分析和 C# 语义仍由共享服务器负责。

0.1.1 修复 VS 按属性名首字符查询时，扫描器将该位置误判为空白、导致 Ctrl+鼠标
导航和悬停没有结果的问题。已安装 0.1.0 时，需要安装新版 VSIX 并重启 VS。
同时修复组件参数声明在其他 `.bui` 的 `@code` 中时，定义坐标回映不到目标模板的问题。

## 安装

支持 Windows x64，Visual Studio 2022 **17.14+** / Visual Studio 2026。
本机需要 **.NET SDK 11 或更高版本**，用于运行服务器、求值项目及读取项目引用。
插件支持 VS 2022 编辑文件；BubuUI 框架本身目标为 .NET 11，
在 VS 内构建这类项目需要 VS 2026。

1. 运行下述构建命令，得到 `bin/Release/net472/BubuUI.VisualStudio.vsix`。
2. 双击 VSIX 安装，按安装器提示关闭并重启 Visual Studio。
3. 打开 BubuUI 解决方案或项目文件夹，先执行 `dotnet restore`。
4. 打开项目 `AdditionalFiles` 中的 `.bui` 或 `.bss`，客户端自动启动。

安装包内含语言服务器和独立文档所需的 Runtime / Rendering 元数据程序集，
不需要设置服务器路径，也不依赖 VS Code 插件目录。

## 构建和验证

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ide/visualstudio/build.ps1
```

脚本发布共享服务器、构建元数据程序集、运行客户端测试、调用 Visual Studio
MSBuild 生成 VSIX，最后打开 ZIP 检查必需文件、高亮语法一致性和宿主依赖隔离。
需要安装 Visual Studio 的 .NET 桌面开发工作负载；VSSDK 构建工具由 NuGet 还原。
另外需要 .NET 8.0.4xx SDK：本目录的 `global.json` 为 `net472` 客户端选择 .NET 8，
以兼容 VS 2022 MSBuild。共享服务器发布和测试仍使用仓库根目录指定的 .NET 11 SDK。

```powershell
# 只重建客户端（server/ 已由首次构建生成）
powershell -NoProfile -ExecutionPolicy Bypass -File ide/visualstudio/build.ps1 -SkipPublish

# 单独运行客户端行为测试，不需要启动 VS
dotnet test tests/BubuUI.VisualStudio.Tests -c Release

# 共享服务器的协议会话测试
dotnet test tests/BubuUI.Ide.Tests -c Release --filter FullyQualifiedName~ProtocolTests

# 单独验证现有安装包
powershell -NoProfile -ExecutionPolicy Bypass -File ide/visualstudio/verify-vsix.ps1 `
  -Path ide/visualstudio/bin/Release/net472/BubuUI.VisualStudio.vsix
```

VSIX 工程使用 .NET Framework 4.7.2 和完整 Visual Studio MSBuild，独立于根解决方案构建，
避免普通 `dotnet build BubuUI.slnx` 触发仅支持完整 MSBuild 的打包任务。
客户端行为测试已加入根解决方案。

调试时在 VS 中打开 `BubuUI.VisualStudio.csproj`，将 `DeployExtension` 设为 `true`，
F5 启动 `/RootSuffix Exp` 实验实例。普通构建默认不自动安装插件。

## 能力与接线

| 能力 | 实现 |
|---|---|
| 文件识别及自动启动 | MEF 注册 `bubuui` / `bss`，继承 `code-remote` |
| 模板、样式及内嵌 C# 词法高亮 | 直接打包 `ide/vscode/syntaxes` 的共享 grammar |
| 注释、括号及自动闭合 | 共享 language configuration，按 grammar 和 content type 注册 |
| 补全、悬停、诊断、签名提示 | VS 内置 LSP 客户端 + 共享服务器 |
| 定义、重命名、代码操作、格式化、大纲 | 共享服务器声明的标准 LSP 能力，交给 VS 呈现 |
| DLL 类型的 F12 | 自定义请求取得反编译文本，转为本地只读 `.cs` 文件 |
| 保存后刷新项目目录 | `FilesToWatch` 加解决方案模式的递归 watcher；批量合并事件，并处理重命名前后路径 |
| 切换解决方案 | 串行重启服务器，重新传入工作区 |
| 故障诊断 | 初始化失败通知、独立日志、stderr 持续排空及断线清理进程 |

反编译文件位于 `%LOCALAPPDATA%\BubuUI\VisualStudio\decompiled`，
文件名由 URI 与内容的 SHA-256 生成，不直接将虚拟 URI 当磁盘路径。
生成的文件为只读，内容更新会得到新的缓存文件。

## 可选配置

通过环境变量配置，在启动 Visual Studio 前设置，修改后重启 VS：

| 变量 | 默认与作用 |
|---|---|
| `BUBUUI_SERVER_DLL` | 默认使用 VSIX 内的 `server/BubuUI.LanguageServer.dll`；也可指定开发版 DLL 的绝对路径 |
| `BUBUUI_DOTNET` | 默认 `dotnet`，可设为 `dotnet.exe` 的完整路径 |
| `BUBUUI_RUNTIME` | 默认服务器自动查找；可指定 Runtime DLL 或所在目录 |
| `BUBUUI_CATALOG_ASSEMBLIES` | 额外元数据程序集的完整路径，用分号分隔 |

指定了不存在的服务器路径时明确报错，不会悄悄回退到其他版本。
无显式配置时，先找内嵌服务器，再从工作区及扩展目录向上探测仓库的
`ide/BubuUI.LanguageServer/bin/{Release,Debug}/net11.0`。

日志位于 `%LOCALAPPDATA%\BubuUI\VisualStudio\logs\server-*.log`。
有高亮但无补全时，先检查日志中的项目求值结果、文件所属项目和 restore 提示；
不在项目 `AdditionalFiles` 中的文件遵循共享服务器的归属规则。

## 验证范围

已验证：VS MSBuild 实际编译和 VSIX 打包；包内共享资产及服务器完整性；
Windows 原生参数解析、路径探测、工作区初始化、真实 JSON-RPC 反编译导航、
只读缓存、实际文件创建与重命名通知，以及共享 LSP 协议测试。
发布后的独立服务器还通过真实 stdio 会话验证初始化、打开文档、从内嵌 Runtime
元数据补全 `Button`、属性名首字符处的悬停范围与定义、反编译内容和正常退出。

0.1.1 本次验证：24 项客户端测试、317 项 IDE 串行回归及发布后真实进程会话通过。
完整 IDE 默认并行回归曾出现 1 项 `MirrorDecompileTests` 失败；该用例单独重跑及
全套串行运行均通过，尚未修改共享反编译器的并发行为。串行复核命令：

```powershell
dotnet test tests/BubuUI.Ide.Tests -c Release -- xUnit.ParallelizeTestCollections=false
```

尚未在 VS 界面做交互冒烟。安装后应检查 `.bui` / `.bss` 高亮、Ctrl+Space、
悬停、错误列表、F12、重命名及格式化。VS 2022 与 VS 2026 的界面能力可能存在差异。
未接管 `.cs` 未保存缓冲区，也未新增调试器、设计器或项目模板；
内嵌 C# 的语义着色取决于 VS 对 LSP semantic tokens 的支持。

接口依据：微软的 [LSP 扩展指南](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension?view=vs-2022)、
[Language Configuration](https://learn.microsoft.com/en-us/visualstudio/extensibility/language-configuration)、
[VS 扩展兼容性说明](https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/extension-compatibility?view=visualstudio)。
