# EclipseUI IDE 支持（design/06 的落地）

这里放的是 design/06 里那份「**一份共享资产，两个薄壳**」：

```
ide/
  EclipseUI.LanguageServer/   LSP server（C#/.NET 10，stdio）——全部 IDE 能力都在这里
    Workspace/                L1–L4：MSBuild 求值 / 精确引用 / 项目 Compilation / 真生成器
    Analysis/                 诊断 / 补全 / 悬停 / 大纲 / 跳转 / 重命名 / 内嵌 C# 语言服务
    Server/                   JSON-RPC 协议层（声明能力、翻译 LSP 模型）
  vscode/                     VS Code 壳：TextMate grammar + 语言配置 + 片段 + 极薄的 LSP 客户端
  rider/                      Rider 壳：lsp4ij 插件（Language/FileType + TextMate bundle + server 启动命令）
  build.ps1                   发布 server 到扩展目录 + 装依赖 + 跑 grammar/两个壳的断言
```

Rider 走 lsp4ij 接**同一个** server（见 §4），所以补全/诊断的行为在两个 IDE 里天然一致，
差别只在壳的接线方式。

## 0. 架构：一个项目系统，一份项目 Compilation

design/06 §6.9 之后，目录不再是「扫 dll 元数据」，所有能力都从**同一份项目 Compilation**派生：

