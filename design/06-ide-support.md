# 6. IDE 支持（VS Code + Rider 第一优先）

> **实现状态（2026-11-11，§6.9 已落地 P0–P8）**：设计口径从「v1 dll 元数据目录 + 手写启发式」
> 切换为 **§6.9 项目系统驱动的单一 Compilation**。`ide/BubuUI.LanguageServer/Workspace/`
> 新增 L1（MSBuildLocator + `Microsoft.Build.Evaluation.Project` 求值）、L2（每项目一份真
> `CSharpCompilation`，引用来自 `obj/project.assets.json`）、L3（`extern alias euiGen` 在 IDE
> 内跑真 `TemplateGenerator`）、L4（`UiCatalogFactory.Build(outputCompilation)`）；L5 诊断双通道、
> L6 符号查询式转定义、L7 watcher、L8 内嵌 C# 语言服务全部从这一份 Compilation 派生。
> **L8 的覆盖不完整**：`{expr}`（含属性表达式）九项能力全通；`@code { }` 块只有跳转 /
> 签名帮助 / 语义着色 / Tier 2 诊断 / 格式化，**补全 / 悬停 / 重命名从未接入**（三者都只判
> `CursorKind.Expression`）。机制、成因与修法见 **§6.9.10**——不要按"内嵌 C# 完整语言服务"
> 这个说法推定两块对称。
> 五条验收夹具已在 `tests/BubuUI.Ide.Tests/ProjectSystemTests.cs` 落成可执行回归，
> `ide/build.ps1` 已重发布并对发布版做过 stdio LSP 端到端冒烟（框架属性 F12 实测落
> `src/BubuUI.Runtime/Panels.cs`）。**P9（重命名换 `SymbolFinder`）仍按原计划不做**。
> `CatalogProvider` 的 dll 目录只作为「无 csproj / untitled / 内存态」的 legacy fallback 保留。
> 落地过程中实测修正的三处见 §6.9.9 末尾。§6.9 各节以下仍是设计与决策口径，不再是提案。
>
> **实现状态（2026-11-10，v1 历史）**：§6.8 的 **P0 + P1 前半 + L3 核心（投影 / 悬停 / 补全 / 跳转定义 / 重命名）**已落地——
> `ide/BubuUI.LanguageServer`（stdio LSP：诊断 / 标签·属性·事件·参数·附加属性·`@bind-`·枚举·指令补全 /
> **表达式悬停与 C# 补全 / 跳转定义 / 重命名** / 大纲）+ `ide/vscode`（`.bui` 与 `.bss` 两份 TextMate grammar、
> 语言配置、片段、百行客户端）。目录仍走 §6.3 的「组件注册表驱动」，只是 v1 由
> `UiCatalogFactory` 从程序集符号派生，而非 MSBuildWorkspace 加载项目。
> 表达式投影按 §6.3 机制落地：探针合成树复用生成器的 `ExpressionProbe`（单一实现，
> `src/BubuUI.Compiler.Probe`），code-behind 取 `{Name}.cs` 兄弟文件（spec/02 §2.2）；
> 跳转定义四路落地：① 探针符号反查（`Locate`）——foreach 变量落回本文档、code-behind
> 成员落到兄弟文件声明；② `@code` 块内 C# → 成员树语义模型（偏移经块体逐字透传回映）；
> ③ 标签名 → 目录 CLR 类型反查源码——兄弟 code-behind / 工作区 .cs / 框架类型向上找仓库根
> （`.git`/sln 标记）；④ 源码不可达（dll/nuget 引用）→ ICSharpCode.Decompiler（ILSpy 引擎，
> 自带 Roslyn fork 不与本仓 Roslyn 冲突）反编译类型挂 `eui-decompiled:` 虚拟文档，
> 客户端 TextDocumentContentProvider 经 `bubuui/decompiledContent` 取回文本。
> 重命名沿用同一探针（`Locate` + `ClassifyIdentifiers`）：foreach 变量文档内完备改名；
> code-behind 类自身成员扫 `.bui`+`@code`+兄弟 `.cs`+工作区 `.cs`，其他类型成员拒绝。
> **L3 余项（语义着色 / `#line` 调试映射）尚未做**。
>
> **2026-11-11 审计**：server 内存在**四套互不一致的「可见源码」口径**（目录吃 dll、表达式探针只吃
> 本文件 code-behind、重命名吃全工作区 `.cs`、跳转定义正则扫文本），已实测导致自家项目控件报
> `EUI001`、且 `IsFromReferencedAssembly` 恒真使诊断码/位置与 `dotnet build` 分叉。
> 收敛方案即 **§6.9（2026-11-11 拍板，同日落地 P0–P8）**——它取代上文「v1 由程序集符号派生，
> 而非 MSBuildWorkspace 加载项目」这一阶段性决定。**§6.9.0 按用户五条 IDE 要求定验收**
> （`@using`/别名、标签补全、属性补全、属性与标签转定义、内嵌 C# 完整语言服务），
> 逐条列出的是**落地前的现状与缺口**；实施项与验收见 §6.9.7，下表「现状」列不再代表当前行为。
> 用法与边界见 [ide/README.md](../ide/README.md)。本文余下部分是设计与决策口径，不是完成度声明。

核心难题：**`.bui` 是自定义语言，但表达式是 C#——让 C# 的智能"投影"进你的文件**。

## 6.1 三层能力，三个载体

| 层 | 内容 | 载体 | 成本 |
|---|---|---|---|
| 语法层 | 高亮、括号匹配、折叠、片段 | TextMate grammar（VS Code / Rider 通用）+ Tree-sitter（Neovim/Zed） | 低，纯声明式 |
| 编译诊断 | 类型错误、未知标签、字面量格式错 | **Roslyn 增量生成器天然携带**（`Location.Create(additionalFile, span)`），零 IDE 插件 | 极低 |
| 语义智能 | 补全、悬停、跳转、重命名、代码操作 | **自写 LSP + Roslyn 投影** | 高，主要投入 |

关键洞察：模板编译为 Roslyn 增量生成器后，诊断这条线是搭便车的——生成器报错时用
`Location.Create(additionalFile, span)` 把错误定位到 `.bui` 原始偏移，VS / Rider /
VS Code 的错误列表和红波浪线自动就有。**第一版 IDE 体验的底线靠这个兜住**。

## 6.2 总体形态：一份共享资产，两个薄壳

```
┌─────────────────────────────────────────────┐
│  共享资产（真正的工作量所在）                  │
│  ① LSP Server：.NET 进程，stdio 通信，         │
│     内嵌 Roslyn Workspace + 表达式投影器       │
│  ② TextMate grammar（.tmLanguage.json）       │
│  ③ 语言片段（snippets）、语言配置              │
└──────────────┬──────────────┬────────────────┘
               │              │
     ┌─────────▼────┐   ┌─────▼──────────────┐
     │ VS Code 扩展  │   │ Rider/IntelliJ 插件 │
     │ （LSP 客户端）│   │ （TextMate 导入      │
     │              │   │  + lsp4ij 声明）     │
     └──────────────┘   └────────────────────┘
```

两个壳极薄，补全/诊断/跳转/重命名全部逻辑在 server 内，IDE 侧只做接线。
**IDE 差异被压到接近零，成本集中在一份 server 上。**

## 6.3 LSP Server（核心资产）

- **技术栈**：C#/.NET，以 `dotnet tool` 分发（如 `myfx-lsp`）。
  必须内嵌 Roslyn Workspace 来做表达式投影和组件注册表收集——这是它比普通 LSP
  贵的原因，也是值钱的原因。
- **表达式投影机制**（核心技巧）：把模板中每个 `{expr}` 连同上下文类型环境投影成
  内存合成 C# 文件（注入 code-behind 字段声明构造完整类型上下文），向 Roslyn 要
  补全/悬停/定义，按 span 映射表换算回 `.bui` 偏移。重命名时 Roslyn 改 `.cs`，
  同时反扫所有模板投影表达式同步改 `.bui`。
