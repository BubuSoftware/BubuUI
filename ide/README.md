# BubuUI IDE 支持（design/06 的落地）

这里放的是 design/06 的共享语言服务与三个 IDE 客户端：

```
ide/
  BubuUI.LanguageServer/   LSP server（C#/.NET 11，stdio）——全部 IDE 能力都在这里
    Workspace/                L1–L4：MSBuild 求值 / 精确引用 / 项目 Compilation / 真生成器
    Analysis/                 诊断 / 补全 / 悬停 / 大纲 / 跳转 / 重命名 / 内嵌 C# 语言服务
    Server/                   JSON-RPC 协议层（声明能力、翻译 LSP 模型）
  vscode/                     VS Code 壳：TextMate grammar + 语言配置 + 片段 + 极薄的 LSP 客户端
  rider/                      Rider 壳：lsp4ij 插件（Language/FileType + TextMate bundle + server 启动命令）
  visualstudio/               Visual Studio 2022 17.14+ / 2026 VSIX：MEF + LSP，内嵌共享 server
  build.ps1                   发布 server 到扩展目录 + 装依赖 + 跑 grammar/两个壳的断言
```

Rider 走 lsp4ij、Visual Studio 走内置 LSP 客户端接**同一个** server，所以补全/诊断共用实现，
差别只在壳的接线方式。

0.2.5 的三个客户端共用 `vscode/highlighting/palette.json`：浅色主题下，
标签/类型/命名空间 `#267F99`、属性 `#0451A5`、指令 `#AF00DB`、
字面量 `#A31515`、注释 `#008000`、标点 `#666666`，同时提供深色配色。
模板配色与插件一起保存，只应用到 `.bui` / `.bss`；`@code` 和表达式继续使用 C# 高亮。
VS Code 使用模板 token 的前景色装饰，Rider 在 TextMate 高亮器上选择独立颜色键，
VS 使用可在“字体和颜色”中调整的 `BubuUI …` 分类。

## 三个 IDE 插件一键打包

### GitHub Actions（推荐）

提交工作流到 GitHub 默认分支后，打开 **Actions → Package IDE plugins → Run workflow**，
选择要构建的分支并运行。默认执行测试；仅在已验证测试时勾选 `skip_tests`。
工作流自动准备 .NET 11 / .NET 8 SDK、Node.js 和 JDK 21，并在 Windows 上构建三个插件。
共享语言服务器使用 .NET 11；Visual Studio 的 `net472` 客户端单独使用 .NET 8 SDK，
以兼容 Windows runner 的 Visual Studio 2022 MSBuild。

成功后，在该次运行页面的 **Artifacts** 分别下载 `BubuUI-VSCode-<版本>`、
`BubuUI-VisualStudio-<版本>` 或 `BubuUI-Rider-<版本>`；
校验文件单独放在 `BubuUI-SHA256SUMS-<版本>`，均保存 30 天。
GitHub Artifacts 下载时会附加 ZIP 外包装，解压后取出对应的 VSIX 或 Rider 插件 ZIP 安装。
打包和校验成功后，还会自动创建或更新 GitHub Release，附件包含三个安装包和 `SHA256SUMS.txt`。

| 发布类型 | Release 名称 | Tag 示例 |
|---|---|---|
| IDE 插件 | `BubuUI IDE <版本>` | `ide-v0.2.7` |
| 框架 NuGet 包 | `BubuUI <版本>` | `v0.0.1` |

IDE Release 使用三个插件统一的版本，tag 指向本次工作流的构建提交。`ide-v` 前缀不参与框架 MinVer 版本推导；IDE Release 不设为 Latest，保留框架的 Latest 发布标记。
发布使用内置 `GITHUB_TOKEN`，无需另设发布令牌。同版本重跑可更新附件；发布新版本前需同步更新三个插件的版本。
此流程发布 GitHub Release，插件市场仍需单独发布。本地执行 `publish.ps1` 仍只生成安装包。
工作流文件：[ide-release.yml](../.github/workflows/ide-release.yml)。

### 本地运行

在 Windows 上安装仓库 `global.json` 指定的 .NET 11 SDK、.NET 8.0.4xx SDK、Node.js/npm、JDK 21–24
（推荐 21，设置 `JAVA_HOME`），以及 Visual Studio 2022 17.14+ / 2026 的
“.NET 桌面开发”工作负载。首次构建需要联网下载 npm、NuGet、Gradle 和 Rider SDK 依赖。

在仓库根目录执行：