```
工作区里的 *.csproj
   ↓ L1  MSBuildLocator + Microsoft.Build.Evaluation.Project 求值（不执行 Target）
   ↓     Compile / AdditionalFiles / Using / 属性最终值；归属 = 求值后 AdditionalFiles 命中
   ↓ L2  真 CSharpCompilation：源码树 = Compile，隐式 using = Using items，
   ↓     引用 = obj/project.assets.json（package compile assets + 被引用工程的输出）
   ↓ L3  IDE 内跑真 TemplateGenerator（extern alias euiGen + build_property.rootnamespace），
   ↓     生成树加回编译 —— .ui 派生组件不必先 build 就存在
   ↓ L4  UiCatalogFactory.Build(输入编译, .ui 声明清单) —— 目录的唯一事实源
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
| 语法高亮 `.ui` / `.eustyle` | ✅ | TextMate grammar；标签/属性/`@bind-`/附加属性/指令/注释，内嵌 C# 词法 |
| 诊断 Tier 1 | ✅ | 每次按键跑 `FrontEnd.Parse + Analyze`，与 `dotnet build` 同码同 span |
| 诊断 Tier 2 | ✅ | 真生成器 + 项目 Compilation 的 CS/EUI 诊断经 `#line` 映回 `.ui`；400ms debounce，只在文档版本未变时发布 |
| `.eustyle` 诊断 | ✅ | `.eustyle` 进 `documentSelector`，`ExecuteStylesheet` 的 EUI050–052 有 LSP 红波浪线 |
| `@namespace` 契约 | ✅ | 必填；生成器报 EUI055（缺失）/ EUI056（与 code-behind 不一致），经 Tier 2 映回 `.ui`。补全提供 `@namespace` 指令与命名空间候选（自身命名空间排首位） |
| 无 code-behind 的 `.ui` 组件 | ✅ | 目录把「同名同命名空间的 `.ui`」当组件证据（spec/02 §2.7），`<Tag />` 在编辑器与 `dotnet build` 里一致。K34：`@code` 里的 `[UiParameter]`/`[UiSlot]`/`[UiContent]` 由目录语法级提取为对外表面——补全、`@bind-` 校验与 `dotnet build` 同口径；有同名 `.cs` 时这些特性只对本文件可见（`EUI057` Warning）。`Ref={…}` 指向 `@code` 字段时 F12 落在本文档的字段声明 |
| 标签补全 | ✅ | 来自项目目录；未 build 的自家控件、`.ui` 派生组件都在；选择链外未导入类型时，以同一补全项的附加编辑补入去重的 `@using`（`@namespace` 链内不需要，K33） |
| `@using` 命名空间 / 别名补全 | ✅ | 命名空间候选来自项目 Compilation，不限于 UI 控件命名空间；支持 `System.Col` 等限定前缀与别名目标。`@using Alt = Ns.Type` 的 `Alt` 仍作为标签候选 |
| 属性 / 事件 / 组件参数 / 附加属性补全 | ✅ | 含 `Grid.Row` 形态；事件带委托签名 |
| `@bind-` / 属性值 / `@` 指令 / `@using` 命名空间补全 | ✅ | `@bind-` 只列**支持绑定**的目标（控件可写属性；组件带形状正确的 `{P}Changed` 的参数，不含内容/委托/只读）；不可绑的名字由 EUI040 解释。另有枚举成员、`bool`、`Style=` 属性名；`Style="Prop: |"` 值位置按目录形态给候选（enum 列成员表，design/19） |
| 悬停 / 大纲 | ✅ | 标签 → CLR 类型与内容模型；属性 → 类型；事件 → 处理器签名；`Style` 属性名 → 值形态（enum 列成员） |
| 表达式补全 / 悬停 | ✅ | `{expr}` 与属性表达式走 Roslyn 投影；候选来自 `ExpressionEnvironment` + code-behind/`@code` 成员 + 接收者类型的实例成员 |
| `@code` 块内补全 / 悬停 | ✅ | 补全使用 Roslyn 生成文档，覆盖成员、局部变量、类型、关键字、扩展方法和基础 snippet；悬停与跳转按符号回映。成员声明处的 override 候选来自真实基类，生成器占用的 InitializeUi/Render 不作为作者候选 |
| 跳转定义 | ✅ | 标签与**属性/事件/参数/附加属性**都走 Roslyn 符号查询，落在成员声明；有源码跳源码（含仓库里以源码编译的框架工程），无源码走成员级反编译（`eui-decompiled:`） |
| 重命名 | ✅ | 可从 `{expr}` 或 `@code` 发起；项目模式通过 Roslyn Renamer 修改同项目 `.cs/.ui` 成员引用，`@foreach` 变量保留文档内路径。改不全、映不回或发生冲突时整体拒绝 |
| signature help | ✅ | `textDocument/signatureHelp`，重载与当前参数位从同一份 Compilation 取 |
| 语义着色 | ✅ | `textDocument/semanticTokens/full`，只着色 `{expr}` 与 `@code` 区间（模板表面仍归 TextMate）；项目模式走 Roslyn `Classifier` 对生成文档分类再经统一映射回映，legacy 回落探针投影（design/18 §12.12） |
| 代码操作 | ✅ | `EUI001` 补 `@using`、`EUI002` 最近属性替换、`CS0103` 把缺失成员生成到 `@code` 块（触发点仍是属性表达式，`@code` 块内不产生快速修复）；映不回的编辑丢弃 |
| 格式化 / 区间格式化 | ✅ | 模板缩进按 AST 做最小行编辑；`@code` 块体交 Roslyn `Formatter` |
| watcher 自动失效 | ✅ | `.cs/.ui/.eustyle/.csproj/Directory.Build.props/obj/project.assets.json` 变更 → 重建快照 + 重发诊断，无需手动 Reload |
| `#line` 调试映射 | ⛔ | 生成器侧已有 `#line`，调试器接线未做（design/06 P3） |

第 5 条验收的边界：**只在 `.ui` 内提供与 `.cs` 同档的 C# 能力，不接管 `.cs` 文件**
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
`ide/EclipseUI.LanguageServer/bin/{Release,Debug}/net10.0/EclipseUI.LanguageServer.dll`。
改完服务端要重跑 `ide/build.ps1` 并 **Developer: Reload Window**；server 是常驻进程，
重载前一直连旧 dll，旧进程还会锁住 `ide/vscode/server/*.dll`（MSB3026/3027）。

## 3. server 怎么找到项目与目录

**优先路径（有 csproj）**：`--workspace <root>`（扩展自动传第一个工作区文件夹）→ 向上找仓库根
（`.git` / `*.sln*` 标记）→ 枚举 `*.csproj` 求值 → 每个项目一份 Compilation/目录。
文档属于哪个项目完全由求值后的 `AdditionalFiles` 决定；一个文档被多个项目收，诊断逐份都算；
不属于任何项目的磁盘文档**明确不给目录也不给诊断**（编译时不参与，IDE 不该假装它参与）。

