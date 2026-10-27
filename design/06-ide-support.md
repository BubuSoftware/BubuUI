# 6. IDE 支持（VS Code + Rider 第一优先）

核心难题：**`.ui` 是自定义语言，但表达式是 C#——让 C# 的智能"投影"进你的文件**。

## 6.1 三层能力，三个载体

| 层 | 内容 | 载体 | 成本 |
|---|---|---|---|
| 语法层 | 高亮、括号匹配、折叠、片段 | TextMate grammar（VS Code / Rider 通用）+ Tree-sitter（Neovim/Zed） | 低，纯声明式 |
| 编译诊断 | 类型错误、未知标签、字面量格式错 | **Roslyn 增量生成器天然携带**（`Location.Create(additionalFile, span)`），零 IDE 插件 | 极低 |
| 语义智能 | 补全、悬停、跳转、重命名、代码操作 | **自写 LSP + Roslyn 投影** | 高，主要投入 |

关键洞察：模板编译为 Roslyn 增量生成器后，诊断这条线是搭便车的——生成器报错时用
`Location.Create(additionalFile, span)` 把错误定位到 `.ui` 原始偏移，VS / Rider /
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
  补全/悬停/定义，按 span 映射表换算回 `.ui` 偏移。重命名时 Roslyn 改 `.cs`，
  同时反扫所有模板投影表达式同步改 `.ui`。
- **组件注册表驱动补全**：标签补全列可见组件；属性补全按目标类型过滤（含附加属性按
  owner 过滤、事件显示委托签名）；第三方包组件自动出现。
- **诊断双通道**：生成器诊断（编译期，带 span 映射）+ LSP 诊断（编辑中实时），
  指向同一 span 映射表，红的位置一致。

## 6.4 VS Code 扩展（第一优先，成本最低）

- 扩展捆绑 grammar + snippets + LSP 客户端启动逻辑（约百行 TS）。
- 激活时以 stdio 拉起 `dotnet tool` 版 server；检测不到 .NET SDK 时给明确引导。
- 受益于 C# Dev Kit 生态：解决方案加载、热重载按钮、调试宿主现成。

## 6.5 Rider 插件（第二优先，lsp4ij 生态）

已核实的事实基础：lsp4ij（Red Hat，EPL-2.0）支持所有 IntelliJ 系 IDE 含 Rider，
要求 2024.2+，经 `com.redhat.devtools.lsp4ij.server` / `languageMapping` 扩展点把
`.ui` 关联到 stdio LSP server。

- **首选路径**：发布极小插件，只做两件事——注册 TextMate bundle（IntelliJ 平台原生
  支持导入 `.tmLanguage.json`，grammar 共享）+ lsp4ij 扩展点声明 server。
- **过渡路径**：正式插件发布前，允许用户手动配置 lsp4ij 的 server 定义。
- **备选路径**：JetBrains 内置 LSP API（`com.intellij.platform.lsp`）在 Rider 稳定后
  平滑迁移，server 侧零改动。
- **平台注意点**：`.ui` 需先关联到插件注册的 Language 才能挂高亮与 LSP——这是
  Rider 壳里唯一需要写几行平台代码的地方。

## 6.6 两家差异矩阵

| 事项 | VS Code | Rider |
|---|---|---|
| 语法高亮 | TextMate 原生 | TextMate bundle 导入（同一份 grammar） |
| LSP 客户端 | 内置 | lsp4ij（或未来内置 API） |
| 调试/热重载 | C# Dev Kit 现成 | ReSharper 后端 + DAP；`.ui` 断点映射靠 `#line`（P3） |

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