```powershell
pwsh -File ./publish.ps1 -CheckOnly # 仅检查环境和三个插件的版本
pwsh -File ./publish.ps1            # 测试、构建并生成三个安装包
pwsh -File ./publish.ps1 -SkipTests -SkipNpm # 已验证测试且依赖已安装时
```

也支持 Windows PowerShell 5.1：`powershell -ExecutionPolicy Bypass -File ./publish.ps1`。
脚本按自身位置定位仓库，可以从其他工作目录调用；任一步失败都会终止并报错。
`-SkipTests` 跳过测试和 stdio 冒烟，但仍检查安装包结构、版本及共享资源一致性。
更新版本时需要同步 `vscode/package.json`（及 npm 锁文件）、
`visualstudio/source.extension.vsixmanifest` 和 `rider/gradle.properties`。

输出目录为 `out/ide-release/<版本>/`，包含：

- `BubuUI-VSCode-<版本>.vsix`
- `BubuUI-VisualStudio-<版本>.vsix`
- `BubuUI-Rider-<版本>.zip`
- `SHA256SUMS.txt`

底层打包入口仍可直接调用：

```powershell
pwsh -File ide/package.ps1
```

产物统一放在 `out/ide-release/0.2.0/`：VS Code VSIX、Visual Studio VSIX、Rider ZIP 和
`SHA256SUMS.txt`。脚本运行共享语言服务测试、客户端测试、真实 stdio 进程验证，
并检查三个包里的服务器依赖逐文件 SHA-256 一致、语法文件一致、版本一致。
只生成本地包，不安装插件，也不上传商店。已经验证测试时可用 `-SkipTests`；依赖已安装可用 `-SkipNpm`。

三个插件都携带同一版语言服务器和元数据目录，目标机器需要 .NET 11 SDK；Rider 还需要
LSP4IJ 和 TextMate 插件。VS Code 使用“Install from VSIX”，Rider 使用“Install Plugin from Disk”，
Visual Studio 双击对应 VSIX 安装。

| 编译器已实现的语法 | 三个 IDE 共用的支持 |
|---|---|
| `.bss` 旧类规则、子级/后代选择器、组合伪类 | 高亮、属性/状态/类型补全、属性悬停和定义、编译器诊断、格式化 |
| `$variable` | 声明顺序和单样式块作用域内补全、悬停、跳转、重命名、大纲；正确处理重新赋值 |
| `@states` | 本项目样式来源的状态补全、悬停、定义与重命名；保留内置状态 |
| `.bui` 的 `@style` | 上述旧样式能力，宿主源码坐标保持不变；未闭合块仍可补全 |
| `Style="..."` | 目录驱动的属性/枚举/`theme()` 补全、悬停、定义、参数帮助、拼写修复和高亮 |
| `Class="..."` | 未保存的本项目样式类补全和定义，样式声明与 UI 字面量引用的重命名；组件局部类优先 |
| `@using`、`@theme ... for ...`、类型化 setter | 导入、按类型补全、悬停、定义、编译器诊断、符号、语义着色及格式化 |
| `template()`、`resource()`、`theme()`、`/template/` | 类型过滤、函数参数帮助、模板/主题/部件导航；主题重命名同步可绑定的 C# 引用 |
| `@template`、`Owner`、`Part`、`ContentPresenter` | UI 目录及嵌入式 C# 类型服务 |

边界遵循编译器：内联 `Style` 不支持变量或规则；组件 `@style` 不支持控件主题模块；
`resource()` 只解析同模块控件主题，并非通用动态资源。类/状态重命名只改可识别的样式语法和
`Class` 字面量，不猜测 C# 动态构造的字符串；无法唯一解析的类/主题不提供重命名。
缩进格式化保留字面量和注释，内联 `Style` 不重排。安装包构建与协议测试不等同于三个 IDE 的人工 GUI 验收。