服务端支持用编辑器缓冲区覆盖磁盘；`.ui` / `.eustyle` 由客户端正常同步。当前客户端没有额外
转发 `.cs` 的未保存缓冲区，因此实际 code-behind 更新仍依赖保存后的文件 watcher；服务端的
缓冲区注入测试不能替代这段客户端接线。结构文件变化重求值，其余变化使快照失效。

**legacy fallback（无 csproj / untitled / 内存态）**：仍是 `UiCatalogFactory.Build(Compilation)`
从 `EclipseUI.Runtime.dll` 符号派生（不加载程序集），runtime 同目录下引用了 Runtime 的
程序集也会被扫进来。查找顺序：

1. `--runtime <path>`（扩展设置 `eclipseui.runtime.path`）
2. 环境变量 `ECLIPSEUI_RUNTIME`
3. server 自身目录下的 `EclipseUI.Runtime.dll`（发布版随包带）
4. 从 `--workspace` 向上找仓库布局 `src/EclipseUI.Runtime/bin/{Release,Debug}/net*/`

也可显式给 `--assembly <path>`（扩展设置 `eclipseui.catalog.assemblies`，字符串数组）。

> 找不到 runtime 且没有项目时**不会**报「每个标签都未知」——语义检查直接跳过，只出语法诊断，
> 并用 `window/showMessage` 说明缺什么。配置问题不该伪装成代码问题。

### 3.1 没有红波浪线时怎么查

0. **先看 server 日志**。扩展会给 server 传 `--log <logUri>/eclipseui-server.log --log-stderr`，
   所以调试控制台（扩展宿主）里能直接看到：

   ```
   projects: 19 evaluated from d:\workspace\EclipseUI (workspace …)
   catalog: 14 tags from <project compilations>
   open Showcase.ui · 1 project(s)
   definition: Showcase.ui @32 -> Panels.cs
   definition: no result · Showcase.ui @1407
   ```

   `open … · 0 project(s)` 就是根因：文件不在任何项目的 `AdditionalFiles` 里，
   补全/诊断/跳转都会是空的（紧跟一行 `! … is not in any project's AdditionalFiles`）。
   `definition: no result` 说明服务端确实拿到了请求、光标位置对不上可跳转的符号。
1. 输出面板 → `EclipseUI` 通道，看 `projects: N evaluated from …` 与
   `catalog: N tags from <project compilations>`；`N=0` 说明目录为空。
2. 命令面板 `EclipseUI: Reload Tag Catalog`：0 tags 会弹 Warning。
3. 编辑器右下角语言模式必须是 `eclipseui` / `eustyle`，否则 server 收不到 `didOpen`。
4. 确认 `obj/project.assets.json` 存在（先 `dotnet restore`）；缺它时引用不全，
   `Notes` 会明确提示「run `dotnet restore`」。
5. 确认扩展用的是最新 server（见 §2 的 Reload Window）。**server 进程会被 dll 锁住**：
   重发布前要先把窗口重载（或结束掉 `dotnet …LanguageServer.dll`），否则 `dotnet publish`
   会报 MSB3027。

## 4. Rider（正式插件 `ide/rider`）

design/06 §6.5 的**首选路径已落地**：`ide/rider` 是 IntelliJ 平台插件（lsp4ij 0.21.0 /
Rider 2024.2+），server 仍是同一个 `EclipseUI.LanguageServer`，不加第二个语言服务实现。
插件只做三件事：

1. 把 `.ui`/`.eustyle` 注册为 `Language` + `FileType`，并用
   `com.redhat.devtools.lsp4ij.languageMapping` 把它们映射到 `eclipseui` server
   （`languageId` 分别发 `eclipseui` / `eustyle`，与 VS Code 的 documentSelector 一致）；
2. 用 `textmate.bundleProvider` 把**共享 grammar** 交给平台，并显式绑定
   `TextMateEditorHighlighterProvider` / `TextMateSyntaxHighlighterFactory`——自定义
   FileType 会压掉 TextMate 着色，这个绑定是 lsp4ij 自己也在用的 workaround；