- **组件注册表驱动补全**：标签补全列可见组件；属性补全按目标类型过滤（含附加属性按
  owner 过滤、事件显示委托签名）；第三方包组件自动出现。
- **诊断双通道**：生成器诊断（编译期，带 span 映射）+ LSP 诊断（编辑中实时），
  指向同一 span 映射表，红的位置一致。

## 6.4 VS Code 扩展（第一优先，成本最低）

- 扩展捆绑 grammar + snippets + LSP 客户端启动逻辑（约百行 TS）。
- 激活时以 stdio 拉起 `dotnet tool` 版 server；检测不到 .NET SDK 时给明确引导。
- 受益于 C# Dev Kit 生态：解决方案加载、热重载按钮、调试宿主现成。

## 6.5 Rider 插件（第二优先，lsp4ij 生态）—— ✅ 首选路径已落地（2026-11-12）

已核实的事实基础：lsp4ij（Red Hat，EPL-2.0）支持所有 IntelliJ 系 IDE 含 Rider，
要求 2024.2+，经 `com.redhat.devtools.lsp4ij.server` / `languageMapping` 扩展点把
`.bui` 关联到 stdio LSP server。

**落地形态**（`ide/rider/`，详见 [ide/README.md §4](../ide/README.md)）：JDK 17 +
Gradle 9 的 IntelliJ 平台插件，目标 `RD/2024.2`（build 242），依赖
`com.redhat.devtools.lsp4ij` 0.21.0 与 `org.jetbrains.plugins.textmate`；
Language + FileType 两对、TextMate bundle provider 与显式高亮绑定、
`LanguageServerFactory` 拉起同一份 stdio server，共 10 个 Java 类 + 1 个 plugin.xml。
grammar 仍由 `ide/vscode/syntaxes` 单一来源经 Gradle 的 `syncTextMateBundle` 组装。

实现时发现并补掉两处协议假设：① lsp4ij 只认 `client/registerCapability` 动态注册
watcher（其 `WatchedFilesTest` 为证），而 v1 只靠 VS Code 客户端的静态 watcher——
故 server 在 `initialized` 后动态注册六项 glob，两个 IDE 共用标准 LSP；
② IntelliJ 的 `file:/D:/…` / `file://D:/…` URI 形态补进 `UriToPath` 的回归夹具。
静态契约（`ide/rider/test/plugin.test.js`）交叉比对扩展点名、类名、grammar
scopeName、watcher glob 与启动参数，无需 JVM；IDE 侧 199 例全绿。
真实 `compileJava` / `buildPlugin` 已在 Temurin 17 + Gradle 9.0.0 wrapper 下通过
（产物 `ide/rider/build/distributions/bubuui-rider-0.1.0.zip`，内含共享 grammar 与
两条 languageMapping）；**仍未做** Rider 内人工冒烟，以及 `eui-decompiled:` 虚拟文档
在 Rider 的 content provider（VS Code 独有）。

- **首选路径（✅ 已实现）**：极小插件，只做两件事——注册 TextMate bundle
  （grammar 共享）+ lsp4ij 扩展点声明 server。
- **过渡路径（保留）**：不构建插件时，仍允许手动配置 lsp4ij 的 User-defined server。
- **备选路径（未启用）**：JetBrains 内置 LSP API（`com.intellij.platform.lsp`）在 Rider
  稳定后平滑迁移，server 侧零改动。
- **平台注意点（已实现）**：`.bui`/`.bss` 先关联到插件注册的 Language + FileType
  才能挂高亮与 LSP；自定义 FileType 会压掉 TextMate 着色，需显式绑定
  `TextMateEditorHighlighterProvider` / `TextMateSyntaxHighlighterFactory`。

## 6.6 两家差异矩阵

| 事项 | VS Code | Rider |
|---|---|---|
| 语法高亮 | TextMate 原生 | TextMate bundle 导入（同一份 grammar） |
| LSP 客户端 | 内置 | lsp4ij（或未来内置 API） |
| 调试/热重载 | C# Dev Kit 现成 | ReSharper 后端 + DAP；`.bui` 断点映射靠 `#line`（P3） |

## 6.7 发布策略与决策记录

- grammar、server、两个壳同仓库**锁步发版**——LSP 协议有版本兼容面，
  避免"扩展比 server 新"的碎裂。
- **VS 本体（MEF 语言服务）不进 MVP**：仅靠生成器诊断 + TextMate 高亮兜底，
  视社区需求再评估。
- Neovim/Zed 的 Tree-sitter grammar 为顺手产出，非主优先级。

## 6.8 分期

- **P0（约 1–2 个月）**：grammar + server 诊断通道（生成器 span 映射先跑通）
  → VS Code、Rider 同时"无补全但全量报错"，内部可用。
- **P1（约 2–3 个月）**：server 补全/悬停/跳转 → 两个壳同日发布，
  **功能天然同日同版**（同一 server）。
- **P2**：重命名跨文件联动、代码操作（"提取为组件"、"生成缺失的 code-behind 成员"）、
  语义着色、格式化。
- **P3**：`#line` 调试映射、热重载协议接入、Profiler/DevTools 面板。

> 压缩原则：LSP 是仅次于文本排版的第二贵资产（全职人力半年以上到 P2）。
> 资源紧张时砍 IDE 功能档位（先 VS Code 单平台），**不砍编译器诊断质量**——
> 诊断是底线，补全是加分。

## 6.9 目标架构：项目系统驱动的单一 Compilation（提案，待拍板）

> 状态：**提案，未落地**（2026-11-11 审计产出）。本节取代 §6.3 的 v1 阶段性决定
> 「由 `UiCatalogFactory` 从程序集符号派生，而非 MSBuildWorkspace 加载项目」。
> **验收由 §6.9.0 的用户五条要求定义**（`@using`/别名、标签补全、属性补全、属性与标签转定义、
> 内嵌 C# 完整语言服务），架构只是达成手段。
> 落地后按 [spec/02](../spec/02-generated-code-contract.md) 台账记 D 编号；在此之前不占号。

一句话：**一个项目系统，一份项目 Compilation，所有 IDE 能力都从它派生。**

本节由 §6.9.0 的五条要求驱动：要求是验收，架构是手段。§6.9.7 把实施项按"闭合哪条要求"
归类，并标明各自依赖哪一层。

### 6.9.0 验收标准（用户五条要求，2026-11-11 拍板）