## Visual Studio 插件

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ide/visualstudio/build.ps1
```

产物：`ide/visualstudio/bin/Release/net472/BubuUI.VisualStudio.vsix`，双击安装并重启 VS。
VSIX 包含语言服务器及共享高亮语法，本机需 .NET SDK 11+。
安装、环境变量配置、日志、客户端测试和验证边界见 [Visual Studio 说明](visualstudio/README.md)。

## 0. 架构：一个项目系统，一份项目 Compilation

design/06 §6.9 之后，目录不再是「扫 dll 元数据」，所有能力都从**同一份项目 Compilation**派生：

```
工作区里的 *.csproj
   ↓ L1  MSBuildLocator + Microsoft.Build.Evaluation.Project 求值（不执行 Target）
   ↓     Compile / AdditionalFiles / Using / 属性最终值；归属 = 求值后 AdditionalFiles 命中
   ↓ L2  真 CSharpCompilation：源码树 = Compile，隐式 using = Using items，
   ↓     引用 = obj/project.assets.json（package compile assets + 被引用工程的输出）
   ↓ L3  IDE 内跑真 TemplateGenerator（extern alias euiGen + build_property.rootnamespace），
   ↓     生成树加回编译 —— .bui 派生组件不必先 build 就存在
   ↓ L4  UiCatalogFactory.Build(输入编译, .bui 声明清单) —— 目录的唯一事实源
   ↓     与编译器的目录**同源同实现**（UiComponentScanner）：输入编译 + 清单，不是生成后编译
   ↓
所有能力：标签/属性补全、悬停、诊断、跳转定义、重命名、signature help、
语义着色、代码操作、格式化 —— 全部查这一份 Compilation 与这一份目录
```

口径总则：**编译时按什么，IDE 就按什么**。不自己实现 csproj glob、不解析 csproj 属性、
不依赖 VS Code C# Dev Kit 的项目系统、不为属性跳转扩正则认成员声明。
没有 csproj / untitled / 内存态分析时自动退回 legacy 的 dll 目录，能力优雅降级而不是消失。

## 1. 能力

| 能力 | 状态 | 说明 |
|---|---|---|
| 语法高亮 `.bui` / `.bss` | ✅ | TextMate grammar；标签/属性/`@bind-`/附加属性/指令/注释，内嵌 C# 词法 |
| 诊断 Tier 1 | ✅ | 每次按键跑 `FrontEnd.Parse + Analyze`，与 `dotnet build` 同码同 span |
| 诊断 Tier 2 | ✅ | 真生成器 + 项目 Compilation 的 CS/EUI 诊断经 `#line` 映回 `.bui`；400ms debounce，只在文档版本未变时发布 |
| `.bss` 诊断 | ✅ | `.bss` 进 `documentSelector`，`ExecuteStylesheet` 的 EUI050–052 有 LSP 红波浪线 |
| 控件模板 `@template` / `Owner` | ✅ | 目标类型补全、导入、悬停、定义导航；类型化 Owner 接入 C# 服务，错误恢复视图保留模板宿主类型 |
| `.bss` 控件主题 | ✅ | `@namespace`、`@using`、`@theme`、typed setter、状态、`/template/`、`resource()`、`template()` 的补全/悬停/导航；按类型和命名空间作用域过滤候选，支持未保存文件及未完成输入 |
| 主题编辑辅助 | ✅ | 同模块主题重命名（含本项目 `.cs/.bui` 可绑定引用）、大纲、语义着色、函数参数提示、导入与属性拼写快速修复、缩进及范围格式化；详见 [design/28](../design/28-lookless-controls.md) |
| `@namespace` 契约 | ✅ | 必填；生成器报 EUI055（缺失）/ EUI056（与 code-behind 不一致），经 Tier 2 映回 `.bui`。补全提供 `@namespace` 指令与命名空间候选（自身命名空间排首位） |
| 无 code-behind 的 `.bui` 控件 | ✅ | `@code` 中公开可读写属性自动作为模板参数；`[UiSlot]`/`[UiContent]` 声明内容角色。模板和 partial `.cs` 合并到同一符号视图，补全、绑定与构建共享目录。`UiParameter` 特性及其必填提示已移除。 |
| 标签补全 | ✅ | 来自项目目录；未 build 的自家控件、`.bui` 派生组件都在；选择链外未导入类型时，以同一补全项的附加编辑补入去重的 `@using`（`@namespace` 链内不需要，K33） |
| `@using` 命名空间 / 别名补全 | ✅ | 命名空间候选来自项目 Compilation，不限于 UI 控件命名空间；支持 `System.Col` 等限定前缀与别名目标。`@using Alt = Ns.Type` 的 `Alt` 仍作为标签候选 |
| 属性 / 事件 / 组件参数 / 附加属性补全 | ✅ | 含 `Grid.Row` 形态；事件带委托签名 |
| `@bind-` / 属性值 / `@` 指令 / `@using` 命名空间补全 | ✅ | `@bind-` 只列**支持绑定**的目标（控件可写属性；组件带形状正确的 `{P}Changed` 的参数，不含内容/委托/只读）；不可绑的名字由 EUI040 解释。另有枚举成员、`bool`、`Style=` 属性名；`Style="Prop: |"` 值位置按目录形态给候选（enum 列成员表，design/19） |
| 悬停 / 大纲 | ✅ | 标签 → CLR 类型与内容模型；属性 → 类型；事件 → 处理器签名；`Style` 属性名 → 值形态（enum 列成员） |
| 表达式补全 / 悬停 | ✅ | `{expr}` 与属性表达式走 Roslyn 投影；候选来自 `ExpressionEnvironment` + code-behind/`@code` 成员 + 接收者类型的实例成员 |
| `@code` 块内补全 / 悬停 | ✅ | 补全使用 Roslyn 生成文档，覆盖成员、局部变量、类型、关键字、扩展方法和基础 snippet；`@code` 与 `{expr}` 中直接输入名称即可补全，当前类与继承成员优先。候选说明经 `completionItem/resolve` 按需加载，不阻塞列表返回。悬停与跳转按符号回映；override 候选来自真实基类，生成器占用的 InitializeUi/Render 不作为作者候选 |
| 跳转定义 | ✅ | 标签与**属性/事件/参数/附加属性**都走 Roslyn 符号查询，落在成员声明；有源码跳源码（含仓库里以源码编译的框架工程），无源码走成员级反编译（`eui-decompiled:`） |
| 重命名 | ✅ | 可从 `{expr}` 或 `@code` 发起；项目模式通过 Roslyn Renamer 修改同项目 `.cs/.bui` 成员引用，`@foreach` 变量保留文档内路径。改不全、映不回或发生冲突时整体拒绝 |
| signature help | ✅ | `textDocument/signatureHelp`，重载与当前参数位从同一份 Compilation 取 |
| 语义着色 | ✅ | `textDocument/semanticTokens/full`，`.bui` 着色 `{expr}` 与 `@code` 区间（模板表面仍归 TextMate）；项目模式走 Roslyn `Classifier` 对生成文档分类再经统一映射回映，legacy 回落探针投影；`.bss` 着色类型、属性、主题与模板引用 |
| 代码操作 | ✅ | `EUI001` 补 `@using`、`EUI002` 最近属性替换、`CS0103` 把缺失成员生成到 `@code` 块（触发点仍是属性表达式，`@code` 块内不产生快速修复）；映不回的编辑丢弃 |
| 格式化 / 区间格式化 | ✅ | 模板缩进按 AST 做最小行编辑；`@code` 块体交 Roslyn `Formatter` |
| watcher 自动失效 | ✅ | `.cs/.bui/.bss/.csproj/Directory.Build.props/obj/project.assets.json` 变更 → 重建快照 + 重发诊断，无需手动 Reload |
| `#line` 调试映射 | ⛔ | 生成器侧已有 `#line`，调试器接线未做（design/06 P3） |