3. `EclipseUiLanguageServerFactory` 用 `ProcessStreamConnectionProvider` 拉起
   `dotnet <...>/EclipseUI.LanguageServer.dll --stdio --workspace <project> --log … --log-stderr`。

**grammar 仍然是单份**：`build.gradle.kts` 的 `syncTextMateBundle` 从 `../vscode/syntaxes`
与两个 `language-configuration.json` 组装一个 VS Code 形状的 bundle（连同
`src/main/textmate/package.json`）放进构建产物，`EclipseUiTextMateBundleProvider`
在运行时把 jar 里的 `/textmate/**` 释放到插件目录（按插件版本分目录，升级会刷新）。
仓库里没有第二份 grammar 拷贝。

### 跑起来

```powershell
powershell -ExecutionPolicy Bypass -File ide/build.ps1   # 发布共享 server + 两个壳的断言
cd ide/rider
.\gradlew.bat buildPlugin     # 产物 build/distributions/*.zip
.\gradlew.bat runIde          # 带插件的 Rider 沙箱
.\gradlew.bat compileJava     # 只编译（CI / 换机后快速自检）
```

只需本机装 JDK 17 并让 `JAVA_HOME` 指向它；Gradle 9.0.0 由仓库内的 wrapper 自举，
不必全局安装。`gradle/wrapper/gradle-wrapper.properties` 固定了版本和发行包
SHA-256：默认 URL 走华为云镜像（国内可达且与官方包校验和一致），要换回官方源就把它
改成 `https\://services.gradle.org/distributions/gradle-9.0.0-bin.zip`，校验和不用动。
本机若靠 HTTP 代理上网，需给 Gradle 传 `-Dhttps.proxyHost=… -Dhttps.proxyPort=…`
（Java 不读 `HTTPS_PROXY` 环境变量），否则平台依赖会连不上。

安装 zip（Settings → Plugins → Install Plugin from Disk）或直接用 `runIde`。
server DLL 的查找顺序：Settings → Languages & Frameworks → EclipseUI 的 `Server DLL`
→ `ECLIPSEUI_SERVER_DLL` → 插件目录下的 `server/` → 从项目根向上找仓库根，依次找
`ide/vscode/server`（`build.ps1` 的产物）、`ide/EclipseUI.LanguageServer/bin/{Release,Debug}/net10.0`。
也就是说开发机上打开本仓库即可用；找不到会弹通知并在 lsp4ij 的 LSP console 留下原因。
设置页的四个字段与 VS Code 的 `eclipseui.server.*` 一一对应。

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
| Name | EclipseUI |
| Command | `dotnet` |
| Arguments | `<repo>/ide/EclipseUI.LanguageServer/bin/Release/net10.0/EclipseUI.LanguageServer.dll --stdio --workspace <repo>` |
| Mappings | `*.ui` → `eclipseui`；`*.eustyle` → `eustyle` |

这条路径没有自定义 Language/FileType（高亮靠手动导入 TextMate bundle），但同样能拿到
补全、诊断与跳转。

### 边界

- 插件是 Java/Gradle 工程：**已在 Temurin JDK 17 + Gradle 9.0.0 wrapper 下真实编译
  并打包**（`compileJava` 产出 11 个 class；`buildPlugin` 产出
  `build/distributions/eclipseui-rider-0.1.0.zip`，包内含共享 grammar、`since-build=242`
  与两条 languageMapping）。接线类错误另有 `node test/plugin.test.js` 的 12 项静态契约
  交叉比对（扩展点/类名/grammar scopeName/watcher/启动参数/wrapper）。
  **仍未做** Rider 内人工冒烟（`runIde` 打开 `.ui` 看高亮与补全）：构建期的
  `buildSearchableOptions` 确实启动过 Rider 沙箱并成功释放 bundle，但那不是交互验证。
- 插件 zip **不内嵌 server dll**（锁步发版要求两壳指向同一份产物）。发布时把
  `ide/vscode/server/` 放进插件目录，或在设置页指定 DLL 路径。
- 跳转到只有 dll/元数据、没有源码的控件时 server 返回 `eui-decompiled:` 虚拟文档；
  VS Code 壳注册了 `TextDocumentContentProvider`，Rider 侧尚未接，故这类跳转在 Rider
  上打不开内容（有源码的跳转正常）。这是 P2 的后续片。