| # | 要求 | 现状 | 缺口 |
|---|---|---|---|
| 1 | `@using` 与 `@using` 别名 | `@using` 可见性过滤（K22）与别名解析（K31）已落地——[DocumentAnalysis.cs:300](../ide/BubuUI.LanguageServer/Analysis/DocumentAnalysis.cs#L300) / [:309](../ide/BubuUI.LanguageServer/Analysis/DocumentAnalysis.cs#L309) | **别名不进补全**：[CompletionProvider.cs:145](../ide/BubuUI.LanguageServer/Analysis/CompletionProvider.cs#L145) `Tags()` 只发 `element.Tag`，整个文件不读 `Aliases`；`@using Alt = Ns.Type` 的右侧类型名也不补（[:354](../ide/BubuUI.LanguageServer/Analysis/CompletionProvider.cs#L354) `Namespaces()` 只列命名空间） |
| 2 | 输入标签列出全部合法控件 | 已实现：遍历 `catalog.Elements`，未导入的标 `needs @using` 并降序排序（`@namespace` 链内的命名空间算已可见，K33 / D111——`DocumentAnalysis.IsNamespaceVisible` 与解析共用一条判定） | 合法性依赖目录完整性：自家项目控件**必须先 build**（§6.9.1 后果 1）；`.bui` 派生组件不进目录（需 L3） |
| 3 | 输入属性列出当前控件全部可用属性 | 已实现：属性 / 事件 / 组件参数 / 附加属性 / 保留属性 / `@bind-` 只列**支持绑定**的目标（与 EUI040 同一份判定，spec/01 §4.5）（[CompletionProvider.cs:184](../ide/BubuUI.LanguageServer/Analysis/CompletionProvider.cs#L184)） | 与第 2 条**同一个根因**：`analysis.AttributeOwner` 解析不出元素就返回空列表（[:195](../ide/BubuUI.LanguageServer/Analysis/CompletionProvider.cs#L195)），控件不在目录里则一个属性都没有 |
| 4 | 属性与标签都能转定义（有源码跳源码，无源码反编译） | **标签**四路已落地，含 ILSpy 反编译兜底（[DefinitionProvider.cs:156](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L156)）；表达式 / `@code` 标识符走探针符号反查 | **属性完全没做**：[DefinitionProvider.cs:32](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L32) 的 switch 无 `AttributeName` 分支 → F12 返回 null；标签走正则扫文本、同名取第一个，会跳错命名空间 |
| 5 | C# 语言支持（**已拍板 = 内嵌 C# 完整语言服务**） | **落地前**：`{expr}` 的补全 / 悬停 / 跳转 / 重命名已有（[LanguageServerHost.cs:51](../ide/BubuUI.LanguageServer/Server/LanguageServerHost.cs#L51) 起至 `:66`）；`@code` **只有跳转**（[DefinitionProvider.cs:36](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L36) `CodeBlockDefinition`），补全 / 悬停 / 重命名**从未接入**——三个 provider 都只判 `CursorKind.Expression` | **落地前**还缺 signature help / 语义着色 / 代码操作 / 格式化（host 只注册 6 个 `textDocument/*` 方法）、Tier 2 诊断，且可见源码只有本文件 code-behind。**落地后（P8）**：前两类已闭合（host 现注册 11 个 `textDocument/*`），可见源码随 L2 扩到整个项目；**`@code` 的补全 / 悬停 / 重命名仍未做**，原文"`@code` 已有"是误判，机制与修法见 §6.9.10 |

第 5 条的边界：在 `.bui` 内提供与 `.cs` 同档的 C# 语言服务，**不接管 `.cs` 文件的语言服务**
（那由 C# Dev Kit / ReSharper 负责，§6.2 两个薄壳的口径不变）。

「同档」目前只在 `{expr}` 一侧成立；`@code` 块**低于** `.cs` 同档——缺补全、悬停与重命名。
两块的逐项对照见 §6.9.10，不要把这句话读成两块对称。

**关键读法**：第 2、3 条不是功能缺口，是**类型可见性**缺口——同一个 L1/L2 项目编译同时闭合两条；
第 1 条是纯功能缺口，不依赖架构；第 4 条是功能缺口但**必须搭 L2 的车**
（正则认不出成员声明）；第 5 条是唯一的大块新增投入。

#### 三项的实现口径

**别名补全（第 1 条）**：`Tags()` 除 `catalog.Elements` 外追加 `DocumentAnalysis.Aliases`
的键（[DocumentAnalysis.cs:107](../ide/BubuUI.LanguageServer/Analysis/DocumentAnalysis.cs#L107)
已是现成的 `IReadOnlyDictionary<string, string?>`），detail 指向别名目标类型。
别名是一体两用（C# 别名 + 标签别名，K31），因此表达式补全侧的别名类型走 Roslyn 自然可见，
不需要另做。`Namespaces()` 的右侧类型名补全**不列入第 1 条验收**。

**属性转定义（第 4 条）**：落点是**成员**而非类型，目录已带够信息——
[UiCatalogModel.cs:63](../src/BubuUI.Compiler/UiCatalogModel.cs#L63) `UiPropDef` 有
`DeclaringTypeName` / `Name` / `PropertyFieldName`，[:75](../src/BubuUI.Compiler/UiCatalogModel.cs#L75)
`UiEventDef` 有 `DeclaringTypeName` / `EventName` / `FieldName`。
今天 `TagDefinition` 的正则只匹配**类型声明**（[DefinitionProvider.cs:24](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L24)
`TypeDeclarationRegex` 只认 class/record/struct/interface），扩正则去认成员等于把第二事实源
做得更深。**所以属性转定义与标签转定义合并为一件事：一起换成 L6 符号查询**（§6.9.3），
反编译兜底同样复用——成员级反编译定位由 `DecompileProvider` 承接。

**内嵌 C# 完整语言服务（第 5 条）**：全部能力挂在**同一份项目 Compilation**（L2）上，
不引入第二套类型环境。分项：Tier 2 实时类型诊断（§6.9.4）、signature help、语义着色
（`textDocument/semanticTokens`）、代码操作、格式化。`{expr}` 与 `@code` 的偏移回映已有
（`TryMapMemberSpanToUi` / 块体逐字透传），新增能力复用它，不另造映射。

**注（2026-11-11 落地后补）**：这份分项清单是"补什么"，不是"已有什么"——补全 / 悬停 /
重命名不在其中，因为它们被认为"已有"。但那只对 `{expr}` 成立；`@code` 一侧除了跳转之外
一直为空，P8 完成后依然如此。§6.9.0 第 5 条原文"`@code` 的补全、悬停、跳转、重命名已有"
是这处遗漏的源头，已订正；逐项对照与修法见 §6.9.10。

### 6.9.1 问题：四套互不一致的「可见源码」口径

| 能力 | 它看到的源码 | 类型来源 | 代码位置 |
|---|---|---|---|
| 标签目录 | **零语法树** | dll 元数据（含自家项目） | [CatalogProvider.cs:155](../ide/BubuUI.LanguageServer/Workspace/CatalogProvider.cs#L155) |
| 表达式探针 | 只有本 `.bui` 的 `{Name}.cs` + `@code` | 目录的 base compilation | [DocumentAnalysis.cs:243](../ide/BubuUI.LanguageServer/Analysis/DocumentAnalysis.cs#L243) |
| 重命名 | **整个工作区所有 `.cs`** | 临时 `AddSyntaxTrees` | [RenameProvider.cs:332](../ide/BubuUI.LanguageServer/Analysis/RenameProvider.cs#L332) |
| 跳转定义 | **正则扫工作区文本**，同名取第一个 | 目录的 `ClrTypeDisplayName` | [DefinitionProvider.cs:265](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L265) |

`CSharpCompilation.Create("BubuUICatalogProbe", Array.Empty<SyntaxTree>(), …)` 的
`compilation.Assembly` 是一个零类型空壳，于是 [UiCatalogFactory.cs:168](../src/BubuUI.Compiler.Metadata/UiCatalogFactory.cs#L168)
的两格扫描口径里**第①格（源程序集）恒空**，自家项目的类型只能从第②格（元数据引用）进来。
编译器侧走的是第①格：[TemplateGenerator.cs:40](../src/BubuUI.Compiler.Generator/TemplateGenerator.cs#L40)
拿 `context.CompilationProvider`，应用集自定义控件是 source symbol，根本不需要 dll。

**已实测的三个后果**：

1. **自家控件必须先 build 才有提示**。触发案例（2026-11-10，最小变量对照，同一份
   `samples/Gallery/Counter.bui` + 同一个 `--workspace`，只换 server dll）：

   | server | 目录标签数 | `<MyControl />` |
   |---|---|---|
   | `ide/vscode/server/`（23:24 发布，扩展实际加载） | 9 | `EUI001 unknown tag 'MyControl'`，hover 返回 null |
   | 仓库 Debug 构建（23:38，含 `WorkspaceAssemblies`） | 14 | 零诊断，hover 显示 `Gallery.MyControl` |
   | 旧 server + `--assembly …/Gallery.dll` | 14 | 零诊断 |

   > 注：上表是 2026-11-10 的历史实测，夹具控件 `Gallery.MyControl` 已于 2026-11-13
   > 从 `samples/Gallery` 移除；结论（自家控件需先 build 才有提示）不受影响，当前 IDE
   > 回归夹具改用 `.bui` 派生组件 `Gallery.Counter` 覆盖别名解析。

   第三组证明扫描器、`@using Gallery` 可见性过滤（K22）、类型暴露判定全部正确；
   直接原因是扩展优先加载打包目录（[extension.js:23](../ide/vscode/client/extension.js#L23)）
   而该产物过期。**但即使产物最新，"必须先 build" 这一条仍然存在**——那是架构问题，不是发布问题。

2. **诊断码与位置与 build 分叉**。`IsFromReferencedAssembly` 的算法是
   `assembly != compilation.Assembly`（[UiCatalogFactory.cs:118](../src/BubuUI.Compiler.Metadata/UiCatalogFactory.cs#L118)），
   而 IDE 的 `compilation.Assembly` 是空壳 → **所有**扫进来的程序集恒为 true，包括自家项目。
   后果在 [Analyzer.cs:799](../src/BubuUI.Compiler/Analyzer.cs#L799) 与
   [Analyzer.cs:999](../src/BubuUI.Compiler/Analyzer.cs#L999)：引用程序集控件的声明问题走
   **使用点** EUI019/020/021，源项目控件走**声明处** EUI026 + 用点 EUI002/EUI006
   （[design/14 §14.9.2](./14-control-template-contract.md#L739)）。同一个非法 `[UiContent]` 目标，
   IDE 与 `dotnet build` 给不同码、不同位置——直接违反 §14.10.5「同码同 span」。

3. **定位与改名不可靠**。跳转定义"同名取第一个"会跳错命名空间；重命名每次全工作区
   `ParseText` 且跨项目，既慢又有误改风险。

另：[extension.js:73](../ide/vscode/client/extension.js#L73) 的 `documentSelector` 只有 `bubuui`，
**`.bss` 今天完全没有 LSP 诊断**（只有 grammar），而生成器侧 `ExecuteStylesheet` 是报诊断的。

### 6.9.2 架构不变量（功能验收看 §6.9.0）

1. 目录只有一个事实源 `UiCatalogFactory.Build(Compilation)`，LSP 不得另建目录
   （[design/14 §14.10.5](./14-control-template-contract.md#L858)）。
2. IDE 与 `dotnet build` **同码同 span**。
3. 不做没有数字支撑的预优化——目录构建实测 10–20ms（§14.10.3），**不需要精细增量**。
4. 两个薄壳，逻辑全在 server（§6.2）→ **不得依赖 VS Code C# Dev Kit 的项目系统**，Rider 要走同一份。

### 6.9.3 分层设计

```
.cs / .bui / .bss / .csproj 变更
        ↓ watcher (L7)
L1 项目系统：document → csproj → MSBuild 求值 + project.assets.json
        ↓
L2 ProjectModel（每项目一份，缓存）
     Compilation = 真实源码 + 精确引用 + 正确选项
        ↓
L3 跑真 TemplateGenerator → 生成树（带 #line）加回编译
        ↓
L4 UiCatalogFactory.Build(...)   ←── 与编译器同一个函数，一行不改
        ↓
L5 诊断双通道 · L6 跳转/重命名 · 补全/悬停/大纲
        ↓
L8 内嵌 C# 语言服务（签名帮助 / 语义着色 / 代码操作 / 格式化）
```

**L1 项目系统（唯一真新增）**

- **口径总则（2026-11-11 拍板）：编译时按什么，IDE 就按什么。** 下面每条都是这条总则的推论。
- `document → 最近祖先 csproj` **只是候选定位**；真正的归属判据是**求值后 `AdditionalFiles`
  是否命中该文件**——生成器只吃 `AdditionalTextsProvider`
  （[TemplateGenerator.cs:34](../src/BubuUI.Compiler.Generator/TemplateGenerator.cs#L34)
  `Where(ext == ".bui")`），不看 slnx、不看目录远近。推论：
  - 一个 `.bui` 被多个项目收 → 编译时两边各生成一份，IDE 也算两份，**不选一个**。
  - 没被任何项目收 → 编译时压根不参与，IDE **不给目录、不给诊断**（提示"不属于任何项目"）。
  - `.slnx` **不参与归属**：它在 `dotnet build` 里只是"构建哪些项目"的清单，不影响单项目编译内容；
    且本仓 [BubuUI.slnx](../BubuUI.slnx) 只列 16 个项目而仓库有 19 个 csproj
    （3 个 sandbox 不在内），拿它当判据会误判。slnx 至多用于扩大"已知项目集合"（服务 P9）。
- 用 **MSBuild 求值**（`Microsoft.Build.Evaluation.Project`），**不是 build，也不是 design-time build**：
  取 `Compile` items、`AdditionalFiles`、`RootNamespace`、`Nullable`、`LangVersion`、
  `ImplicitUsings`、`DefineConstants`。求值 = 读 csproj + 递归处理 SDK imports + 算属性最终值
  + 展开 Item 的 glob/Condition，**不执行任何 Target**；因此正确处理 conditions 与
  [Directory.Build.props](../Directory.Build.props)。
- **求值的固有边界（已核实，SDK 10.0.400）**：默认 `Compile` glob 连同 bin/obj 排除写在
  `Microsoft.NET.Sdk.DefaultItems.props:36`（`.props` → 求值阶段，**拿得到**）；
  但 `CoreGenerateAssemblyInfo`（`Microsoft.NET.GenerateAssemblyInfo.targets:189`）在**执行阶段**
  往 `Compile` 加 `obj/…/AssemblyInfo.cs`，`Microsoft.NET.Sdk.DefaultItems.Shared.targets:224`
  还会在 `DesignTimeBuild=true` 时去重 Remove+Include。**所以求值 items 与 build items 天生有差集**，
  验收必须按 §6.9.7 P2 的口径扣掉已知生成物，不能要求集合全等。
- **引用路径从 `obj/project.assets.json` 取**（restore 产物，精确到 dll，含 NuGet 与
  ProjectReference），不跑 `ResolveReferences`。
- 需 MSBuildLocator 定位 SDK——server 本来就由 `dotnet` 启动，SDK 必在。
  `Microsoft.Build` 包引用挂 `ExcludeAssets=runtime` 让 Locator 加载 SDK 里的程序集，
  编译期契约锁到**要支持的最老 SDK** 版本（版本倒挂会 `MissingMethodException`，见 R1 实测）。
- 无 assets.json（从未 restore）→ **明确降级 + 提示，不静默**。
- ⛔ **绝不自己实现 `**/*.cs` 排除 bin/obj**：那等于给"项目里有什么"造第二事实源，
  正是本节要消灭的那类 bug。**也绝不自己解析 csproj 属性**——那不是编译时的口径。

**三条线别混**（"MSBuild 求值"到底指什么）：

| 动作 | 做什么 | 谁在用 |
|---|---|---|
| **求值** evaluation | 读 csproj + 递归 SDK imports + 算属性最终值 + 展开 Item 的 glob/Condition；**不执行任何 Target** | `Microsoft.Build.Evaluation.Project`——L1 要用的 |
| **执行** execution | 跑 Target 链：`ResolveReferences` → `CoreGenerateAssemblyInfo` → `CoreCompile` → csc | `dotnet build` |
| **design-time build** | 跑 Target 但跳过真正编译，只为吐 items 给 IDE | C# Dev Kit / VS；退路 B |

**L2 项目 Compilation**

- `compilation.Assembly` 真正代表该项目，自家控件是 source symbol。
- 编译选项读 L1 真值（今天 [CatalogProvider.cs:163](../ide/BubuUI.LanguageServer/Workspace/CatalogProvider.cs#L163)
  硬编码 `Nullable enable`）。
- `ImplicitUsings` 必须展开成真实 using，否则表达式里 `List<T>`/`Task` 绑不出来。
- **打开的文档用编辑器缓冲区覆盖磁盘内容**——否则"写完未保存的 `.cs`"仍不可见，只解决一半。

**L3 在 IDE 内跑真生成器**

- `CSharpGeneratorDriver.Create(new TemplateGenerator()).RunGenerators(...)`，生成树
  `AddSyntaxTrees` 回编译再 Build 目录。`.bui` 派生组件（`Counter`/`Prefs`/`TaskList`/`Showcase`，
  由 [Emitter.cs:129](../src/BubuUI.Compiler.Generator/Emitter.cs#L129) 发射
  `partial class X : global::BubuUI.Component`）因此也在目录里，且与 build 一致。
- **障碍已定位**：LS 不引用 Generator 工程（[BubuUI.LanguageServer.csproj](../ide/BubuUI.LanguageServer/BubuUI.LanguageServer.csproj)），
  而 [Generator.csproj:17](../src/BubuUI.Compiler.Generator/BubuUI.Compiler.Generator.csproj#L17)
  把 Compiler/Metadata/Probe **源码并入**自己的程序集 → 直接引用 CS0433 类型撞车。
  解法：ProjectReference 挂 `Aliases="euiGen"`，只在入口一处用 extern alias
  （公开签名只有 `Initialize(IncrementalGeneratorInitializationContext)`，不跨界传 Compiler 类型，
  别名足够，**不必拆工程**）。
- 必须喂 `build_property.rootnamespace`（[TemplateGenerator.cs:60](../src/BubuUI.Compiler.Generator/TemplateGenerator.cs#L60)，
  已核实是它唯一读的 analyzer 选项）via 自定义 `AnalyzerConfigOptionsProvider`，
  否则无 code-behind 的 `.bui` 命名空间静默漂移（K23 ④）。
- AdditionalTexts = 项目的 `.bui` + `.bss`（[Gallery.csproj:21](../samples/Gallery/Gallery.csproj#L21)）。

**L4 目录**

- 仍是 `UiCatalogFactory.Build`，**一行不改**。
- **每项目一份**，不是每工作区一份 → 消除跨项目同名假歧义。
- `IsFromReferencedAssembly` 自动恢复正确语义（自家项目 = false），§6.9.1 后果 2 自动闭合。
- 用 `UiCatalog` 结构相等做闸门（[UiCatalogModel.cs:160](../src/BubuUI.Compiler/UiCatalogModel.cs#L160)
  已实现逐字段 Equals/GetHashCode）：值没变就不重发诊断。

**L5 诊断双通道** → 见 §6.9.4。

**L6 跳转与重命名**

- 标签跳转：`compilation.GetTypeByMetadataName(catalog.ClrTypeDisplayName)` →
  `Locations.FirstOrDefault(IsInSource)` → 真实文件；无源码位置才反编译。
  **保留现有第 4 路**（[DefinitionProvider.cs:198](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L198)），
  删掉前三路的正则扫描。
- **属性 / 事件 / 参数 / 附加属性跳转（第 4 条，今天全无）**：同一次类型查询拿到
  `INamedTypeSymbol` 后按目录的 `Name` / `EventName` / `PropertyFieldName` 取成员符号的
  `Locations` → 声明处；落点优先 CLR 属性本身，`StyledProperty` 字段作为次选。
  无源码位置走同一反编译兜底（成员级）。
- 重命名：`SymbolFinder.FindReferencesAsync` over 本项目 + 引用它的项目，
  替换"全工作区 ParseText + 签名匹配"。属正确性收益，**不在五条要求之内**，排最后。

**L7 失效**

- server 补 `workspace/didChangeWatchedFiles`（今天只有手动 `bubuui/reloadCatalog`）；
  client 注册 watcher：`**/*.cs`、`**/*.bui`、`**/*.bss`、`**/*.csproj`、
  `**/Directory.Build.props`、`**/obj/project.assets.json`。
- 变更 → 标记该项目 dirty → 重建 + 重发已打开文档诊断。
- 打开文档的缓冲区变更走 `didChange`，不依赖 watcher（未保存也生效）。

**L8 内嵌 C# 语言服务（第 5 条）**

同一份 L2 Compilation + 同一套偏移回映，逐项加 `textDocument/*` 方法：

| 能力 | 数据来源 | 依赖 |
|---|---|---|
| 实时类型诊断 | Tier 2（§6.9.4），CS 错误经 `#line` 映射 | L3 |
| signature help | `SemanticModel.GetSymbolInfo(InvocationExpression)` → 重载 + 当前参数位 | L2 + 表达式回映 |
| 语义着色 | `SemanticModel` 分类 → `semanticTokens/full`；`.bui` 只着色 `{expr}` / `@code` 区间 | L2 |
| 代码操作 | Roslyn `CodeFixProvider` 结果经回映转 `WorkspaceEdit` | L2 + Tier 2 |
| 格式化 | `@code` 块体交 Roslyn `Formatter`；模板缩进由 `FrontEnd` 的 AST 决定 | L2 |

⛔ 不接管 `.cs` 文件——`.bui` 之外的 C# 由 C# Dev Kit / ReSharper 负责（§6.2）。

**依赖增量**：`Formatter` / `CodeFixProvider` / `SymbolFinder` 都在 Workspaces 层，而
[LanguageServer.csproj:24](../ide/BubuUI.LanguageServer/BubuUI.LanguageServer.csproj#L24)
今天只引 `Microsoft.CodeAnalysis.CSharp`。这三项要加 `Microsoft.CodeAnalysis.CSharp.Workspaces`
且**必须锁同一版本 4.9.2**（避免绑到两个 Roslyn 副本，与 csproj 现有注释同一口径）。
signature help 与语义着色只需 `SemanticModel`，不引入新包。

### 6.9.4 诊断双通道

| 通道 | 触发 | 内容 | 成本 |
|---|---|---|---|
| Tier 1 | 每次按键 | `FrontEnd.Parse + Analyze` on buffer + 缓存目录 → EUI 模板诊断 | ~1ms（保留现有实现） |
| Tier 2 | debounce 300–500ms / 保存 | 对该文档跑生成器 → 生成树进编译 → `GetDiagnostics` → CS 错误经 `#line` 映射回 `.bui` | 待实测 |

**`@namespace` 契约只走 Tier 2**（2026-11-11）：EUI055（缺失）/ EUI056（与 code-behind 不一致）
由生成器发出，经同一份映射回映到 `.bui`；Tier 1 刻意不报——解析层不应被必填约束绑架，否则
编辑中间态会满屏红。回归在 `ProjectSystemTests.Fixture8`。

**`@bind-P` 的目标可绑性两条通道都报，且判定只有一份**（spec/01 §4.5）：Tier 1 从目录的
`NonBindableProperties`（不可绑成员 → 原因）直接报 EUI040，Tier 2 走同一份目录 + 生成器短路
（Error 不产码）。所以 IDE 与 `dotnet build` 对同一处给出同一个码、同一条原因；
补全（`@bind-` 之后）复用同一份判定，只列会通过的候选。

两条通道正好对应 [§14.9.2 责任划分](./14-control-template-contract.md#L739)，且与 build 同源，
**不需要 CS→EUI 翻译器**：[Emitter.cs:207](../src/BubuUI.Compiler.Generator/Emitter.cs#L207)
已发射 span 型 `#line (sl,sc)-(el,ec) "path"`，TemplateGenerator 类注释明写
「表达式原样进入生成代码，由 Roslyn 检查并经 `#line` 映射回 `.bui`」。
Tier 2 是今天完全缺失的能力：表达式类型错误现在只有 build 才知道。

`.bss` 一并接入（`documentSelector` 补 `bss`，`ExecuteStylesheet` 的诊断经 LSP 发）。

### 6.9.5 净减代码（不是净增）

**删**：
- `CatalogProvider.WorkspaceAssemblies` / `SiblingAssemblies` / `ReferencesAssembly` +
  整套 bin 探测启发式（`Take(24)` / cap 200 / 按文件名去重）→ assets.json 精确给出引用。
- `DefinitionProvider.ScanForType` / `FindTypeDeclaration` 正则扫描。
- `RenameProvider.ListWorkspaceSourceFiles` + 全工作区 `ParseText`。

**不动**：`UiCatalogFactory`、`FrontEnd`、`ExpressionProbe`、
`UiCatalog.Resolve(tag, imports, enclosingNamespaces)` 的可见性规则（K22 + K33，那是模板契约，
不是 C# 规则）、反编译兜底。

### 6.9.6 已验证的硬约束：模板表面不能交给 Roslyn

提议过「把 `.bui` 投影成合成 C#，让 Roslyn 解析标签/属性/namespace」（Razor 路子）。
**方向部分正确，但不能替代目录**，理由是模板表面是 CLR 表面的**真子集**：
[UiCatalogFactory.cs:437](../src/BubuUI.Compiler.Metadata/UiCatalogFactory.cs#L437)
`if (field is null) continue; // 普通 CLR 属性不暴露`。两个已核实反例：

| CLR 成员 | 配对 `StyledProperty` 字段 | 模板属性？ | naive 投影后果 |
|---|---|---|---|
| [VirtualizingStackPanel.cs:44](../src/BubuUI.Runtime/VirtualizingStackPanel.cs#L44) `public Func<int, Element>? ItemFactory { get; set; }` | 无 `ItemFactoryProperty` | ❌ | Roslyn **接受**，编译器报 EUI002 |
| [Component.cs:255](../src/BubuUI.Runtime/Component.cs#L255) `public Size AvailableSize { get; set; }` | 无 `AvailableSizeProperty` | ❌ | 同上，且它是**所有 `.bui` 组件的基类** |

IDE 比编译器宽松 = 假阴性，是最糟的一类漂移。要堵只能投影到"只含暴露成员的合成门面类型"，
而门面必须从目录派生 → **目录删不掉，只是多加一层**。另外 Roslyn 给不了：
属性值不是 C#（`Gap="8"` 经 `UiLiterals.TryConvert` 转 int，C# 里 `"8"` 不能隐式转 int）、
`Style="CornerRadius: 8"` 是 bss 另一门语言、`@bind-` 是绑定指令不是赋值、
`Grid.Row="1"` 要投影成静态 setter、内容模型/插槽/参数（EUI006/021/028）与 `@foreach` Key 复用规则无 C# 对应物。

**结论**：Roslyn 负责「类型与表达式语义 + 定义定位」，目录负责「模板表面合法性」。
前者已由 [ExpressionProbe.cs:40](../src/BubuUI.Compiler.Probe/ExpressionProbe.cs#L40) 与生成器
共享同一实现（「改动必须保持两侧行为逐字节一致」）；后者必须目录驱动，因为这个收窄正是 EUI002 存在的理由。
「只有非源码类型才反编译」**已是现状**（`IsInSource` 判据 + 第 4 路兜底），不需要改。

### 6.9.7 实施项与验收（按五条要求归类）

依赖关系：第 1 条不依赖任何层；第 2、3 条依赖 L1/L2（一份项目编译同时闭合两条），
`.bui` 派生组件那部分还依赖 L3；第 4 条依赖 L2；第 5 条的实时诊断依赖 L3，其余项依赖 L2。
P5 保鲜的是已有能力，本身不闭合任何一条要求。

| 项 | 内容 | 闭合 | 验收 |
|---|---|---|---|
| **P0** | 重跑 [ide/build.ps1](../ide/build.ps1) 重发布 server | 2·3 触发案例 | 触发案例的 EUI001 消失（与架构无关） |
| **P1** | 别名补全（`Tags()` 读 `Aliases`） | **1** | `@using Alt = Gallery.Counter` 后光标停在 `<Al` 列出 `Alt`，选中解析到目标类型 |
| **P2** | **spike**：MSBuildLocator + MSBuild 求值 + assets.json 读引用 | — | 手写源码一个不漏不多、属性最终值逐项等于 build 的真值；**差集只允许是已知生成物**（`obj/…/AssemblyInfo.cs` 等，见 §6.9.3 L1 边界） |
| **P3** | L1 + L2 + L4，删 dll 启发式 | **2·3** | 自家控件写完**未 build** 即可：标签列全、属性列全、零诊断 |
| **P4** | L6 符号查询：标签 + **属性/事件/参数/附加属性**转定义 | **4** | F12 落在成员声明处；同名不同类型跳对；无源码走反编译 |
| **P5** | L7 watcher + 自动 reload | 2·3·4 保鲜 | 改 `.cs`/`.csproj`/assets.json 后不再需要手动 Reload |
| **P6** | L3 生成器接线（extern alias + rootnamespace）+ `.bss` 接入 | 2·3 完整 | `.bui` 派生组件未 build 时也在目录里；`.bss` 有 LSP 诊断 |
| **P7** | L5 Tier 2 实时类型诊断 | **5** 之一 | 表达式类型错误实时报，`(code, span)` 与 `dotnet build` 一致 |
| **P8** | L8 余项：signature help / 语义着色 / 代码操作 / 格式化 | **5** 大部分（`{expr}` 完整；`@code` 仍缺补全 / 悬停 / 重命名） | 每项与同内容 `.cs` 在 C# Dev Kit 下的结果对照；上述三处不在本项范围，见 §6.9.10 |
| P9 | 重命名换 `SymbolFinder` | 非五条 | 不跨项目误改（正确性收益） |

**P2 是 P3–P8 的技术前提**：MSBuild 求值在本机 SDK 上的可用性是全案最大技术风险，
不过就退到"design-time build"或"保留 dll 目录 + 明确降级提示"——**"最小 csproj 解析"已按
口径总则出局**（自己解析 ≠ 编译时口径，§6.9.3 L1）。P1 不依赖 P2。

> **求值失败不影响构建**：`dotnet build` 用 SDK 自带的 MSBuild，与 LSP 进程内 Locator 加载的
> 那份是两条独立路径。求值挂了只掉 IDE 能力（未 build 的控件补不出来），项目照样编译得过。

**五条要求的回归夹具**（缺此必然慢慢歪回四套口径）：

| 要求 | 夹具 |
|---|---|
| 1 | 一份带 `@using Ns` 与 `@using Alt = Ns.Type` 的 `.bui`：两种写法都能解析、都能补全 |
| 2 | 新写一个未 build 的控件 `.cs` → 标签补全含它；跨命名空间同名两类型 → **解析**只认已导入的（K22）或 `@namespace` 链内的（K33），补全里未导入项标 `needs @using` 并降序 |
| 3 | 该控件上补全 == 目录该元素的属性/事件/参数全集；`ItemFactory` / `AvailableSize` **不得**出现（§6.9.6） |
| 4 | 源码属性 F12 落 CLR 属性声明；框架属性 F12 落仓库源码；NuGet 控件属性 F12 落 `eui-decompiled:` |
| 5 | 一份含类型错误的 `{expr}`：不 build 就报错，且码/span 与 build 一致 |

**另加两条架构级硬检查**：

1. **同一项目，IDE 探针目录与生成器目录结构相等**——`UiCatalog.Equals` 现成可用。
   把 §14.10.5「同码同 span」变成可执行检查。
2. **诊断对照**：同一份含错误的 `.bui`，IDE 发的 `(code, span)` 集合 == `dotnet build` 产的集合。

### 6.9.8 明确不做

- ⛔ 自己实现 csproj glob（第二事实源）。
- ⛔ CS→EUI 诊断翻译器（用真生成器 + `#line`）。
- ⛔ 精细增量缓存（§14.10.3：不做没有数字支撑的预优化）。
- ⛔ 依赖 VS Code C# Dev Kit 的项目系统（§6.2 两个薄壳，Rider 走同一 server）。
- ⛔ 把模板表面合法性交给 Roslyn（§6.9.6 反例）。
- ⛔ 接管 `.cs` 文件的语言服务（第 5 条只在 `.bui` 内提供 C# 能力，§6.2）。
- ⛔ 为属性转定义扩正则去认成员声明（走 L6 符号查询，§6.9.0）。

### 6.9.9 风险与待拍板

| # | 风险 | 处置 |
|---|---|---|
| R1 | MSBuildLocator / 求值在本机 SDK 上可用性 | 由 P2 spike 判定。**实测（2026-11-11）**：无 `global.json`，`dotnet --version` = 11.0.100-preview.7.26381.103；三个 SDK 的 `Microsoft.Build.dll` = 17.11.48（8.0.421）/ **18.9.6**（10.0.400）/ **18.10.0**（11 preview）；NuGet 最新 `Microsoft.Build` = **18.11.0**、`Microsoft.Build.Locator` = **1.11.2** → 默认 SDK 比包**旧**，必须 `ExcludeAssets=runtime` + 契约锁最老支持版本 |
| R2 | 生成器 extern alias 接线（源码并入导致类型撞车） | **已闭合**：`Aliases="euiGen"` + `PrivateAssets="all"`；P6 实测生成 5 棵树、编译错误 10 → 0、目录 10 → 14 标签 |
| R3 | Tier 2 `GetDiagnostics` 是全编译绑定，大项目可能 >200ms | debounce + 只算打开文档；增量按实测数字决定（§6.9.2 第 3 条） |
| R4 | 缓冲区覆盖磁盘的实现复杂度（未保存 `.cs` 也要可见） | L2 必须做，否则只解决一半 |
| R5 | 第 5 条完整语言服务是本案最贵一段（L8 四项 + Tier 2） | 每项单独对照 `.cs` 验收；P0–P6 不依赖它 |
| R6 | 代码操作/格式化要把 Roslyn 的 `WorkspaceEdit` 经回映改写回 `.bui` 偏移 | 复用 `TryMapMemberSpanToUi`；映不回的编辑**丢弃而不是猜**（与本仓「不猜」口径一致） |
| R7 | Workspaces 层新包（格式化/代码操作/`SymbolFinder`）体积与版本绑定 | 锁 4.9.2 与现有 Roslyn 同版；仅 P8/P9 需要，P0–P7 不涉及 |

**已拍板（2026-11-11）**：
- 第 5 条 = 内嵌 C# 完整语言服务（不接管 `.cs`）；实施项按五条要求归类。
- **口径总则：编译时按什么，IDE 就按什么。** 由此定死归属判定 = 求值后 `AdditionalFiles`
  命中（`.slnx` 不参与），并直接判掉"最小 csproj 解析"退路。详见 §6.9.3 L1。

**待拍板（已作废）**：P2 spike 失败时的退路取哪条——

| 退路 | 收益 | 代价 |
|---|---|---|
| **B** design-time build | 权威、不漂，与编译口径完全一致 | 每次秒级；要跑 `ResolveReferences`；需引 Workspaces 层；与 §6.9.2 第 3 条的性能口径冲突 |
| **C** 保留今天的 dll 目录 + 明确降级提示 | 不新增第二事实源，改动最小 | 第 2/3/5 条不闭合（自家控件仍必须先 build） |

原选项 A（最小 csproj 解析）已被口径总则排除。**P2 spike 实测通过，退路全部不再需要**；
C 只保留为「无 csproj / untitled / 内存态」的运行时 fallback，不是方案退路。

#### P0–P8 落地时实测修正的三处（2026-11-11）

| # | 症状 | 根因 | 修法 |
|---|---|---|---|
| 1 | 发布版一启动就 `FileNotFoundException: Microsoft.Build, Version=15.1.0.0` | `ExcludeAssets=runtime` 下输出目录没有该 dll，全靠 Locator 的解析回调；CLR 在 JIT 方法体时就要解析类型标记，把 `Ensure()` 和 `new ProjectCollection()` 放在同一方法里已经太晚 | 注册提前到 `ProjectWorkspace` 构造函数，`Ensure()` 加 `NoInlining`；`MsBuildEnvironmentTests` 回归 |
| 2 | 19 个 csproj 只求值出 10 个，`src/*` 框架源码工程被静默丢弃 | 共用 `ProjectCollection` 在解析 ProjectReference 时已加载被引用工程，之后 `new Project(...)` 再加载同一路径抛「已有等效项目」 | `ProjectEvaluator.GetOrLoad` 先查 `LoadedProjects` 复用 |
| 3 | `.bss` 的 EUI050–052 一条都不发 | 生成器用 `Location.Create(path, …)` 报诊断，位置不是 `IsInSource`；`MapDiagnostics` 按 `IsInSource` 过滤后整批丢掉 | 统一用 `GetMappedLineSpan()` 取文件与行位，生成树的 `#line` 与外部文件位置都能映回 |

另有一处口径修正：无文档上下文的 `DefaultCatalog` 原本取「遍历到的第一个项目」（可能是不含
`.bui` 的测试工程），改为优先取收了 `.bui/.bss` 的项目；`FindSourceTypes` 用只解析源码树的
轻量编译做跨项目符号查询，避免一次 F12 把整个解决方案的 Compilation 都算一遍。

### 6.9.10 L8 的覆盖边界：`{expr}` 与 `@code` 不对称（2026-11-11 补记）

**结论**：`@code { }` 块内**没有补全、没有悬停、不能重命名**。这不是 P0–P8 的回归，是从来没有
实现过——补全/悬停/重命名三个 provider 的分派键只有 `CursorKind.Expression`，`@code` 的
`CursorKind.CodeBlock` 落进各自的 `default`。

#### 逐项对照

| 能力 | `{expr}` / 属性表达式 | `@code { }` |
|---|---|---|
| 补全 | ✅ 成员 / 裸标识符 / `@foreach` 变量 | ❌ 落 `default` 返回空 |
| 悬停 | ✅ 类型 / 可写性 / 方法组 / 目录成员文档 | ❌ 无 `CodeBlock` 分支 |
| F12 跳转 | ✅ `.bui` 回映 / 兄弟 `.cs` / `eui-decompiled:` / 跨项目源码 | ✅ `CodeBlockDefinition` |
| 重命名 | ✅ 含 `@foreach` 迭代变量 | ❌ `ResolveTarget` 硬判 `Expression` |
| 签名帮助 | ✅ | ✅ 同一投影 |
| 语义着色 | ✅ | ✅ 同一投影 |
| Tier 2 诊断 | ✅ 经 `#line` 回映 | ✅ 同 |
| 快速修复 | ✅ EUI002 / CS0103（属性表达式）、EUI001（标签名） | — 不适用（不在属性上下文） |
| 格式化 | 随模板缩进 | ✅ Roslyn `Formatter` |

分派位置：[CompletionProvider.cs:21](../ide/BubuUI.LanguageServer/Analysis/CompletionProvider.cs#L21)
的 `switch`（七个分支，无 `CodeBlock`）、[HoverProvider.cs:16](../ide/BubuUI.LanguageServer/Analysis/HoverProvider.cs#L16)
的 `switch`（同）、[RenameProvider.cs:122](../ide/BubuUI.LanguageServer/Analysis/RenameProvider.cs#L122)
的 `if (context.Kind != CursorKind.Expression …) return null`。`@code` 唯一接上的是
[DefinitionProvider.cs:119](../ide/BubuUI.LanguageServer/Analysis/DefinitionProvider.cs#L119)
的 `CodeBlockDefinition`。

#### 为什么不能"加个分支就完事"

补全成立需要三个前提，`@code` 一个都不具备：

1. **入口**：上面三个 `switch` 没有分支，先要有地方接。
2. **词法上下文**：[CursorScanner.cs:236](../ide/BubuUI.LanguageServer/Analysis/CursorScanner.cs#L236)
   判定 `CodeBlock` 时只做了 `TokenStart = offset`，既没有表达式区间也没有"当前标识符前缀"。
   补全无法判断光标前是 `Na` 还是 `task.Na`，也就定不出替换区间与是否走成员选择。
   `{expr}` 侧有完整的 `ExpressionStart` / `ExpressionEnd` 交给补全器（[:376](../ide/BubuUI.LanguageServer/Analysis/CursorScanner.cs#L376)）。
3. **候选与上下文规则**：`{expr}` 的候选是三段拼起来的——`ExpressionEnvironment`（foreach 变量）+
   code-behind 与 `@code` 成员 + `InferType(receiver).GetMembers()`。这套成立是因为表达式永远处在
   "字段 / 成员选择"语义里。`@code` 块内是自由语句：光标可能在局部变量声明后、`if` 条件里、
   lambda 体内，候选必须包含该位置**可见**的局部变量、参数、类型名与语句关键字，还要按上下文过滤
   （`var x = ta|` 该出类型，`if (|` 该出布尔表达式，`await |` 只出可等待）。`Members()` 那套没有
   "当前位置上下文"的概念。

#### 为什么会漏掉（两处文档误判）

§6.9.0 第 5 条原文写的是"表达式 / `@code` 的补全、悬停、跳转、重命名已有"，把两个区间当成
同一套入口的并集；真实情况是只有**跳转**对 `@code` 单独接了分支。同一误判也写在
[ide/README.md](../ide/README.md) 的能力表里（"`{expr}` 与 `@code` 走 Roslyn 投影" 那一行），
两处均已于本次订正。P8 的分项清单（signature help / 语义着色 / 代码操作 / 格式化）因此不包含
补全——既然"已有"，就不必再排。证据：
`git log -S"CursorKind.CodeBlock" -- ide/BubuUI.LanguageServer/Analysis/CompletionProvider.cs`
为空，说明该分支在历史上从未出现过。

#### 修法（未实施）

| 路线 | 做法 | 覆盖度 | 成本 |
|---|---|---|---|
| **A** | `CursorScanner` 补 `CodeBlock` 的词法前缀与替换区间；候选用 `SemanticModel.LookupSymbols(treeOffset)` 取该位置可见符号，配关键字表与少量上下文规则 | 日常够用：成员 / 局部变量 / 字段 / 类型名 | 数百行，不引新包 |
| **B** | 接 Roslyn `CompletionService`，需要 `AdhocWorkspace` + `Document` 投影（不能只 `AddSyntaxTrees`） | 与 `.cs` 同档：推荐成员、snippet、泛型方法组、`await` 过滤 | 引 Workspaces 服务层，命中 R7 的包体积 / 版本绑定 |

排序建议：**悬停 → 补全 → 重命名**。补全最贵；悬停与重命名便宜得多——`EmbeddedCSharpService.At`
已经为两种区间建好投影，`CodeBlockDefinition` 已经能取到光标处的符号，二者可直接复用。

#### 若做，验收标准

1. `@code` 内 `task.` 列出 `TaskItem` 实例成员（与 `.cs` 同内容对照）
2. `var t = ta|` 列出类型名，`ta` → `TaskItem`
3. `if (|` 给出布尔表达式候选，而不是成员列表
4. 悬停能给出局部变量 / 字段的类型与目录成员文档
5. 重命名能覆盖 `@code` 内的局部变量与 code-behind 字段引用，且不误改其他类型同名成员

**待拍板**：是否按 A 路线补齐这三项（只做悬停 / 三项一起做），以及是否接受 B 路线引入
Workspaces 服务层的依赖。在拍板前，`@code` 的补全 / 悬停 / 重命名按**已知缺口**对待，
不计入第 5 条已闭合范围。

> 2026-11-11 补记：把 Razor/Blazor 的生成文档模型、VS Code 虚拟 HTML 链路与 BubuUI 的对照
> 整理成了 [18 · Razor C# 智能提示借鉴](./18-razor-csharp-intellisense.md)。本文的 A/B 路线在
> 该文 §5 展开为 P0–P3，并给出落地顺序、验收清单与待拍板项。

> 2026-11-11 再补（阶段 0 落地）：design/18 的**阶段 0 已实施**——生成器旁路产出 `SourceMapping`、
> 新增统一 `DocumentMappingService`（双向、完整包含才映射、失败不猜）、`bubuui/generatedDocument`
> 可观测性请求；现有诊断/跳转/`@code` 投影改走该服务，行为不变。落地形态与关键偏差
> （4.9.2 无 `RegisterHostOutput` → 静态 opt-in 旁路；生成树 `FilePath` 带生成器前缀；
> `CompletionService`/`Renamer` 的 4.9.2 真实签名；静态 run 词法 Span 越界 → `mappingSpan`）
> 见 [design/18 §12](./18-razor-csharp-intellisense.md)。**注意**：阶段 0 只是映射底座，
> 上表 `@code` 的补全/悬停/重命名三项缺口**仍未闭合**（属阶段 1–3），在它们落地前继续按已知缺口对待。

> 2026-11-11 三补（阶段 1 落地）：design/18 阶段 1 已实施——`@code` 的**悬停与重命名闭合**
> （上表第 4、5 条验收）：共享入口 `EmbeddedCSharpService.CodeBlockSymbolAt`（真实成员树绑定 +
> 统一映射服务回映），`HoverProvider`/`RenameProvider` 各增 `CodeBlock` 分支；局部变量/参数按
> **声明源区间**匹配引用（跨编译 identity 不可靠、同名会误伤），任一处映不回整体拒绝。
> 阶段 1 重命名沿用 `SemanticModel` 引用扫描（`Renamer`/`Solution` 需阶段 2 的 Workspace，留阶段 3）；
> `@code` 里声明的成员暂拒绝重命名（阶段 3 覆盖）。**仅剩补全**未闭合（阶段 2–3）。
> 细节与偏差见 [design/18 §12.9](./18-razor-csharp-intellisense.md)。

> 2026-11-11 四补（阶段 2 落地）：design/18 阶段 2 已实施——`@code` 的**补全闭合**
> （上表第 1–3 条验收）：每快照一个 `AdhocWorkspace`（`EditorDocumentWorkspace`，与 L3 同一份
> Compilation 派生），`@code` 内补全直接跑 Roslyn `CompletionService`——局部变量/关键字/
> snippet/扩展方法/await 上下文全部来自 Roslyn，不手写枚举；候选编辑区间经统一映射服务
> 回映、映不回丢项。B 路线的 Workspaces/Features 依赖已接受（锁 4.9.2）。**注意**：await
> 上下文实测为「装饰不过滤」（可等待类型加描述前缀，候选不裁剪），与 `.cs` 一致；
> `{expr}` 补全迁移与 `Renamer` 重命名属阶段 3，未闭合。细节、API 真相与延迟实测
> （cold 282–1445ms / warm 71ms）见 [design/18 §12.10](./18-razor-csharp-intellisense.md)。

> 2026-11-11 五补（阶段 3 落地）：design/18 阶段 3（§5.6）已实施——**`{expr}` 补全迁到真实
> 生成文档**（项目模式下 `{task.}`/裸标识符/foreach 内 `{t.}` 都跑 Roslyn `CompletionService`，
> 与 `.cs` 同机；legacy 探针保留给无快照场景），**重命名改走 `Renamer` + Solution diff**
> （源 `.cs` 直接交付，生成文档编辑经统一映射服务回映 `.bui`，任一处映不回整体拒绝）。
> 第 5 条（重命名）自此覆盖 `@code` 声明成员、跨文档引用（含其他 `.bui`）与 `@bind` 双出现去重；
> `@foreach` 迭代变量因生成代码里是无映射的 lambda 参数，预路由回 legacy 文档内路径。
> **两个实测偏差**：① `SourceText.GetTextChanges` 对多处小改会退化成整篇替换，改用
> `Document.GetTextChangesAsync`（自动回落语法树 diff）；② 4.9.2 的 Renamer 对可解冲突
> （同名成员）不抛异常，交付前比对编译错误数、引入新错误即拒绝。细节与 API 订正见
> [design/18 §12.11](./18-razor-csharp-intellisense.md)。

> 2026-11-11 六补（阶段 4 落地）：design/18 阶段 4（§5.8）已实施——**语义着色改走 Roslyn
> `Classifier`**：项目模式对生成文档整篇分类，逐区间经统一映射服务回映 `.bui`（`{expr}`/`@code`
> 逐字区间映得回，生成独有代码无映射天然丢弃，模板表面仍归 TextMate），映不回丢弃不猜；
> legacy 回落探针投影，行为不回归。**代码修复与签名帮助保留手写**：4.9.2 的 `CodeFixService`
> 与 `SignatureHelpService`（含全部内置 `CodeFixProvider`）经反射核对都是 **internal**，无公开
> 入口，接它只能反射 internal API、撞「锁版本 / 不猜」硬边界——故模板级 EUI001/EUI002 与
> CS0103 缺失成员修复、`SignatureHelp` 均保留（§5.8 本就允许「否则保留」）。**诊断一致性**
> 由构造保证：补全 / 重命名 / 着色同走 `EditorDocumentWorkspace.For(snapshot)`，与 Tier 2 诊断
> 同一份 Compilation，无分叉。至此 design/18 阶段 0–4 全部闭合。细节与 API 真相表见
> [design/18 §12.12](./18-razor-csharp-intellisense.md)。