第 5 条验收的边界：**只在 `.bui` 内提供与 `.cs` 同档的 C# 能力，不接管 `.cs` 文件**
（那由 C# Dev Kit / ReSharper 负责）。`{expr}` 与 `@code` 共用项目语义与源映射；
支持范围及编辑规则见 [design/18](../design/18-razor-csharp-intellisense.md)，不等同于已经接通全部 C# IDE 功能。

## 2. VS Code：跑起来

```powershell
# 一次性：发布 server 到 ide/vscode/server + 装 npm 依赖 + 跑 grammar/壳断言
powershell -ExecutionPolicy Bypass -File ide/build.ps1
# 依赖已装好时可加 -SkipNpm
```

然后二选一：**调试**（VS Code 打开 `ide/vscode`，F5 Run Extension）或
**临时安装**（`code --extensionDevelopmentPath=<repo>/ide/vscode`）。

开发机不发布也能用：扩展会自己找到
`ide/BubuUI.LanguageServer/bin/{Release,Debug}/net11.0/BubuUI.LanguageServer.dll`。
改完服务端要重跑 `ide/build.ps1` 并 **Developer: Reload Window**；server 是常驻进程，
重载前一直连旧 dll，旧进程还会锁住 `ide/vscode/server/*.dll`（MSB3026/3027）。

## 3. server 怎么找到项目与目录