- `@code` 块内补全/悬停/重命名由共享 server 提供；`#line` 调试器接线仍未实现。

## 5. 测试

```powershell
dotnet test tests/EclipseUI.Ide.Tests     # 项目系统 / 光标 / 补全 / 悬停 / 跳转 / 重命名 / 反编译 / 诊断 / 协议帧 / C# 源映射与端到端会话
cd ide/vscode; node test/tokenize.test.js  # grammar：真实 oniguruma 引擎逐 token 断言 scope
cd ide/vscode; node test/extension.test.js # 客户端壳：documentSelector / watcher / 参数
cd ide/rider;  node test/plugin.test.js    # Rider 壳静态契约（无需 JDK）：EP / 类名 / grammar / watcher / 启动参数 / wrapper
cd ide/rider;  .\gradlew.bat verifyShell   # 同一组断言的 Gradle 入口（也组装 TextMate bundle）
```

`ProjectSystemTests.cs` 用真实仓库跑 L1–L4，把 design/06 §6.9.7 的五条验收做成可执行夹具：
未 build 的控件与 `.ui` 派生组件进目录、别名补全/解析、属性补全与目录全集一致、
框架属性/标签 F12 落仓库源码、`.ui` 的 `CS0103` 与 `.eustyle` 的 `EUI051` 都由 Tier 2 报出。

grammar 测试值得单说：TextMate 写错不会报错，只会「某个地方忽然没颜色」。
`node test/tokenize.test.js --dump` 会把每行每个 token 的 scope 打出来。

## 6. 已知边界

- **全文同步**（`TextDocumentSyncKind.Full`）：.ui 都是几 KB，全量重解析比维护增量编辑映射可靠。
- **项目求值失败只掉 IDE 能力**：`dotnet build` 用 SDK 自带 MSBuild，与 LSP 进程内 Locator 加载的
  是两条独立路径；求值挂了项目照样编译得过，Notes 里会写清原因。
- **引用依赖 restore**：精确 dll 路径来自 `obj/project.assets.json`；没 restore 过就只有平台程序集，
  框架控件可能补不出来。先跑一次 `dotnet restore`。
- **跳转定义**：源码可达（含仓库里以源码编译的框架工程）跳真实文件；只有元数据（dll / NuGet）
  才反编译成 `eui-decompiled:` 只读虚拟文档。反编译文本是重建的 C#，与原始源码不完全一致。
  目录不认识的标签返回 null，不给错位置。
- **重命名**：项目模式限于同项目源码中的成员、局部变量与参数，不覆盖类型/命名空间、文件名或跨项目改名。独立文档保留保守扫描边界，无法保证完整交付时拒绝。
- **代码操作 / 格式化**：编辑经偏移回映改写回 `.ui`；映不回的丢弃而不是猜。
- **补全编辑边界**：主替换和附加编辑使用原 `.ui` 坐标；全部编辑必须可完整回映，或有明确的
  `@using` 指令插入规则。无法精确转换的生成独有编辑不交付。自动导入按文件已有换行符生成且去重，
  不把 C# `using` 放入 `@code`。机制和验收见 [design/18 §12.15](../design/18-razor-csharp-intellisense.md#1215-补全完整编辑与指令命名空间2026-11-13)。
- **`#line` 调试映射**未做；语义着色只覆盖内嵌 C# 区间，模板表面仍由 TextMate 着色。
- **URI 必须按编辑器格式解析**：VS Code 发 `file:///d%3A/repo/x.ui`（盘符冒号编码、盘符小写）。
  这种写法下 .NET 的 `Uri.LocalPath` 返回 `/d:/repo/x.ui`，再经 `GetFullPath` 就永远匹配不到
  项目里的真实路径——2026-11-11 的「脚本全绿、编辑器全废」就是这个分支。`DocumentStore.UriToPath`
  自己解转义并按盘符/UNC 还原，`ProjectSystemTests.UriConversion_*` 与 `Fixture7_*` 钉住这个行为。
  新增任何「从 URI 取路径」的代码都必须走它，不要直接 `new Uri(...).LocalPath`。