**优先路径（有 csproj）**：`--workspace <root>`（扩展自动传第一个工作区文件夹）→ 向上找仓库根
（`.git` / `*.sln*` 标记）→ 枚举 `*.csproj` 求值 → 每个项目一份 Compilation/目录。
文档属于哪个项目完全由求值后的 `AdditionalFiles` 决定；一个文档被多个项目收，诊断逐份都算；
不属于任何项目的磁盘文档**明确不给目录也不给诊断**（编译时不参与，IDE 不该假装它参与）。

服务端支持用编辑器缓冲区覆盖磁盘；`.bui` / `.bss` 由客户端正常同步。当前客户端没有额外
转发 `.cs` 的未保存缓冲区，因此实际 code-behind 更新仍依赖保存后的文件 watcher；服务端的
缓冲区注入测试不能替代这段客户端接线。结构文件变化重求值，其余变化使快照失效。

**legacy fallback（无 csproj / untitled / 内存态）**：仍是 `UiCatalogFactory.Build(Compilation)`
从 `BubuUI.Runtime.dll` 符号派生（不加载程序集），runtime 同目录下引用了 Runtime 的
程序集也会被扫进来。查找顺序：

1. `--runtime <path>`（扩展设置 `bubuui.runtime.path`）
2. 环境变量 `BUBUUI_RUNTIME`
3. server 自身目录下的 `BubuUI.Runtime.dll`（发布版随包带）
4. 从 `--workspace` 向上找仓库布局 `src/BubuUI.Runtime/bin/{Release,Debug}/net*/`

也可显式给 `--assembly <path>`（扩展设置 `bubuui.catalog.assemblies`，字符串数组）。

> 找不到 runtime 且没有项目时**不会**报「每个标签都未知」——语义检查直接跳过，只出语法诊断，
> 并用 `window/showMessage` 说明缺什么。配置问题不该伪装成代码问题。

### 3.1 没有红波浪线时怎么查

0. **先看 server 日志**。扩展会给 server 传 `--log <logUri>/bubuui-server.log --log-stderr`，
   所以调试控制台（扩展宿主）里能直接看到：

   ```
   projects: 19 evaluated from d:\workspace\BubuUI (workspace …)
   catalog: 14 tags from <project compilations>
   open Showcase.bui · 1 project(s)
   definition: Showcase.bui @32 -> Panels.cs
   definition: no result · Showcase.bui @1407
   ```

   `open … · 0 project(s)` 就是根因：文件不在任何项目的 `AdditionalFiles` 里，
   补全/诊断/跳转都会是空的（紧跟一行 `! … is not in any project's AdditionalFiles`）。
   `definition: no result` 说明服务端确实拿到了请求、光标位置对不上可跳转的符号。
1. 输出面板 → `BubuUI` 通道，看 `projects: N evaluated from …` 与
   `catalog: N tags from <project compilations>`；`N=0` 说明目录为空。
2. 命令面板 `BubuUI: Reload Tag Catalog`：0 tags 会弹 Warning。
3. 编辑器右下角语言模式必须是 `bubuui` / `bss`，否则 server 收不到 `didOpen`。
4. 确认 `obj/project.assets.json` 存在（先 `dotnet restore`）；缺它时引用不全，
   `Notes` 会明确提示「run `dotnet restore`」。
5. 确认扩展用的是最新 server（见 §2 的 Reload Window）。**server 进程会被 dll 锁住**：
   重发布前要先把窗口重载（或结束掉 `dotnet …LanguageServer.dll`），否则 `dotnet publish`
   会报 MSB3027。

## 4. Rider（正式插件 `ide/rider`）

design/06 §6.5 的**首选路径已落地**：`ide/rider` 是 IntelliJ 平台插件（lsp4ij 0.21.0 /
Rider 2024.2+），server 仍是同一个 `BubuUI.LanguageServer`，不加第二个语言服务实现。
0.2.7 的 Rider 安装包包含补全性能优化：复用项目快照、增量生成和 Roslyn 编辑器文档，
标签/属性补全不再等待完整生成及编译诊断；及时处理 LSP4IJ 发出的取消请求，
跳过排队中的过期请求并取消正在执行的 C# 补全和语义着色。完整候选列表可由客户端继续筛选，
避免因误报 `isIncomplete` 而重复请求。首次打开项目与首次 C# 补全仍需要预热。
通过 “Install Plugin from Disk” 安装新版 ZIP 并重启 Rider 后生效；自定义 Server DLL 路径时需同步更新该 DLL。

插件只做三件事：

1. 把 `.bui`/`.bss` 注册为 `Language` + `FileType`，并用
   `com.redhat.devtools.lsp4ij.languageMapping` 把它们映射到 `bubuui` server
   （`languageId` 分别发 `bubuui` / `bss`，与 VS Code 的 documentSelector 一致）。
   轻量词法 PSI 提供名称与标点的范围，避免 Alt/Ctrl 悬停把整个文件当成一个链接；
   模板与 C# 语义仍由共享 server 提供；
2. 用 `textmate.bundleProvider` 把**共享 grammar** 交给平台，并显式绑定
   `TextMateEditorHighlighterProvider` / `TextMateSyntaxHighlighterFactory`——自定义
   FileType 会压掉 TextMate 着色，这个绑定是 lsp4ij 自己也在用的 workaround；
3. `BubuUiLanguageServerFactory` 用 `ProcessStreamConnectionProvider` 拉起
   `dotnet <...>/BubuUI.LanguageServer.dll --stdio --workspace <project> --log … --log-stderr`。

**grammar 仍然是单份**：`build.gradle.kts` 的 `syncTextMateBundle` 从 `../vscode/syntaxes`
与两个 `language-configuration.json` 组装一个 VS Code 形状的 bundle（连同
`src/main/textmate/package.json`）放进构建产物，`BubuUiTextMateBundleProvider`
在运行时把 jar 里的 `/textmate/**` 释放到插件目录（按插件版本分目录，升级会刷新）。
仓库里没有第二份 grammar 拷贝。

### 跑起来

macOS（Apple Silicon / Intel）与 Linux 可直接构建 Rider 插件，无需 PowerShell：

```bash
export JAVA_HOME=$(/usr/libexec/java_home -v 21)  # macOS：使用已安装的 JDK 21
./ide/rider/build.sh
# 已验证测试后，可用 ./ide/rider/build.sh --skip-tests
```

需要 .NET 11 SDK、Node.js 和 JDK 21–24（推荐 21；当前 Gradle 9.0 不支持 JDK 25）。
脚本发布同一份语言服务器和 Runtime/Rendering 元数据，运行共享语言服务测试、
Rider 壳断言和 Java 测试，再构建并检查插件结构。
产物为 `ide/rider/build/distributions/bubuui-rider-0.2.0.zip`；ZIP 可用于三种桌面系统，
运行时需要本机架构的 .NET SDK。安装使用 Rider → Settings（macOS 也可按 `⌘,`）→
Plugins → 齿轮 → Install Plugin from Disk，选中 ZIP 并重启。

Windows：

```powershell
powershell -ExecutionPolicy Bypass -File ide/build.ps1   # 发布共享 server + 两个壳的断言
cd ide/rider
.\gradlew.bat buildPlugin     # 产物 build/distributions/*.zip
.\gradlew.bat runIde          # 带插件的 Rider 沙箱
.\gradlew.bat compileJava     # 只编译（CI / 换机后快速自检）
```

本机装 JDK 21–24 并让 `JAVA_HOME` 指向它；编译产物与 Rider 2024.2 使用 Java 21。
Gradle 9.0.0 由仓库内的 wrapper 自举，
不必全局安装。`gradle/wrapper/gradle-wrapper.properties` 固定了版本和发行包
SHA-256：默认 URL 走华为云镜像（国内可达且与官方包校验和一致），要换回官方源就把它
改成 `https\://services.gradle.org/distributions/gradle-9.0.0-bin.zip`，校验和不用动。
本机若靠 HTTP 代理上网，需给 Gradle 传 `-Dhttps.proxyHost=… -Dhttps.proxyPort=…`
（Java 不读 `HTTPS_PROXY` 环境变量），否则平台依赖会连不上。
已有解压的同版本 Rider SDK 时，可在 Gradle 命令后加
`-PlocalPlatformPath=/path/to/rider-sdk` 复用本地 SDK；默认仍下载完整 Rider SDK。

安装 zip（Settings → Plugins → Install Plugin from Disk）或直接用 `runIde`。
server DLL 的查找顺序：Settings → Languages & Frameworks → BubuUI 的 `Server DLL`
→ `BUBUUI_SERVER_DLL` → 插件目录下的 `server/` → 从项目根向上找仓库根，依次找
`ide/vscode/server`（`build.ps1` 的产物）、`ide/BubuUI.LanguageServer/bin/{Release,Debug}/net11.0`。
也就是说开发机上打开本仓库即可用；找不到会弹通知并在 lsp4ij 的 LSP console 留下原因。
设置页的四个字段与 VS Code 的 `bubuui.server.*` 一一对应。

macOS 从 Finder / Dock 打开 Rider 时，进程可能没有终端里的 `PATH`。插件会自动在
`DOTNET_ROOT`、`~/.dotnet`、官方安装目录与 Homebrew 的常用目录查找 `dotnet`；
也可在 BubuUI 设置的 `dotnet` 字段填写其绝对路径，例如
`/usr/local/share/dotnet/dotnet` 或 `/opt/homebrew/bin/dotnet`。

**watcher 不需要在 lsp4ij 里手配**：server 在 `initialized` 之后发
`client/registerCapability` 动态注册 `workspace/didChangeWatchedFiles`，glob 与
VS Code 客户端的 `WATCH_PATTERNS` 完全一致（六项，静态契约测试会交叉比对三处）。
lsp4ij 只认动态注册，所以这条是 Rider 上 L7 自动刷新的前提；VS Code 的静态 watcher
保留，两边都发时 server 的 `Invalidate` 幂等。

**URI 兼容**：IntelliJ 的 `file:///D:/…`、`file:/D:/…`、`file://D:/…` 与 VS Code 的
`file:///d%3A/…` 都走 `DocumentStore.UriToPath` 还原成同一个 Windows 路径，
`ProjectSystemTests.UriConversion_*` 有夹具钉住。

### 不想构建插件时的过渡路径

lsp4ij → Settings → Languages & Frameworks → Language Servers → `+` User-defined server：

| 字段 | 值 |
|---|---|
| Name | BubuUI |
| Command | `dotnet` |
| Arguments | `<repo>/ide/BubuUI.LanguageServer/bin/Release/net11.0/BubuUI.LanguageServer.dll --stdio --workspace <repo>` |
| Mappings | `*.bui` → `bubuui`；`*.bss` → `bss` |

这条路径没有自定义 Language/FileType（高亮靠手动导入 TextMate bundle），但同样能拿到
补全、诊断与跳转。

### 边界

- 插件是 Java/Gradle 工程：JDK 21 + Gradle 9.0.0 wrapper 下使用 `buildPlugin` 产出
  `build/distributions/bubuui-rider-0.2.0.zip`，包内含共享 grammar、`since-build=242`
  与两条 languageMapping。接线类错误另有 `node test/plugin.test.js` 的静态契约
  交叉比对（扩展点/类名/grammar scopeName/watcher/启动参数/wrapper）。
  2026-11-17 在 macOS Apple Silicon / Temurin 21 上通过共享服务 392 项测试、Rider 23 项
  Java 测试、壳断言与 ZIP 结构检查。本机使用官方 242 SDK 的 Java 部分，打包时用
  `-x buildSearchableOptions` 跳过需要完整 Rider 插件的设置搜索索引；标准构建与 CI 仍使用
  完整 SDK 并保留索引步骤。**仍未做** Rider 内人工冒烟（`runIde` 打开 `.bui` 看高亮与补全）。
- 插件 ZIP 内嵌 `server/`，与 VS Code / Visual Studio 使用同一份发布输出。设置页仍可指定 DLL 覆盖。
- Rider 启动 server 时传入 `--materialize-decompiled`：无源码的控件/属性跳转到系统临时目录里的只读 C# 文件；文件按 URI 与内容散列区分版本，保留定义范围。VS Code / Visual Studio 仍使用原有虚拟文档通道。
- `@code` 块内补全/悬停/重命名由共享 server 提供；`#line` 调试器接线仍未实现。

## 5. 测试

```powershell
dotnet test tests/BubuUI.Ide.Tests     # 项目系统 / 光标 / 补全 / 悬停 / 跳转 / 重命名 / 反编译 / 诊断 / 协议帧 / C# 源映射与端到端会话
cd ide/vscode; node test/tokenize.test.js  # grammar：真实 oniguruma 引擎逐 token 断言 scope
cd ide/vscode; node test/extension.test.js # 客户端壳：documentSelector / watcher / 参数
cd ide/rider;  node test/plugin.test.js    # Rider 壳静态契约（无需 JDK）：EP / 类名 / grammar / watcher / 启动参数 / wrapper
cd ide/rider;  .\gradlew.bat verifyShell   # 同一组断言的 Gradle 入口（也组装 TextMate bundle）
cd ide/rider;  .\gradlew.bat test          # 真实 Rider PSI / LSP4IJ：悬停名称范围与未保存编辑
```

`ProjectSystemTests.cs` 用真实仓库跑 L1–L4，把 design/06 §6.9.7 的五条验收做成可执行夹具：
未 build 的控件与 `.bui` 派生组件进目录、别名补全/解析、属性补全与目录全集一致、
框架属性/标签 F12 落仓库源码、`.bui` 的 `CS0103` 与 `.bss` 的 `EUI051` 都由 Tier 2 报出。

grammar 测试值得单说：TextMate 写错不会报错，只会「某个地方忽然没颜色」。
`node test/tokenize.test.js --dump` 会把每行每个 token 的 scope 打出来。

## 6. 已知边界

- **全文同步**（`TextDocumentSyncKind.Full`）：.bui 都是几 KB，全量重解析比维护增量编辑映射可靠。
- **项目求值失败只掉 IDE 能力**：`dotnet build` 用 SDK 自带 MSBuild，与 LSP 进程内 Locator 加载的
  是两条独立路径；求值挂了项目照样编译得过，Notes 里会写清原因。
- **引用依赖 restore**：精确 dll 路径来自 `obj/project.assets.json`；没 restore 过就只有平台程序集，
  框架控件可能补不出来。先跑一次 `dotnet restore`。
- **跳转定义**：源码可达（含仓库里以源码编译的框架工程）跳真实文件；只有元数据（dll / NuGet）
  才反编译成 `eui-decompiled:` 只读虚拟文档。反编译文本是重建的 C#，与原始源码不完全一致。
  目录不认识的标签返回 null，不给错位置。
- **重命名**：项目模式限于同项目源码中的成员、局部变量与参数，不覆盖类型/命名空间、文件名或跨项目改名。独立文档保留保守扫描边界，无法保证完整交付时拒绝。
- **代码操作 / 格式化**：编辑经偏移回映改写回 `.bui`；映不回的丢弃而不是猜。
- **补全编辑边界**：主替换和附加编辑使用原 `.bui` 坐标；全部编辑必须可完整回映，或有明确的
  `@using` 指令插入规则。无法精确转换的生成独有编辑不交付。自动导入按文件已有换行符生成且去重，
  不把 C# `using` 放入 `@code`。机制和验收见 [design/18 §12.15](../design/18-razor-csharp-intellisense.md#1215-补全完整编辑与指令命名空间2026-11-13)。
- **`#line` 调试映射**未做；语义着色覆盖内嵌 C# 与样式区间，其他模板表面由 TextMate 着色。
- **URI 必须按编辑器格式解析**：VS Code 发 `file:///d%3A/repo/x.bui`（盘符冒号编码、盘符小写）。
  这种写法下 .NET 的 `Uri.LocalPath` 返回 `/d:/repo/x.bui`，再经 `GetFullPath` 就永远匹配不到
  项目里的真实路径——2026-11-11 的「脚本全绿、编辑器全废」就是这个分支。`DocumentStore.UriToPath`
  自己解转义并按盘符/UNC 还原，`ProjectSystemTests.UriConversion_*` 与 `Fixture7_*` 钉住这个行为。
  新增任何「从 URI 取路径」的代码都必须走它，不要直接 `new Uri(...).LocalPath`。

## 循环和缩进（0.2.8）

`.bui` 支持 `@for`、`@while`、`@do { ... } while (...);`，包括循环变量的补全、跳转、悬停、重命名和语法高亮。循环的条目重建语义见 [模板语法](../spec/01-template-syntax.md#4111-forwhiledo)。

模板格式化、范围格式化和输入时格式化共用缩进扫描器，识别内联标签、多行属性、循环块及 C# 字符串/注释，并遵守编辑器的空格/Tab 和宽度设置。补全按客户端能力协商：支持片段及缩进调整的客户端接收相对缩进；其余客户端接收按当前行对齐的文本。

- Visual Studio：Enter 使用 Smart Indent；多行粘贴在同一次撤销操作内整理缩进。`@` 指令补全使用已展开的文本，避免 `$0`/`${1:...}` 原样出现；补全读取当前文档视图的缩进宽度、Tab 宽度及空格设置。
- VS Code：默认启用 BubuUI/Bss 的 formatOnPaste、formatOnType；可在编辑器设置中覆盖。
- Rider：通过 LSP4IJ 使用同一服务端的 Enter/输入及范围格式化；粘贴整理遵守 Rider 的 Reformat on paste 设置。指令补全按当前文件的 Code Style 对齐，保留 IntelliJ 模板占位符；补上 LSP4IJ 0.21 多行 textEdit 缺少当前行缩进的问题。多行字符串和注释内部空白保持不变。
