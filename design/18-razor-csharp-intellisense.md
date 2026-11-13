# 18 · Razor/Blazor 的 C# 智能提示：技术方案、VS Code 链路与 EclipseUI 借鉴

> 状态：调研 + 方案建议（2026-11-11）；**阶段 0–4 已实施**（阶段 0：SourceMapping + 统一映射服务 + `eclipseui/generatedDocument`；阶段 1：`@code` 悬停 + 重命名；阶段 2：`EditorDocumentWorkspace` + `@code` 补全；阶段 3：`{expr}` 补全迁移 + `Renamer` 重命名；阶段 4：语义着色 Roslyn 化，代码修复 / 签名帮助按 4.9.2 可用性保留。落地细节与偏差见 §12）。
> 关联：[design/06 §6.9.3 / §6.9.6 / §6.9.10](./06-ide-support.md)、[ide/README.md](../ide/README.md)、[spec/02](../spec/02-generated-code-contract.md)。
> 结论：Razor 的 C# 能力来自「真实生成文档 + Roslyn 语言服务 + 双向位置映射 + 语言路由」；
> EclipseUI 已经具备同源生成器、项目 Compilation 与目录，缺的是 **Document 层**与**统一映射服务**，
> 不是 Roslyn 本身。

---

## 0. 结论摘要

| 问题 | 答案 |
|---|---|
| Razor 为什么能给出 C# 补全、悬停、跳转、重命名？ | 因为 `.razor` 被编译成真实 C#，Roslyn 把它当成 `SourceGeneratedDocument`；这些能力全部由 Roslyn 的语言服务提供，Razor 只做路由和映射。 |
| VS Code 插件具体做了什么？ | 客户端维护 `razor-html` 虚拟文档、把 HTML 请求转发给 VS Code 内置语言服务；C# 请求在 Roslyn 进程内直接调用 C# API，不再走一轮 LSP。 |
| EclipseUI 最大的差距是什么？ | 生成代码只进了 `Compilation`，没有进 Roslyn `Workspace`/`Document`；因此只能手写 `SemanticModel` 查询，调不到 `CompletionService`/`Renamer`。 |
| `@code` 缺补全/悬停/重命名为啥不能只加一个 `switch` 分支？ | 加分支只能拿到符号；补全需要“当前位置可见什么”的上下文，重命名需要 `Solution` 级引用查找。根因是没有文档层。 |
| 最该先做什么？ | 先做 `SourceMapping` + 统一 `DocumentMappingService`，再做 `@code` 悬停/重命名，最后做 `EditorDocumentWorkspace` + `CompletionService`。 |
| 要不要照搬 Razor 的 HTML 虚拟文档？ | 不要。`.ui` 的标签/属性/`@bind-`/内容模型是自定义语义，交给 HTML/CSS 服务会制造“IDE 比编译器宽松”的假阴性。 |

**一句话**：Razor 没有为 `.razor` 重写 C# 语言服务；它把 `.razor` 变成 Roslyn 认得的 C# 文档，然后让 Roslyn 干活。

---

## 1. 范围与非目标

### 1.1 本文覆盖

- Razor/Blazor 的编译产物、源生成器、`SourceGeneratedDocument` 与 Roslyn 服务的关系；
- Razor 的 `#line` / `SourceMapping` 映射机制；
- VS Code 侧 Razor/C# 扩展的具体链路，特别是虚拟 HTML 文档与请求转发；
- EclipseUI 现有 IDE 实现（L1–L4、`EmbeddedCSharpService`、`CompletionProvider`、`RenameProvider`）的对照；
- 可落地的借鉴项、优先级、验收与风险。

### 1.2 本文不覆盖

- 不改变 [spec/01](../spec/01-template-syntax.md) 的 `.ui` 语法；
- 不改变 [spec/02](../spec/02-generated-code-contract.md) 的生成代码契约与 `#line` 规范；
- 不提议用 Razor 编译器/HTML 语法层替换 EclipseUI 的前端；
- 不提议让 Roslyn 接管 `.cs` 文件（那仍由 C# Dev Kit / ReSharper 负责）；
- 不展开调试器的 `#line` 断点接线（design/06 P3 的独立项）。

---

## 2. Razor 的技术方案

### 2.1 编译链路

```
Counter.razor
  │  Razor 编译器（RazorProjectEngine / RazorCodeDocument）
  ▼
RazorCSharpDocument
  │  Text + SourceMappingsSortedByGenerated/Original + LinePragmas
  │  RazorSourceGenerator: context.AddSource(hintName, csharpDocument.Text)
  ▼
Roslyn Project.Compilation
  │  SourceGeneratedDocument（真实 Document，不是磁盘文件）
  ▼
CompletionService / Hover / Definition / Rename / Diagnostics / SemanticTokens
```

关键点：

1. `.razor` 不是“被当成 C# 解析”，而是**先被 Razor 编译器生成 C#**；
2. 生成代码不是临时字符串，而是 Roslyn 项目里的 `SourceGeneratedDocument`；
3. 生成文档与项目里的 `.cs` 共享同一套 `Project`、引用、编译选项、`using`、`Nullable`、`LangVersion`；
4. 所有 C# 能力都是 Roslyn 对这份文档的原生服务。

Blazor 和 Razor Pages/MVC 的生成形态不同：

- Blazor 生成 `ComponentBase` 派生类 + `BuildRenderTree(RenderTreeBuilder)`；
- Razor Pages/MVC 生成 `RazorPage` 派生类 + `WriteLiteral`/`Write` 调用；
- 机制相同：**生成真实 C#，交给 Roslyn**。

### 2.2 生成代码长什么样

以一个简化的 Blazor 组件为例：

```razor
@code {
    private int count;
}

<button @onclick="Increment">@count</button>
```

Razor 会生成类似下面的真实 C#（简化）：

```csharp
public partial class Counter : ComponentBase
{
    private int count;

    protected override void BuildRenderTree(RenderTreeBuilder __builder)
    {
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick",
            EventCallback.Factory.Create(this, Increment));
        __builder.AddContent(2, count);
        __builder.CloseElement();
    }
}
```

这里没有“模拟”或“投影”：

- `@code` 里的 `count` 是**真实字段**；
- `Increment` 是**真实方法**；
- `@foreach` 生成真实 `foreach`，迭代变量是**真实局部变量**；
- `@bind` 生成真实属性赋值和事件绑定；
- 组件标签生成 `OpenComponent<T>` / `AddComponentParameter` 等真实调用。

Razor 指令、标签助手、`@bind-` 的合法性由 Razor 编译器/标签助手层判断；Roslyn 只负责生成代码里的 C# 语义。两者分工清楚。

### 2.3 源生成器与 host output

当前 `RazorSourceGenerator` 是一个 `IIncrementalGenerator`：

- 从 `AdditionalTextsProvider` 读取 `.razor`；
- 用 Razor 编译器生成 `RazorCSharpDocument`；
- 调用 `context.AddSource(hintName, csharpDocument.Text)` 把生成代码交给 Roslyn；
- 通过 `RegisterHostOutput` 输出 `RazorGeneratorResult`（标签助手、`.razor` 路径与 hint name 的映射），供工具链使用。

`RazorCSharpDocument` 同时保留：

```csharp
public SourceText Text { get; }
public ImmutableArray<SourceMapping> SourceMappingsSortedByGenerated { get; }
public ImmutableArray<SourceMapping> SourceMappingsSortedByOriginal { get; }
public ImmutableArray<LinePragma> LinePragmas { get; }
```

> 截至 2026-11，Razor 编译器和工具链已经并入 `dotnet/roslyn/src/Razor`，旧的 `dotnet/razor` 仓库已归档。
> 这本身就是方向信号：**Razor 与 Roslyn 是深度集成，而不是两个语言服务互相转发。**

### 2.4 为什么必须是 `Document`，而不是只有 `Compilation`

Roslyn 有两层能力，差别很大：

| 层 | 能回答的问题 | 典型 API |
|---|---|---|
| `Compilation` / `SemanticModel` | “这个节点是什么符号/什么类型？”“这里有没有诊断？” | `GetSymbolInfo`、`GetTypeInfo`、`GetDiagnostics` |
| `Document` / `Solution` + 语言服务 | “光标处应该补什么？”“重命名这个符号要改哪些文件？”“这里有哪些代码修复？” | `CompletionService`、`Renamer`、`CodeFixService`、`Classifier` |

`SemanticModel` 足够做悬停、跳转、签名帮助的一部分；但补全和重命名不是 `SemanticModel` 能解决的：

- 补全要知道“当前位置可见哪些局部变量、参数、类型名、扩展方法”，还要按上下文过滤：
  - `var t = ta|` 应该出类型名；
  - `if (|` 应该出布尔表达式；
  - `await |` 只出可等待对象；
  - `task.` 要出 `TaskItem` 的实例成员、扩展方法、推荐成员、snippet；
- 重命名需要 `Solution` 级别的引用查找、冲突检测、跨文档修改；
- 代码修复需要 `CodeFixProvider` 和 `Workspace`；
- 语义着色需要 Roslyn 分类器，而不是自己遍历 token。

Razor 的生成文档里，`@code` 就是真实类成员，光标可能落在：

- 字段/属性声明处；
- 方法体内；
- `if` 条件里；
- lambda 体内；
- `@foreach` 循环体内。

**只有 Roslyn 的 `CompletionService` 知道这些上下文。** 手写一个 `Members()` 枚举器，只能覆盖“成员选择”这一种语义，永远补不出局部变量、关键字、snippet、扩展方法、泛型方法组和 `await` 过滤。

### 2.5 映射：`#line` 与 `SourceMapping` 的分工

生成代码和 `.razor` 不是一一对应的：

- 有 prologue/epilogue；
- 有 `__builder` 调用链；
- 有 `#line hidden` 的合成代码；
- 表达式可能被包进 cast、lambda、临时变量；
- 一行生成代码可能包含多个原始 span。

Razor 同时保留两类映射信息。

#### 2.5.1 `#line`：给诊断和调试器用

生成代码里会发出：

```csharp
#line (12, 8) - (12, 30) "Counter.razor"
```

这是 C# 10 的带列范围增强 `#line`。Roslyn 的 `Location.GetMappedLineSpan()` 会据此把生成代码里的诊断位置映射回 `.razor`；调试器也依赖它把断点放回源文件。

EclipseUI 的生成器已经发了这种 span 型 `#line`（`Emitter.Mapped` / `EmitCodeBlock`），Tier 2 诊断也已经用 `GetMappedLineSpan()` 回映。这一点和 Razor 是同一条路。

#### 2.5.2 `SourceMapping`：给编辑类功能用

Razor 的 `RazorCSharpDocument` 还保留：

```csharp
public sealed class SourceMapping
{
    public SourceSpan OriginalSpan { get; }   // .razor 里的区间
    public SourceSpan GeneratedSpan { get; }  // 生成 C# 里的区间
}
```

它按 generated/original 两个方向排序，支持二分查找。补全的 `textEdit`、悬停的 range、重命名的 `WorkspaceEdit`、代码修复的编辑，都要靠它把生成代码里的区间映射回 `.razor`。

#### 2.5.3 为什么不能只用 `#line`

`#line` 是“行/列映射”，不是通用的双向区间映射：

- 一行生成代码可能对应多个原始区间；
- 生成代码里可能有完全合成的、没有原始位置的代码；
- 文本编辑需要的是区间包含关系，不是单个位置；
- 表达式被包进 `(Type)(expr)` 后，生成区间和原始区间长度/边界不一致；
- 映射失败时必须能明确返回“不可映射”，而不是猜一个位置。

Razor 的规则是：**能完整映射才映射；映射不了就丢弃这个补全项/编辑，绝不近似。**  
EclipseUI 的代码操作已经遵循“映不回的编辑丢弃而不是猜”，这个原则应该扩展到所有 provider。

### 2.6 多语言路由与结果合并

一个 `.razor` 文件里同时存在三种“语言”：

| 区域 | 归属 | 谁提供能力 |
|---|---|---|
| `@code`、`@if`/`@foreach` 条件、属性表达式 | C# | Roslyn |
| HTML 标签、属性、文本 | HTML | VS Code 内置 HTML 语言服务 |
| `@page`、`@using`、`@inherits`、标签助手 | Razor | Razor 编译器/工具链 |

Razor 用一个语言路由层判断光标属于谁，再把请求分发给对应的语言服务。补全时：

- Razor 指令/标签助手候选；
- C# 补全（Roslyn，对生成文档）；
- HTML 补全（VS Code 客户端，对 `razor-html` 虚拟文档）；

三路合并，再把每个 item 的编辑范围映射回 `.razor`，映射失败就丢弃。

这就是 Razor 补全看起来“什么都有”的原因：不是一个大而全的补全器，而是**多个语言服务的合并结果**。

---

## 3. VS Code 插件的具体链路

以补全为例，VS Code 侧并不是“把 `.razor` 当 C# 文件打开”，而是下面这条链路。

### 3.1 客户端：薄壳 + 虚拟文档适配器

相关代码在 `vscode-csharp/src/lsptoolshost/razor/`。

- `HtmlDocumentContentProvider` 注册 `razor-html` scheme；
- 虚拟文档路径是 `<原 .razor 路径>__virtual.html`；
- `HtmlDocumentManager` 维护虚拟文档内容、checksum、待更新 Promise；
- 服务端通过 `razor/updateHtml` 请求把 HTML 投影推给客户端：

```ts
new RequestType<HtmlUpdateParameters, void, void>('razor/updateHtml')
// params: { textDocument, checksum, text }
```

- 客户端收到后打开/更新 `razor-html` 虚拟文档，触发 `onDidChangeTextDocument`；
- 需要 HTML 补全时，调用 VS Code 内置命令：

```ts
vscode.commands.executeCommand(
  'vscode.executeCompletionItemProvider',
  virtualDocumentUri,
  projectedPosition,
  triggerCharacter
);
```

- 再把 VS Code 的 `CompletionItem` 转成 LSP 的 `CompletionItem`：
  - `kind` 需要 +1（VS Code 与 LSP 的枚举偏移）；
  - `SnippetString` 要转成 `InsertTextFormat.Snippet`；
  - `range` 要转成 `TextEdit` / `InsertReplaceEdit`；
  - `documentation` 要转成 `MarkupContent`。

其他能力同理：

- `vscode.executeHoverProvider`
- `vscode.executeDefinitionProvider`
- `vscode.executeReferenceProvider`
- `vscode.executeSignatureHelpProvider`
- `vscode.executeFoldingRangeProvider`

关闭 `.razor` 时，客户端还会发 `razor/documentClosed`，让服务端清理虚拟文档。

调试辅助也很实用：

- 服务端提供 `razor/generatedDocumentContents`；
- 客户端命令 `extension.showRazorCSharpWindow` / `extension.showRazorHtmlWindow` 打开生成的 C# / HTML 内容。

### 3.2 服务端：Razor 与 Roslyn 同进程

Razor 的 cohosting 把 Razor 语言服务放进 Roslyn 进程，Razor 与 Roslyn 共享对象类型和对象实例。补全请求的路径大致是：

1. VS Code 把 `.razor` 的 `textDocument/completion` 发给 Roslyn LSP；
2. Roslyn LSP 路由到 Razor 的 `CohostDocumentCompletionEndpoint`；
3. Razor 判断光标属于 Razor 指令、C# 还是 HTML；
4. 如果是 C#：
   - 把 `.razor` 位置映射到生成 C# 文档的位置；
   - 直接调用 Roslyn 的 completion 服务（同进程 C# API，不再发一轮 LSP）；
   - 拿到 `CompletionList`；
   - 把每个 item 的 `TextEdit` / `Span` 映射回 `.razor`；
5. 如果是 HTML：
   - 通过 forwarded request 让 VS Code 客户端调用内置 HTML 语言服务；
   - 客户端用 checksum 确保虚拟文档已更新；
   - 把 HTML 补全项转成 LSP 项返回；
6. Razor 把 Razor 指令/标签助手、C#、HTML 三路结果合并，过滤掉映射不回去的 item，返回一个 `CompletionList`。

跳转定义也是同样的模式：`CohostGoToDefinitionEndpoint` 对 C# 结果做生成位置 → `.razor` 的回映；对 HTML 结果做 `razor-html` 虚拟 URI → 原 `.razor` URI 的回映。

**关键点：C# 不走跨进程 LSP，而是同进程直接调用 Roslyn 服务。** 这是 cohosting 的核心收益，也是类型安全和性能的来源。

### 3.3 一次补全的时序

```
VS Code (.razor)
  │ textDocument/completion
  ▼
Roslyn LSP (Razor cohost)
  │
  ├─ C# 区域 ──► 映射到生成文档 ──► Roslyn CompletionService
  │                                      │
  │                                      ▼
  │                                  CompletionList
  │                                      │
  │                                      ▼
  │                                  映射回 .razor
  │
  ├─ HTML 区域 ──► forwarded request ──► VS Code 客户端
  │                                      │ razor-html 虚拟文档
  │                                      │ vscode.executeCompletionItemProvider
  │                                      ▼
  │                                  HTML CompletionList
  │
  └─ Razor 区域 ──► Razor 指令/标签助手候选
  │
  ▼
合并 + 过滤不可映射项
  │
  ▼
LSP CompletionList → VS Code 渲染
```

这个结构的关键不是“合并”本身，而是**每一路都用自己的语言服务，而不是用一个手写器模拟所有语言**。

---

## 4. EclipseUI 对照

### 4.1 已经对齐的部分

EclipseUI 并不是“没有 Roslyn”：

- L1–L4 已经用 MSBuild 求值 + 真实项目 Compilation + 真 `TemplateGenerator`；
- 生成器已经发出 span 型 `#line`；
- Tier 2 诊断已经用 `GetMappedLineSpan()` 回映；
- `EmbeddedCSharpService` 已经用 `SemanticModel` 做签名帮助、语义着色、表达式类型推断；
- `CodeBlockDefinition` 已经能把 `@code` 里的声明跳回 `.ui`；
- 客户端也是薄 LSP 客户端，还有反编译虚拟文档。

这些和 Razor 的方向是一致的。尤其是 **“编译时按什么，IDE 就按什么”**，这就是 Razor 的核心原则。

### 4.2 差异表

| 维度 | Razor | EclipseUI 现状 | 影响 |
|---|---|---|---|
| 生成代码在 IDE 中的身份 | Roslyn `SourceGeneratedDocument`，属于 `Project`/`Solution` | L2/L3 `Compilation` 里的 `SyntaxTree`，不是 `Document` | **根本差距**：无法调用 `CompletionService`/`Renamer` |
| C# 能力来源 | Roslyn 语言服务 | `SemanticModel` + 手写 `Members()` | 能力上限不同 |
| `@code` | 真实类成员，完整 C# 服务 | `MemberTree` 只用于跳转/签名/着色；补全/悬停/重命名落 `default` | 已知缺口 |
| `{expr}` | 真实生成文档 + 映射 | `ForExpression` 合成 `__EclipseUiL8()` | 丢失真实语句上下文 |
| 补全项模型 | Roslyn item + Razor/HTML 合并 | catalog + 手写成员枚举；所有 item 共享一个替换区间 | 无法表达 per-item `textEdit`、tags、commit chars |
| 映射 | `SourceMapping` + `#line` + mapping service | `#line` 用于诊断；`BodyStart`/`IndexOf` 散落各处 | 脆弱，易错位 |
| 重命名 | `Renamer` over `Solution` | 手写 `SymbolFinder`，仅 `{expr}` / code-behind 成员 | 覆盖窄，冲突处理弱 |
| 诊断 | 生成文档诊断 + 映射 | Tier 2 `#line`，已对上 | 保留 |
| 多语言 | HTML 虚拟文档 + VS Code HTML 服务 | 自定义 `.ui` / `.eustyle` 目录 | **不应照搬 HTML** |
| 项目系统 | Razor project system / Roslyn workspace | L1–L4 MSBuild 求值 + 目录 | EclipseUI 的强项，保留 |
| 客户端 | 薄壳 + 虚拟文档适配 | 薄 LSP 客户端 + 反编译文档 | 基本够用 |

### 4.3 差距的本质

不是“Razor 用了 Roslyn，EclipseUI 没用”，而是：

> **Razor 让 Roslyn 的语言服务跑在一份真实的 `Document` 上；EclipseUI 只让 Roslyn 的 `SemanticModel` 跑在一份 `Compilation` 上。**

这带来三个具体后果：

1. **补全只能手写**：`CompletionProvider.Members()` 遍历 `GetMembers()`，没有局部变量、关键字、snippet、扩展方法、推荐成员、`await` 过滤；
2. **`@code` 无法补全**：`@code` 是类成员，光标可能在字段、方法体、lambda、`if` 条件里；没有 `CompletionService`，手写枚举无法判断上下文；
3. **重命名只能覆盖很窄的场景**：`RenameProvider.ResolveTarget` 硬判 `CursorKind.Expression`，`@code` 和跨文档引用都进不来。

`@code` 缺补全/悬停/重命名不是“忘了加一个 `switch` 分支”，而是**没有 `Document` 这一层**。加分支只能拿到符号信息，拿不到“当前位置该补什么”。

### 4.4 目录不是问题，反而应该保留

Razor 也没有让 Roslyn 去验证 HTML 标签、标签助手、`@bind-`。这些是 Razor 编译器/标签助手层的职责。  
EclipseUI 的 `UiCatalogFactory` 就是 Razor 标签助手描述符的对应物：它把 CLR 类型收窄成“模板真正暴露的表面”，防止 Roslyn 接受 `AvailableSize`、`ItemFactory` 这类普通 CLR 属性而编译器报 EUI002。

所以 [design/06 §6.9.6](./06-ide-support.md) 的结论是对的：

> **模板表面归目录，C# 表达式/语句归 Roslyn。**

借鉴 Razor 不是“把 `.ui` 整体投影成 C# 让 Roslyn 校验标签”，而是“把 C# 岛生成成真实文档，让 Roslyn 服务 C# 岛”。目录一行都不用删。

---

## 5. 可借鉴点（按优先级）

### 5.1 P0-1：生成文档成为一等公民

**目标**：让每个 `.ui` 生成代码在 Roslyn `Workspace` 里有一个 `Document`，而不是只往 `Compilation` 里塞 `SyntaxTree`。

有两种实现路线。

#### 路线 A（务实，建议先做）

新建 `Analysis/EditorDocumentWorkspace.cs`：

- 每个项目快照一份 `AdhocWorkspace`，懒创建、按版本缓存；
- 加入项目的 `.cs` 源文档（用编辑器缓冲区文本，不用磁盘文本）；
- 加入 `.ui` 的生成文档（来自 `ProjectSnapshot.GeneratedTrees`），虚拟路径如 `Foo.ui.g.cs`；
- 使用和 L2 完全相同的 `CSharpParseOptions`、`CompilationOptions`、`MetadataReference`；
- 不要把生成树同时作为源文档和生成文档加入，避免 partial 成员重复；
- 缓冲区变化时用 `Solution.WithDocumentText` 更新，而不是重建整个 workspace。

这样不需要改生成器管线，就能拿到 `Document`，从而调用 `CompletionService`、`Renamer`。

#### 路线 B（更贴近 Razor，后续评估）

让 Roslyn `Workspace` 自己跑同一个 `TemplateGenerator`：

- 把 `TemplateGenerator` 作为 `AnalyzerReference` 加入 `Project`；
- 把 `.ui` 作为 `AdditionalDocument` 加入（生成器从 `AdditionalTextsProvider` 读）；
- 用 `AnalyzerConfigDocument` 提供 `build_property.rootnamespace`；
- 调用 `Project.GetSourceGeneratedDocumentsAsync()` 得到 `SourceGeneratedDocument`。

这条路最像 Razor：生成文档由 Roslyn 自己管理，不需要手工注入。但要处理生成器程序集加载、`extern alias`、analyzer 配置、版本绑定等问题，建议作为第二阶段评估。

**验收**：

- 对同一个 `.ui`，workspace 里的生成文档与 L3 生成树文本一致；
- `GetSemanticModelAsync()` 能解析 `@code` 成员和 code-behind 成员；
- `CompletionService` 能在 `@code` 内返回候选。

---

### 5.2 P0-2：统一的双向映射服务

**目标**：把现在散落在 `ForExpression.BodyStart`、`ForCodeBlock.IndexOf`、`TryMapMemberSpanToUi`、代码操作里的偏移计算，收敛成一个 `DocumentMappingService`。

建议让生成器直接产出映射表：

- `Emitter.Mapped(TextSpan span, string text, int userOffset)` 和 `EmitCodeBlock` 在写生成代码时，同时记录 `SourceMapping(originalSpan, generatedSpan)`；
- `TemplateGenerator` 返回 `GeneratedDocument { SourceText Text, ImmutableArray<SourceMapping> Mappings, string HintName }`；
- `ProjectSnapshot` 保存每个 `.ui` 的 `GeneratedDocument`。

`DocumentMappingService` 提供：

```csharp
bool TryMapToGenerated(TextSpan original, out TextSpan generated);
bool TryMapToOriginal(TextSpan generated, out TextSpan original);
bool TryMapPositionToGenerated(int originalOffset, out int generatedOffset);
bool TryMapRangeToOriginal(TextSpan generated, out TextSpan original);
```

规则：

- 只做**完整包含**映射；跨边界、部分重叠、生成独有代码一律返回 `false`；
- 映射失败时，补全项/编辑/重命名整个丢弃，不猜；
- `#line` 继续用于诊断和调试器；`DocumentMappingService` 用于编辑类功能；
- 用二分查找，按 generated/original 两个方向排序，和 Razor 的 `SourceMappingsSortedByGenerated/Original` 同构。

**验收**：

- 对仓库所有 `.ui` fixture，每个 `{expr}` 和 `@code` 区间都能 round-trip；
- 多行 `@code`、CRLF、边界位置、生成独有代码都有测试；
- 现有诊断/跳转/代码操作改走该服务后行为不变。

---

### 5.3 P0-3：补全模型升级

现在 `UiCompletionItem` 只有一个共享的 `Replacement` 区间：

```csharp
public sealed record UiCompletionList(TextSpan Replacement, IReadOnlyList<UiCompletionItem> Items);
```

这装不下 Roslyn 的补全结果。建议扩展：

- `UiCompletionItem` 增加：
  - `TextSpan? Replacement`（per-item 替换区间）；
  - `string? FilterText`；
  - `IReadOnlyList<string>? CommitCharacters`；
  - `IReadOnlyList<string>? Tags`（`Class`/`Method`/`Property`/`Keyword`/`Snippet` 等）；
  - `bool Preselect`；
  - `InsertTextFormat`（PlainText/Snippet）。
- `UiCompletionList` 增加 `bool IsIncomplete`；
- `LanguageServerHost.Completion` 输出对应的 LSP 字段：`textEdit`、`filterText`、`commitCharacters`、`tags`、`preselect`、`insertTextFormat`。

没有这一层，即使接了 `CompletionService`，也会丢掉 Roslyn 的 per-item 编辑范围、snippet 和 commit characters。

---

### 5.4 P1-1：先补 `@code` 的悬停和重命名

这两项最便宜，因为 `EmbeddedCSharpService.At` 已经能处理 `CursorKind.CodeBlock`，`ForCodeBlock` 在项目模式下用的就是真实 `MemberTree`。

- `HoverProvider` 增加 `CursorKind.CodeBlock` 分支：
  - `GetSymbolInfo` / `GetTypeInfo`；
  - 局部变量/字段/参数/方法/类型分别格式化；
  - 目录成员文档可以继续复用。
- `RenameProvider` 先加 `CodeBlock` 分支：
  - 用 `SymbolFinder.FindReferencesAsync` 或 `Renamer`；
  - 用映射服务把生成文档里的引用位置回映到 `.ui`；
  - 任何一处映射不回去，整个重命名拒绝（保持现有“改不全就拒绝”的原则）。

**验收**（对应 [design/06 §6.9.10](./06-ide-support.md)）：

1. `@code` 内局部变量/字段悬停显示类型；
2. `@code` 内重命名局部变量和 code-behind 字段引用；
3. 不误改其他类型同名成员；
4. `@code` 内 `task.` 的补全暂时可以不做，但悬停/重命名要先通。

---

### 5.5 P1-2：接 `CompletionService`，先 `@code` 后 `{expr}`

**依赖**：

- `Microsoft.CodeAnalysis.CSharp.Workspaces`
- `Microsoft.CodeAnalysis.CSharp.Features`
- 版本必须和现有 `Microsoft.CodeAnalysis.CSharp` **完全一致**。design/06 写的是 4.9.2，实施前以 csproj 实际版本为准，不要盲目锁旧版本。

**实现**：

```csharp
var service = CompletionService.GetService(document);
if (service is null) return UiCompletionList.Empty;

var completions = await service.GetCompletionsAsync(new CompletionContext(
    document, mappedOffset, trigger, options, cancellationToken));
```

- `document` 是生成 C# 文档；
- `AdhocWorkspace` 必须能组合到 C# 的 completion provider：用 `MefHostServices.Create(MefHostServices.DefaultAssemblies)`，并确保 `Microsoft.CodeAnalysis.CSharp.Features` 已加载；否则 `GetService` 会返回 `null`；
- `mappedOffset` 是 `.ui` 光标经映射服务得到的生成偏移；
- `trigger` 从 LSP `CompletionContext` 转换（`Invoke` / `TriggerCharacter`）；
- 对每个 `CompletionItem`：
  - 有 `TextEdit` 就用它的 `Span` 映射回 `.ui`；
  - 没有 `TextEdit` 就用 `item.Span` 映射；
  - 映射失败就丢弃；
  - 转换 `Tags`、`SortText`、`FilterText`、`CommitCharacters`、`InsertTextFormat`。

**合并策略**：

- `@code`：只用 Roslyn；
- `{expr}`：Roslyn 为主；如果未来有模板级候选（例如组件参数名），再按位置合并；
- 标签/属性/指令：继续走目录，不调 Roslyn；
- `@bind-`、`Style=` 等非 C# 值：继续走目录。

**验收**：

- `@code` 内 `task.` 的成员与 `.cs` 同位置一致；
- `var t = ta|` 出类型名，`ta` → `TaskItem`；
- `if (|` 出布尔表达式候选，而不是成员列表；
- `await |` 只出可等待对象；
- 扩展方法、泛型方法组、snippet 都出现；
- 与 `.cs` 对照测试通过。

如果包体积/版本绑定暂时不可接受，可以先用 design/06 的路线 A：`SemanticModel.LookupSymbols(treeOffset)` + 关键字表 + 少量上下文规则。它能让 `@code` “日常够用”，但**达不到“与 `.cs` 同档”**；Razor 对应的是路线 B。

---

### 5.6 P1-3：重命名改走 `Renamer` + `Solution`

补全接上后，重命名也应该升级：

```csharp
var newSolution = await Renamer.RenameSymbolAsync(
    solution, symbol, new SymbolRenameOptions(), newName, cancellationToken);
```

- 对 `newSolution` 做 diff；
- `.cs` 文档的修改直接应用；
- 生成 `.g.cs` 文档的修改经映射服务回映到 `.ui`；
- 任何一处生成文档修改映射失败，整个重命名拒绝。

这样能自然覆盖：

- `@code` 局部变量；
- code-behind 字段/属性/方法；
- `{expr}` 里的引用；
- 跨文档引用；
- 同名成员冲突检测。

现有手写 `SymbolFinder`/`ParseText` 扫描可以逐步删除。

---

### 5.7 P2-1：统一语言路由，别再让每个 provider 自己 switch

现在 `CompletionProvider`、`HoverProvider`、`RenameProvider` 各自 `switch (context.Kind)`，所以 `@code` 的 `CursorKind.CodeBlock` 会在三个地方同时落空。

建议引入一个 `UiLanguageRouter`：

```csharp
enum UiLanguageKind { Markup, CSharpExpression, CSharpCode, Style }

(UiLanguageKind Kind, TextSpan? GeneratedSpan) Route(DocumentAnalysis analysis, int offset);
```

- `TagName` / `EndTagName` / `AttributeName` / `Directive` / `DirectiveArgument` → `Markup`；
- `Expression` → `CSharpExpression`；
- `CodeBlock` → `CSharpCode`；
- `Style` 值 → `Style`。

provider 只问 router “这段归谁”，不再各自判断。这样 `@code` 的接入是一个路由改动，而不是三个 provider 各加一个分支。

---

### 5.8 P2-2：语义着色、代码修复、签名帮助逐步交给 Roslyn

- **语义着色**：用 Roslyn `Classifier` 对生成文档分类，映射回 `.ui`，与 TextMate 的模板表面着色合并。现在的 `Classify` 是迷你分类器，能用，但覆盖不如 Roslyn。
- **代码修复**：对 `@code` 接 `CodeFixService`，把 `WorkspaceEdit` 映射回 `.ui`；模板级修复（EUI001/EUI002）继续走目录。
- **签名帮助**：当前 `EmbeddedCSharpService.SignatureHelp` 已经能用；如果 Roslyn 的 `SignatureHelpService` 在目标版本可用且稳定，再考虑替换；否则保留。
- **诊断一致性**：确保 Tier 2 诊断和补全使用**同一份生成文档/同一版本**。如果 Tier 2 重新跑生成器、补全用另一份 `MemberTree`，两者可能看到不同代的代码。Razor 的 checksum/version 思路值得借用。

---

### 5.9 P2-3：生成代码可观测性

加一个 `eclipseui/generatedDocument` 请求 + VS Code 命令：

- 返回当前 `.ui` 的生成 C# 文本；
- 可选返回映射表（`eclipseui/documentMap`）；
- VS Code 用只读虚拟文档或 webview 展示。

Razor 的 `razor/generatedDocumentContents` 和 `extension.showRazorCSharpWindow` 已经证明了这个功能的价值：映射错位、补全候选不对时，第一时间能看见生成代码。

---

### 5.10 P3：虚拟文档/嵌入式语言，选择性借鉴

Razor 的 `razor-html` 虚拟文档很优雅，但**不要直接套到 `.ui` 上**：

- `.ui` 的标签、属性、`@bind-`、内容模型、`Grid.Row` 都是自定义语义；
- HTML/CSS 语言服务会给出编译器不接受的候选，制造“IDE 比编译器宽松”的假阴性——这是 [design/06 §6.9.6](./06-ide-support.md) 明确要避免的；
- `Style="CornerRadius: 8"` 是自定义样式语言，CSS 服务会建议不存在的属性。

只有未来 `.ui` 真的嵌入标准 HTML/JS/CSS 时，才值得借：

- 客户端 `TextDocumentContentProvider`；
- 服务端 `update` 请求 + checksum；
- 客户端 `vscode.execute*Provider` 转发；
- URI/位置回映。

今天 EclipseUI 唯一需要的“虚拟文档”是**服务端的生成 C# 文档**，它不需要 VS Code 客户端参与。

---

### 5.11 P3：不要照搬 cohost 协议和设计时/运行时双份生成

- EclipseUI 已经是单进程 LSP server，不需要 Razor 的 cohost 通信协议；要学的是“共享同一个 workspace/document”，不是“再造一套 IPC”。
- Razor 为了 IDE 性能会生成设计时代码，和运行时不完全相同。EclipseUI 的“IDE 与 build 同码”是更强的约束，建议保留；先让生成代码对映射友好，而不是分叉。
- 映射友好包括：
  - 每个用户 span 都发 `SourceMapping`；
  - 用户表达式逐字保留；
  - 不要在用户可编辑代码周围随便 `#line hidden`；
  - 一个生成区间尽量只对应一个用户区间，减少歧义。

---

## 6. 建议的落地顺序

| 阶段 | 内容 | 验收 |
|---|---|---|
| 0 | 生成器产出 `SourceMapping`；新增 `DocumentMappingService`；现有诊断/跳转/代码操作改走统一映射；加 `eclipseui/generatedDocument` | 所有 fixture round-trip；现有行为不变 |
| 1 | `@code` 悬停 + 重命名（复用 `EmbeddedCSharpService.At`） | §6.9.10 第 4、5 条 |
| 2 | `EditorDocumentWorkspace` + 补全模型升级 + `@code` `CompletionService` | `task.` / `var t = ta▍` / `if (▍)` / `await` 与 `.cs` 一致 |
| 3 | `{expr}` 补全迁到真实生成文档；删除 `Members()`；重命名改 `Renamer` | 表达式补全 parity；重命名覆盖 `@code` + code-behind |
| 4 | 语义着色/代码修复/签名帮助逐步 Roslyn 化；性能与缓存 | 能力 parity；补全延迟可接受 |

**排序理由**：

- 先做映射服务，因为后面所有功能都依赖它，而且它不改用户可见行为，风险最低；
- 再做 `@code` 悬停/重命名，因为最便宜，能快速验证 `MemberTree` + 映射服务；
- 再做 workspace + completion，因为这是架构性改动，放在映射稳定之后；
- 最后迁移 `{expr}` 和重命名，避免一次性重写所有 provider。

---

## 7. 验收清单

### 7.1 映射

- [ ] 所有 `.ui` fixture 的 `{expr}` span 能 `TryMapToGenerated` 再 `TryMapToOriginal` 回到原 span；
- [ ] 所有 `@code` 块体 span 能 round-trip；
- [ ] 多行 `@code`、CRLF、空行、块首/块尾边界有测试；
- [ ] 生成独有代码（无原始 span）返回 `false`；
- [ ] 映射失败时，补全/重命名/代码操作不产生错误编辑。

### 7.2 `@code`

- [ ] `task.` 列出 `TaskItem` 实例成员，与 `.cs` 同位置对照一致；
- [ ] `var t = ta|` 列出类型名，`ta` → `TaskItem`；
- [ ] `if (|` 给出布尔表达式候选，而不是成员列表；
- [ ] `await |` 只给出可等待对象；
- [ ] 悬停局部变量/字段显示类型；
- [ ] 重命名 `@code` 局部变量、code-behind 字段引用；
- [ ] 不误改其他类型同名成员；
- [ ] 重命名任一处映射失败时整体拒绝。

### 7.3 一致性与回归

- [ ] Workspace 编译与 L2 编译的符号/诊断一致（同一 fixture）；
- [ ] 未保存的 code-behind / `.ui` 缓冲区在补全中可见；
- [ ] Tier 1 / Tier 2 诊断无回归；
- [ ] 现有跳转、代码操作、格式化、语义着色无回归；
- [ ] 补全延迟有实测记录（warm/cold 分开）；
- [ ] `eclipseui/generatedDocument` 能显示当前 `.ui` 的生成 C#。

---

## 8. 风险与边界

| 风险 | 影响 | 缓解 |
|---|---|---|
| 包体积/版本绑定 | 新增 `Workspaces`/`Features` 依赖，可能与现有 Roslyn 版本冲突 | 锁到 `Microsoft.CodeAnalysis.CSharp` 同一版本；发布前测体积/启动时间；必要时先走 `LookupSymbols` 过渡 |
| Workspace 与 L2 不一致 | 补全看到的符号/诊断和 build 不一致 | 用同一套 `ParseOptions`/`CompilationOptions`/`MetadataReference`；加一致性测试 |
| 未保存缓冲区 | 补全看不到刚写的 code-behind 成员 | 用编辑器缓冲区 `SourceText`，不要读磁盘 |
| 性能 | 首次 completion 慢；每次按键重建 workspace 会卡 | 懒初始化、按项目/版本缓存、`Solution.WithDocumentText` 增量更新 |
| 线程安全 | 并发 LSP 请求修改 workspace 导致状态错乱 | 用 `SemaphoreSlim` 串行化写；读取用不可变 `Solution` 快照 |
| 映射歧义 | 同一表达式在生成代码里出现多次 | 映射服务标记歧义；补全选包含光标的区间；重命名交给 Roslyn 找全部引用；无法确定时返回空 |
| 生成器变换 | cast/lambda/临时变量导致生成区间和原始区间不一致 | 审计 `Mapped`/`MappedToken`；在每个发射点记录 `SourceMapping`；测试覆盖 |
| `@code` 上下文 | `@code` 是类成员，内部可能是方法体/lambda/条件 | 生成文档必须包含完整 partial 类、code-behind、using、引用；Roslyn 才能给对上下文 |
| `#line` 与 `SourceMapping` 混用 | 把诊断映射当成通用逆向映射，编辑错位 | 明确分工：`#line` 诊断/调试；`SourceMapping` 编辑 |
| Roslyn 源生成器加载 | 路线 B 的 analyzer 加载/ALC/版本问题 | 先走路线 A；路线 B 单独评估，不阻塞 P0/P1 |

**硬边界**：

- 不接管 `.cs` 文件；
- 不让 Roslyn 校验模板表面；
- 不为了补全而放宽目录（否则 IDE 比编译器宽松）；
- 不把映射失败的结果“猜”到某个位置；
- 不因为引入 Workspace 就改变生成代码文本（`spec/02` 契约不变）。

---

## 9. 不做什么

1. **不替换 `UiCatalogFactory`**  
   目录仍是模板表面的唯一事实源；Razor 的标签助手层也是独立于 Roslyn 的，这一点不矛盾。

2. **不把 `.ui` 投影成 HTML/CSS 交给外部语言服务**  
   `.ui` 的标签/属性/绑定/内容模型是自定义语义；HTML/CSS 服务会制造假阴性。

3. **不复制 Razor 的 cohost IPC**  
   EclipseUI 已经是单进程；需要的是共享 `Workspace`/`Document`，不是再造一套跨进程协议。

4. **不先做设计时/运行时双份生成**  
   保持“IDE 与 build 同码”；先让生成代码对映射友好，性能问题实测后再谈分叉。

5. **不让 Roslyn 接管模板表面**  
   目录负责“模板允许什么”，Roslyn 负责“生成代码里的 C# 是什么”。

6. **不把 `#line` 当万能映射**  
   诊断可以靠 `#line`；补全/重命名/代码操作必须走区间映射服务。

---

## 10. 待拍板

1. **是否按 P0 → P1 实施？**  
   建议顺序：`SourceMapping` + `DocumentMappingService` → `@code` 悬停/重命名 → `EditorDocumentWorkspace` + `CompletionService`。

2. **是否接受新增 Roslyn Workspaces/Features 依赖？**  
   需要 `Microsoft.CodeAnalysis.CSharp.Workspaces` + `Microsoft.CodeAnalysis.CSharp.Features`，并锁定与现有 `Microsoft.CodeAnalysis.CSharp` 相同版本。若不可接受，只能走 `LookupSymbols` 过渡，但达不到 `.cs` 同档。

3. **P0 是否允许生成器返回值增加 `SourceMapping` 旁路元数据？**  
   只增加元数据，不改变生成 C# 文本与 `#line`，`spec/02` 契约和 `dotnet build` 产物不变。

4. **`@code` 的验收是否按 §7.2 执行？**  
   特别是 `task.`、`var t = ta|`、`if (|`、`await |` 四项与 `.cs` 对照。

5. **是否把 P2 的 Roslyn 语义着色/代码修复排进本轮？**  
   建议不排；先把补全/悬停/重命名打通，语义着色/代码修复作为后续增量。

> 拍板后按现有决策台账登记（D 编号由维护者分配），并在 [design/06 §6.9.10](./06-ide-support.md) 与本文件同步更新状态。

---

## 11. 参考

### Razor / Roslyn

- Razor 源生成器（当前在 Roslyn 仓库）：  
  <https://github.com/dotnet/roslyn/blob/main/src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/SourceGenerators/RazorSourceGenerator.cs>
- `RazorCSharpDocument`：  
  <https://github.com/dotnet/roslyn/blob/main/src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/RazorCSharpDocument.cs>
- `SourceMapping`：  
  <https://github.com/dotnet/roslyn/blob/main/src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/SourceMapping.cs>
- Razor/Roslyn cohosting 设计：  
  <https://github.com/dotnet/razor/issues/9519>
- Razor 迁移到 Roslyn：  
  <https://github.com/dotnet/razor/issues/13021>
- Razor cohosting 端点目录：  
  <https://github.com/dotnet/roslyn/tree/main/src/Razor/src/Razor/src/Microsoft.CodeAnalysis.Razor.CohostingShared>

### VS Code

- VS Code C# 扩展的 Razor 支持（虚拟 HTML 文档、请求转发）：  
  <https://github.com/dotnet/vscode-csharp/tree/main/src/lsptoolshost/razor>
- `HtmlDocumentContentProvider` / `HtmlDocumentManager`：  
  <https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/razor/htmlDocumentContentProvider.ts>  
  <https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/razor/htmlDocumentManager.ts>
- `razor/updateHtml`、HTML 请求转发：  
  <https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/razor/razorEndpoints.ts>
- 生成文档查看命令：  
  <https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/razor/showGeneratedDocumentCommand.ts>

### EclipseUI

- [design/06 · IDE 支持](./06-ide-support.md)，重点 §6.9.3 / §6.9.6 / §6.9.10
- [ide/README.md](../ide/README.md)
- [spec/02 · 生成代码契约](../spec/02-generated-code-contract.md)
- `ide/EclipseUI.LanguageServer/Analysis/EmbeddedCSharpService.cs`
- `ide/EclipseUI.LanguageServer/Analysis/CompletionProvider.cs`
- `ide/EclipseUI.LanguageServer/Analysis/DocumentAnalysis.cs`
- `ide/EclipseUI.LanguageServer/Analysis/RenameProvider.cs`
- `ide/EclipseUI.LanguageServer/Server/LanguageServerHost.cs`
- `ide/EclipseUI.LanguageServer/Workspace/ProjectWorkspace.cs`
- `src/EclipseUI.Compiler.Generator/Emitter.cs`
- `src/EclipseUI.Compiler.Generator/TemplateGenerator.cs`

---

## 12. 实现说明（阶段 0 已落地，2026-11-11）

本节记录阶段 0 的实际落地形态、与上文方案的关键偏差，以及后续阶段必须沿用的 API 真相。
**硬边界全部守住**：生成 C# 文本与 `#line` 逐字不变（`EclipseUI.Compiler.Tests` 375/375 golden 绿）、
目录仍是模板表面唯一事实源、映射失败一律丢弃不猜、`.cs` 不接管。

### 12.1 已建成的链路

生成器旁路 → 收集 → 快照 → 分析 → provider → LSP 请求，一条线：

| 层 | 文件 | 职责 |
|---|---|---|
| 旁路元数据 | `src/EclipseUI.Compiler.Generator/IdeSourceMappings.cs` | `IdeSourceMapping`（Original/Generated/Verbatim）+ `IdeSourceMappingSideband` 静态收集器 |
| 发射记录 | `src/EclipseUI.Compiler.Generator/Emitter.cs` | `Mapped`/`MappedToken`/`EmitCodeBlock` 写代码的同时 `RecordPending`；多缓冲合并经 `AppendMapped` 重基；`PublishMappings` 交旁路 |
| hintName | `src/EclipseUI.Compiler.Generator/TemplateGenerator.cs` | `SetHintName(input.Path, hintName)`，与 `AddSource` 同名 |
| 收集 | `ide/.../Workspace/GeneratorHost.cs` | `Enable()` 窗口内跑 driver，`Take(path)` 取映射，按 hintName 配对生成树，产出 `GeneratedDocument` |
| 快照 | `ide/.../Workspace/ProjectWorkspace.cs` | `ProjectSnapshot.GeneratedDocuments`（按 `.ui` 绝对路径，OrdinalIgnoreCase） |
| 映射服务 | `ide/.../Analysis/DocumentMappingService.cs` | 双向二分查找，完整包含才映射 |
| 分析入口 | `ide/.../Analysis/DocumentAnalysis.cs` | `GeneratedDocument`/`Mappings`；`TryMapMemberSpanToUi` 优先走映射 |
| provider 迁移 | `EmbeddedCSharpService.ForCodeBlock`、`DefinitionProvider.CodeBlockDefinition` | 生成树命中时用映射定位，失败回退 legacy `IndexOf` |
| 可观测性 | `ide/.../Server/LanguageServerHost.cs` | `eclipseui/generatedDocument` 返回 hintName + 生成文本 + 映射表 |

### 12.2 偏差一：4.9.2 没有公开 `RegisterHostOutput`（§2.3 / §5.1 路线 B 不可用）

上文 §2.3 提到的 host output 注册（`RegisterHostOutput`）是 **Roslyn 4.12+** 的 API；本仓库锁定的
**4.9.2 没有**。因此不走"生成器返回值带映射"的路线，改用**静态 opt-in 旁路收集器**：

- `IdeSourceMappingSideband.Enable()` 返回 `IDisposable`，引用计数窗口；`IsActive` 在 `Emitter`
  构造时一次性捕获。**build 路径不开窗口 → 零开销**，`Record`/`SetHintName` 见 `!IsActive` 立即返回。
- 只有 IDE 的 `GeneratorHost.Run` 在 `using (Enable())` 内跑 driver；每次 `Run` 新建
  `CSharpGeneratorDriver`，保证所有文件重新发射、映射不串代。
- 旁路 API 只用 BCL（生成器程序集被 `extern alias euiGen` + `PrivateAssets=all` 引用，
  不能反向依赖 IDE/Compiler 类型）。`Take(path)` 取走即删，lock + `Dictionary`（OrdinalIgnoreCase）。

> 连带约束：`PrivateAssets=all` 阻断传递引用，`EclipseUI.Ide.Tests` **不能直接引用生成器类型**；
> 测试一律经 `GeneratorHost.Run` 这条真实路径验证，不手写旁路。

### 12.3 偏差二：4.9.2 生成树 `FilePath` 带生成器前缀

`RunGeneratorsAndUpdateCompilation` 产出的生成树，其 `FilePath` 不是裸 hintName，而是
`GeneratorAssemblyName\GeneratorTypeFullName\hintName`（实测）。因此 `GeneratorHost` 配对用
**`Path.GetFileName(t.FilePath) == sideband.HintName`**，不是全路径相等。后续阶段按生成树定位
文档时同样要记住这条。

### 12.4 偏差三：`CompletionService` / `Renamer` 的 4.9.2 真实签名（订正 §5.5 / §5.6）

上文示例签名在 4.9.2 不存在，阶段 2/3 必须用下列真实形态（已对 4.9.2 Features 程序集反编译核对）：

```csharp
// §5.5 的 new CompletionContext(...) 形态不存在。4.9.2 是：
Task<CompletionList> GetCompletionsAsync(
    Document document, int caretPosition,
    CompletionTrigger trigger = default,
    ImmutableHashSet<string>? roles = null,
    OptionSet? options = null,
    CancellationToken cancellationToken = default);

// 取 per-item 编辑（映射回 .ui 用）：
Task<CompletionChange> GetChangeAsync(
    Document document, CompletionItem item,
    char? commitCharacter = null, CancellationToken cancellationToken = default);

// §5.6 的 new SymbolRenameOptions() 形态在 4.9.2 **存在且未弃用**（阶段 3 实测订正：
// 早前误判为不存在）。两个重载都在，OptionSet 版已弃用：
[Obsolete("Use overload taking RenameOptions")]
Task<Solution> Renamer.RenameSymbolAsync(
    Solution solution, ISymbol symbol, string newName,
    OptionSet? options = null, CancellationToken cancellationToken = default);
// 阶段 3 用这个（SymbolRenameOptions 是 public readonly record struct，全 false 默认）：
Task<Solution> Renamer.RenameSymbolAsync(
    Solution solution, ISymbol symbol, SymbolRenameOptions options,
    string newName, CancellationToken cancellationToken = default);
```

`Microsoft.CodeAnalysis.CSharp.Features` / `Workspaces` 一律锁 **4.9.2**，与现有
`Microsoft.CodeAnalysis.CSharp` 完全一致；NuGet 走 azure.cn 镜像
（`https://nuget.azure.cn/v3-flatcontainer/{id-lower}/{version}/{id-lower}.{version}.nupkg`）。

### 12.5 映射语义（`DocumentMappingService`）

- **完整包含才映射**：跨边界、部分重叠、生成独有代码一律 `false`。
- **Verbatim**（生成长度 == 原文长度，逐字 1:1）允许子区间按偏移差双向换算；
  **语句级**（非 verbatim）只映射整段，不做子区间。
- **同原文多次出现**（`@bind` 的 readSource/writeSource）：正向取首个 verbatim，反向映射全部。
- **不同原文重叠**：查询区间同时落在两个不同原文的映射里 → `false`（歧义不猜）。
- **零宽边界点**：取排序首个，确定性。
- 双向各按 Start 二分；稳定排序保留发射顺序。

### 12.6 踩坑：静态 run 的词法 Span 越过表达式（`mappingSpan`）

`Lexer.FlushText` 用 `_i` 作终点，静态文本 token 的 `Span` 会**伸进后随的 `{expr}`**
（如 `Hello ` 的 Span 覆盖 `Hello {name}`）。若照单记录语句级映射，会与 `name` 的 verbatim
映射原文重叠，触发 §12.5 的"不同原文重叠拒绝"，导致表达式 round-trip 失败。

修法：`Mapped` 增加可选 `mappingSpan`——**`#line` 仍用原 `Span`（golden 逐字不变）**，
映射原文钳到实际文本长度（`EmitTextRuns` 静态分支）。这是"生成文本不变"与"映射可用"兼得的关键：
任何需要收窄映射原文的场景，都走 `mappingSpan`，**绝不改传给 `EmitMappedLine` 的 span**。

### 12.7 验证

- `EclipseUI.Compiler.Tests` **375/375**：生成文本 / `#line` golden 契约不变。
- `EclipseUI.Ide.Tests` **148/148**（现有 134 + 新增 `SourceMappingTests` 14）：
  合成映射服务 7 例 + 真生成器 round-trip（表达式 / `@code` 子区间 / CRLF / 生成独有 /
  bind 双出现 / 错误短路）+ 全部 Gallery `.ui` 夹具逐表达式与 `@code` round-trip。
- `EclipseUI.Headless.Tests` **372/372**、`EclipseUI.Rendering.Tests` **106/106**：无回归。
- 全解决方案构建 0 警告 0 错误。

### 12.8 本轮范围

按 §6 落地顺序实施**阶段 0–3**；阶段 4（P2 语义着色 / 代码修复 / 签名帮助 Roslyn 化）本轮不排
（§10 第 5 条建议；**后续已落地，见 §12.12**）。§10 其余待拍板项按本文自身建议执行：P0→P1 顺序、接受 Workspaces/Features
依赖（锁 4.9.2）、允许旁路元数据（不改生成文本）、`@code` 验收按 §7.2。D 编号由维护者分配，本文不代填。

### 12.9 阶段 1 落地：`@code` 悬停 + 重命名（2026-11-11）

**共享入口**：`EmbeddedCSharpService.CodeBlockSymbolAt(analysis, offset)` 返回光标处符号 +
其标识符在 .ui 的区间。投影/绑定复用既有 `At`（真实成员树 + 项目 Compilation），回映走
`DocumentAnalysis.TryMapMemberSpanToUi`（项目模式 = 统一映射服务，legacy = IndexOf 回退）。
悬停与重命名都从这里取符号，不再各写一套。

- **悬停**：`HoverProvider` 增 `CursorKind.CodeBlock` 分支，局部变量/参数/字段/属性/事件/方法/类型
  分别给类型或签名（复用既有 `DisplayFormat`，去可空噪音）。
- **重命名**：`RenameProvider.ResolveTarget` 增 `CodeBlock` 分支：
  - 局部变量/参数 → 新目标 `CodeScopedTarget`：引用全在成员树，逐处经映射服务回映，**任一处映不回整体拒绝**；
  - code-behind 类自身成员（声明在兄弟 .cs）→ 复用既有 `MemberTarget` 全套路径（.ui + 兄弟 .cs + 工作区 .cs）。

**偏差一：阶段 1 重命名不走 `SymbolFinder`/`Renamer`**。§5.4 写的 `SymbolFinder.FindReferencesAsync`/
`Renamer` 都需要 `Solution`，而 `Workspace` 是阶段 2 才引入的。阶段 1 沿用既有的
`SemanticModel` 引用扫描（与现有 `MemberTarget` 同一台机器），回映改走统一映射服务；
`Renamer` + `Solution` diff 留到阶段 3（§5.6）。

**偏差二：按声明源区间匹配引用，不用符号 identity**。跨编译实例（`AddSyntaxTrees` 派生）的
符号 identity 对 merged partial 有假阴性（既有 `SignatureOf` 注释已述）；而局部变量的签名字符串
只有名字，同名会误伤。故 `CodeScopedTarget` 记录**声明在成员树里的源区间**，扫描时按
`symbol.Locations[0].SourceSpan == 声明区间` 匹配——源区间是树内位置，跨编译实例稳定，
且天然区分不同作用域的同名局部变量（验收"不误改其他类型同名成员"由此保证）。

**范围边界**：`@code` 里**声明的成员**（字段/属性/方法）阶段 1 拒绝重命名（声明在合成树，
不在本 server 能保证改全的文件集合）；阶段 3 的 `Renamer` 会自然覆盖。外部类型成员同样拒绝。

**验证**：新增 `CodeBlockIntellisenseTests` 10 例，三层覆盖——内存态（legacy 回映）/
磁盘 code-behind（成员路径）/ 项目模式（统一映射服务回映，借用 Gallery 真实 .ui 路径注入缓冲区，
`ProjectWorkspace.TryRead` 优先取缓冲区故生成文档与注入文本对齐）。`EclipseUI.Ide.Tests`
**158/158** 绿；design/06 §6.9.10 第 4、5 条（悬停/重命名）闭合。

### 12.10 阶段 2 落地：`EditorDocumentWorkspace` + `@code` `CompletionService`（2026-11-11）

**链路**（§5.1 路线 A：workspace 直接从快照 Compilation 派生，§7.3 一致性由构造保证）：

| 层 | 文件 | 职责 |
|---|---|---|
| 编辑器 workspace | `ide/.../Analysis/EditorDocumentWorkspace.cs` | 每快照一个 `AdhocWorkspace`：trees/options/references 全部取自 `snapshot.Compilation`，生成树只 `AddDocument` 一次（不重复 partial）；`ConditionalWeakTable<ProjectSnapshot, …>` 缓存——快照不可变，缓冲区一变自然换新 workspace，创建后只读无锁 |
| 补全 provider | `ide/.../Analysis/EmbeddedCSharpCompletionProvider.cs`（阶段 2 名 `CodeBlockCompletionProvider.cs`，阶段 3 改名） | 光标经映射服务进生成文档 → `ShouldTriggerCompletion`/`GetCompletionsAsync` → 复杂编辑逐项 `GetChangeAsync` → 编辑区间回映 .ui，**映不回丢项** |
| 模型 | `ide/.../Analysis/LanguageModels.cs` | `UiCompletionItem` 增 `Replacement`/`FilterText`/`CommitCharacters`/`Tags`/`Preselect`；`UiCompletionKind` 补齐 LSP 类别；`UiCompletionList` 增 `IsIncomplete` |
| 词法 | `ide/.../Analysis/CursorScanner.cs` | `CodeBlock` 上下文补标识符前缀（`TokenStart`/`Prefix`） |
| 服务入口 | `ide/.../Analysis/LanguageService.cs` | 新增 `CompleteAsync`：`CodeBlock` → Roslyn；其余仍走目录（同步 `Complete` 保留给目录位与既有测试） |
| 协议 | `ide/.../Server/LanguageServerHost.cs` | completion 改 async 分派，透传 triggerCharacter/filterText/preselect/commitCharacters/tags（`1`=Deprecated）/逐项 textEdit/isIncomplete；triggerCharacters 增 `.` `:` `(` |
| 依赖 | `ide/.../EclipseUI.LanguageServer.csproj` | 增 `Microsoft.CodeAnalysis.CSharp.Features` **4.9.2**（与 CSharp/Workspaces 锁同版） |

**踩坑一：`AdhocWorkspace` 链式 Solution 变更不自动落回**。`AddProject`→`WithCompilationOptions`
→…→`AddDocument` 得到的是新 Solution 分支；不显式 `workspace.TryApplyChanges(project.Solution)`
的话 `CurrentSolution` 仍是空解（实测 projects=0，补全全空且无任何报错）。创建末尾一次性
`TryApplyChanges`，之后 `CurrentSolution` 才含项目/文档。

**踩坑二：MEF 组合比预想便宜**。4.9.2 的 `MefHostServices.DefaultAssemblies` 已含全部 6 个
程序集名（Workspaces / CSharp.Workspaces / VisualBasic.Workspaces / Features / CSharp.Features /
VisualBasic.Features），`LoadNearbyAssemblies` 跳过缺失项——引用 Features 包后
`MefHostServices.Create(MefHostServices.DefaultAssemblies)` 即可，无需手动维护加载清单。

**偏差一：4.9.2 补全模型成员订正**（反编译核对）。`CompletionList.Items` 已弃用 → `ItemsList`；
`DefaultSpan` 弃用报错 → `Span`。`CompletionItem` **没有**公开 commit 字符集与弃用标志：
生效 commit 集 = `list.Rules.DefaultCommitCharacters` ± `item.Rules.CommitCharacterRules`
（Add/Remove/Replace）；弃用 = `Tags` 含 `"Deprecated"`；`Preselect` = `Rules.MatchPriority ==
MatchPriority.Preselect`；`IsIncomplete` = `SuggestionModeItem is not null`（建议模式）。

**偏差二：snippet 无 tabstop**。`RoslynLSPSnippetConverter` 在 4.9.2 是 internal——拿不到 LSP
`$1/$0` 片段语法。`GetChangeAsync` 返回展开后文本（占位符是字面词，如 `for` 的 `length`）。
只接受「本文档单处 `TextChange`」：多处/跨文档编辑（如自动补 using 落在生成独有代码区）
无法忠实回映，整项丢弃（§8 不猜）。

**偏差三：await 上下文是「装饰不过滤」**。§7.2「`await |` 只出可等待对象」的字面假设不成立：
4.9.2 实测 await 位置返回 `await` 关键字 + **全量符号**，可等待类型仅在描述里加
「(可等待)/(Awaitable)」前缀（Features 的 `AbstractSymbolDescriptionBuilder.AddDescriptionForNamedType`
→ `IsAwaitableNonDynamic` → `AddAwaitablePrefix`；`AwaitCompletionProvider` 只贡献 `await`
关键字本身）。验收以「与 `.cs` 一致」为准：测试对照同一 workspace 里 `.cs` 文档同位置
`Task` 项的描述**逐字相等**。不自行过滤——过滤就是手写语义判断，违背本阶段
「Roslyn 拥有语义」的前提。

**踩坑三：旁路收集器并发串扰（`GeneratorHost.Run` 串行化）**。`IdeSourceMappingSideband` 是
进程级静态、按 .ui 路径键控；两个并发 `Run`（并行测试类各建 Gallery 快照、注入不同
Counter.ui 缓冲区）会互相覆盖条目、`Take` 走对方映射甚至拿 null——全量 Ide 套件并行跑时
实测复现（`GeneratedDocumentFor` 偶发 null）。修法：`GeneratorHost.Run` 整体加静态 `RunGate`，
「开窗口 → 跑驱动 → Take」串行；生成器程序集零改动，build 路径不受影响。

**延迟实测（§7.3）**：Gallery Counter.ui 注入夹具，`task.` 位置 7 项候选。cold（workspace
创建 + MEF 组合 + provider 首载 + 语义绑定）两次实测 **282ms / 1445ms**（随机器负载波动）；
warm（同位再请求，全缓存命中）**71ms**。测试留 5000ms 宽松上限防回归，数值记录于此。

**范围边界**：legacy（无项目快照/无生成文档）`@code` 补全返回空——不另造合成投影；
模板表面（标签/属性/指令）补全仍归目录，不过 Roslyn；`{expr}` 补全本阶段仍走旧
`CompletionProvider`，阶段 3 迁移。

**验证**：新增 `CodeBlockCompletionTests` 12 例——§7.2 四项对照（`task.` 候选集与同
workspace `.cs` 文档逐集合相等 / `var` 类型名 / `if` 布尔上下文 / await 描述对照）、扩展方法、
snippet 真实展开、触发字符、替换区间回映、未保存 code-behind 缓冲区可见、legacy 空、
workspace 与 L3 同文 + 语义模型、延迟。`EclipseUI.Ide.Tests` **170/170**（158+12，全量并行
两遍绿）；`EclipseUI.Compiler.Tests` **375/375**、`EclipseUI.Headless.Tests` **372/372**、
`EclipseUI.Rendering.Tests` **106/106**；全解决方案构建 0 警告 0 错误。
design/06 §6.9.10 第 1–3 条（补全验收）闭合。

### 12.11 阶段 3 落地：`{expr}` 补全迁移 + `Renamer` 重命名（2026-11-11）

**链路**（§5.6：重命名 = `Renamer` over Solution + diff 回映；`{expr}` 补全复用阶段 2 机器）：

| 层 | 文件 | 职责 |
|---|---|---|
| 重命名 provider | `ide/.../Analysis/WorkspaceRenameProvider.cs`（新增） | 光标 → 生成文档符号（`SymbolFinder.FindSymbolAtPositionAsync`，touch 语义）→ `Renamer.RenameSymbolAsync` → `newSolution.GetChanges(oldSolution)` diff：源 `.cs` 编辑直接交付，生成文档编辑经映射服务回映 `.ui`，**任一处映不回整体拒绝** |
| 补全 provider | `ide/.../Analysis/EmbeddedCSharpCompletionProvider.cs`（由 `CodeBlockCompletionProvider` 改名） | 逻辑不变；服务范围从 `@code` 扩到 `{expr}`（C# 岛统一入口） |
| 服务入口 | `ide/.../Analysis/LanguageService.cs` | `CompleteAsync`：`Expression` 且有生成文档 → Roslyn（否则目录）。新增 `PrepareRenameAsync`/`RenameAsync`：项目模式且非 foreach 变量 → `WorkspaceRenameProvider`，否则 legacy 同步路径 |
| 协议 | `ide/.../Server/LanguageServerHost.cs` | `prepareRename`/`rename` 改 async 分派；`WorkspaceEdit.changes` 支持多文件（当前 `.ui` 用请求 uri，兄弟 `.ui`/`.cs` 用磁盘 file uri） |
| 编辑器 workspace | `ide/.../Analysis/EditorDocumentWorkspace.cs` | 增 `UiPathFor(DocumentId)`/`SourcePathFor(DocumentId)` 反查（diff 时把变更文档归到 `.ui` 或 `.cs`） |
| legacy 路由 | `ide/.../Analysis/RenameProvider.cs` | `IsForeachVariable`/`IsIdentifier` 改 internal 供预路由；legacy 探针扫描路径保留（legacy 模式 + foreach 变量仍用） |

**`{expr}` 补全**：项目模式下 `{task.|}`/`{Cou|}`/`@foreach` 内 `{t.|}` 都走真实生成文档的
`CompletionService`，与 `.cs` 同一台机器；成员/局部/关键字/snippet 全来自 Roslyn。legacy
（无快照/无生成文档）仍走旧 `CompletionProvider` 探针——**不删**，因为 legacy 模式没有
生成文档可投影，探针是唯一来源。`Members()` 手写枚举只在 legacy 路径存活。

**踩坑一：`SourceText.GetTextChanges` 对多处小改退化成整篇替换**。`task→entry` 改 4 处
（各 +1 字符），`newText.GetTextChanges(oldText)` 实测返回**单条 `[0, 旧文本长]`**（整篇替换），
回映必败 → 整体拒绝。反编译 `Document.GetTextChangesAsync(oldDocument)` 发现：文本 diff 若
退化成「单条且覆盖全文」就**自动回落语法树 diff**（`SyntaxTree.GetChanges`）取细粒度编辑。
故改用 `newDocument.GetTextChangesAsync(oldDocument, ct)`——不自己拼 diff，用 Document 级
API 自带的回落。`TextChange.Span` 是**旧文本**坐标（生成坐标），正好喂映射服务。

**踩坑二：最小 diff 只换标识符的一部分**。`Name→Title` 共享后缀 `e`，Roslyn 实际编辑是
`Nam→Titl`（保留 `e`），不是整词替换。回映后 `.ui` 编辑区间也只盖 `Nam`——**这是正确的**
（应用后 `Name`→`Title`），但测试不能逐条断言「编辑覆盖完整标识符」。跨文档测试改为
「应用全部编辑后的整文 == 原文整词 `Name→Title`」，对 diff 粒度鲁棒。

**偏差一：可解冲突 `Renamer` 不抛异常（订正 §5.6「冲突检测」的实现口径）**。§5.6 把「同名
成员冲突检测」列为 Renamer 的自然覆盖，但 4.9.2 实测：`task→items`（items 已存在）生成
**两个 `items` 字段**仍 `IsSuccessful`——`RenameSymbolAsync` 只对**不可解**冲突抛
`ArgumentException`，可解冲突（重复成员/同名局部变量）直接产出带错误的解。故交付前加一道
**编译错误数比对**：`newCompilation` 比 `oldCompilation` 多出 Error 级诊断 = 引入冲突 →
整体拒绝（§8「改坏就拒绝」；旧解既有错误两边相同会抵消，只有新增才触发）。重命名是低频
用户操作，两次 `GetCompilationAsync` 可接受。两条防线：抛异常 → 拒绝；新增错误 → 拒绝。

**偏差二：`SymbolRenameOptions` 重载存在（订正 §12.4）**。见 §12.4 订正——`OptionSet` 版
已 `[Obsolete]`，阶段 3 用 `SymbolRenameOptions` 版（全 false 默认：不重载/不字符串/不注释/
不改文件名，保守口径）。

**范围边界**：
- 只改**本项目源码声明**的成员/局部变量/参数（`ContainingAssembly == 项目程序集` 且
  `Locations.Any(IsInSource)`）；元数据符号（如 `string.Length`）改不了 → 拒绝；类型/命名
  空间不改（类名 = 文件名契约，spec/02 §2.2）。
- **`@foreach` 迭代变量预路由回 legacy**：生成代码里它是 `SetTemplate` lambda 的参数
  （`Emitter.EmitForeach`），**无 `.ui` 映射**——Renamer 改它会整体拒绝。故
  `RenameProvider.IsForeachVariable` 在进 Roslyn 前识别，走 legacy 文档内路径（头声明 +
  本文档 `{t.X}` 引用）。
- **`@bind` 双出现去重**：readSource/writeSource 两处生成编辑映到**同一原文区间**，
  按 `(Start, Length, NewText)` 去重，只交付一条。
- **跨 `.ui` 重命名**：外部模型成员（如 `TaskItem.Name`）从 `{expr}` 光标触发，Renamer 改
  全项目——其他 `.ui` 的生成文档编辑回映到各自原文（文本口径：缓冲区优先，其次磁盘），
  源 `.cs` 直接交付。实测 `Name→Title` 覆盖 Counter.ui + TaskItem.cs + TaskList.ui +
  GalleryScene.cs 四文件。

**踩坑三：测试共享工作区的 sibling 缓冲区串扰**。`WorkspaceRenameTests` 共享一个
`ProjectWorkspace`；bind/foreach 测试把 TaskList.ui 注入为**当前**缓冲区（那些夹具没有
`{t.Name}`），跨文档测试要 TaskList.ui 作**磁盘 sibling** 参与引用——若跑到 bind 之后，
快照用的是残留缓冲区（无 `t.Name`），Renamer 找不到引用 → TaskList 缺编辑。修法：跨文档
测试开头 `ForgetBuffer(TaskList.ui)` 回磁盘。这是测试隔离问题，非产品 bug（产品里 sibling
缓冲区本就代表用户未保存编辑，语义正确）。

**验证**：新增 `ExpressionCompletionTests` 4 例（`{task.}` 成员来自 Roslyn / 替换区间回映 /
裸标识符出类成员 / foreach 变量成员）+ `WorkspaceRenameTests` 10 例（@code 字段/方法/局部
变量、prepare、跨文档外部成员、bind 去重、冲突拒绝、元数据拒绝、非法名拒绝、foreach 走
legacy）。`EclipseUI.Ide.Tests` **184/184**（170+14，全量并行两遍绿）；
`EclipseUI.Compiler.Tests` **375/375**、`EclipseUI.Headless.Tests` **372/372**、
`EclipseUI.Rendering.Tests` **106/106**；全解决方案构建 0 警告 0 错误。
design/18 §5.6（`{expr}` 补全迁移 + `Renamer` 重命名）闭合；design/06 §6.9.10 第 5 条
（重命名）自此覆盖 `@code` 声明成员与跨文档引用。

### 12.12 阶段 4 落地：语义着色 Roslyn 化；代码修复 / 签名帮助按可用性保留（2026-11-11）

§5.8 的四项里，**语义着色**接上了 Roslyn `Classifier`；**代码修复**与**签名帮助**经
4.9.2 真实程序集反射核对后**保留手写**（对应服务是 internal，见下表）；**诊断一致性**
由构造已保证，无需改动。这闭合 §12.8 暂缓的阶段 4（P2）。

**API 真相（对 NuGet 缓存里 4.9.2 实际程序集反射探针，非文档转述）**：

| 类型 | 所在程序集 | 公开性 | 结论 |
|---|---|---|---|
| `Classification.Classifier` | Workspaces | **public static** | `GetClassifiedSpansAsync(Document, TextSpan, ct)` → `Task<ImmutableArray<ClassifiedSpan>>`；另有 `GetClassifiedSpans(SemanticModel, TextSpan, Workspace, ct)` |
| `Classification.ClassifiedSpan` | Workspaces | **public readonly struct** | `TextSpan` + `ClassificationType`（string） |
| `Classification.ClassificationTypeNames` | Workspaces | **public** | 全部常量（`class name`/`field name`/`keyword - control`/…） |
| `CodeFixes.CodeFixService` | Features | **internal**（反射查无公开入口） | 无法直接调 |
| `CodeFixes.CodeFix` / `CodeFixCollection` | Features | **internal** | 同上 |
| 内置 `CodeFixProvider` 具体子类 | Workspaces/Features/CSharp.Features | **0 个 public** | 内置修复全 internal |
| `SignatureHelp.SignatureHelpService` | Features | **internal**（反射查无公开入口） | 无法直接调 |
| `SignatureHelp.SignatureHelpItems`/`Item`/`TriggerInfo`/`TriggerReason` | Features | **internal** | 同上 |

> 反编译 dump（`/tmp/*-decomp`）对这两个服务**不完整**（未含 `CodeFixService`/
> `SignatureHelpService` 类型），不能据其判断公开性；以反射探针 + 包内 XML 文档
> （只收公开成员，两者均无条目）双重核对为准。

**决策一：语义着色 → Roslyn `Classifier`（已实施）**。项目模式对**生成文档**整篇分类，
每个 `ClassifiedSpan` 经统一映射服务 `TryMapToOriginal` 回映 `.ui`——`{expr}` 与 `@code`
块体都是逐字映射（§12.5 / `SourceMappingTests` round-trip 已证），子区间映得回；生成独有
代码（类声明 / `Build` / `Track` 等）无映射天然丢弃，**模板表面仍归 TextMate**。映不回或
归并不到 legend 的类型丢弃而不是猜（R6）。无生成文档 / 分类异常回落既有探针投影路径
（`SemanticTokens`），行为不回归。Roslyn 粒度更细（字符串拆 escape、控制关键字单列、
record/扩展方法/常量各有专名），`MapClassification` 归并到 16 类 legend；`identifier`/
`text`/`punctuation`/事件等映不上的返回 -1 丢弃，与迷你分类器「不认识就不着色」一致。

**决策二：代码修复 → 保留确定性修复（不接 internal `CodeFixService`）**。4.9.2 的
`CodeFixService` 与全部内置 `CodeFixProvider` 都是 internal，唯一公开面是供第三方**编写**
修复的 `CodeFixProvider` 抽象类与 `CodeAction`/`ApplyChangesOperation`，没有「按诊断取内置
修复」的公开入口。接它只能反射 internal API——脆弱且撞 §8「锁版本 / 不猜」硬边界，故不做。
现状保留：模板级 EUI001/EUI002 走目录确定性修复，`@code` 相关的 CS0103 缺失成员由
`AddMissingMemberFix` 生成（目标类型取自目录属性，映不出类型丢弃整个 action）。

**决策三：签名帮助 → 保留 `EmbeddedCSharpService.SignatureHelp`**。`SignatureHelpService`
及其模型在 4.9.2 全 internal，符合 §5.8「可用且稳定才替换，**否则保留**」的保留分支。
现有实现（投影 + `InvocationExpressionSyntax` + `GetSymbolInfo`/`CandidateSymbols`/
`GetMemberGroup`）已给重载与当前参数位，协议测试覆盖。

**决策四：诊断一致性 → 构造已保证，无改动**。补全 / 重命名 / 语义着色都走
`EditorDocumentWorkspace.For(project)`，它直接以 `snapshot.Compilation` 为事实源（§12.10）；
Tier 2 诊断来自**同一快照**的生成器运行。单快照 → 单 Compilation → 单 workspace → 同一份
生成文档，不存在「Tier 2 与补全看不同代代码」的分叉。

**链路**（语义着色）：

| 层 | 文件 | 职责 |
|---|---|---|
| 分类 + 回映 | `ide/.../Analysis/EmbeddedCSharpService.cs` | 新增 `SemanticTokensAsync(analysis, uiPath, ct)`：生成文档 → `Classifier.GetClassifiedSpansAsync` → 逐区间 `TryMapToOriginal` → `MapClassification` 归并 → `ToTokens`（排序/去重叠/行位，与 legacy 共用）。`SemanticTokens` 抽出 `ToTokens` 复用 |
| 服务入口 | `ide/.../Analysis/LanguageService.cs` | 新增 `SemanticTokensAsync(uri, ct)`：非模板/无分析返回空，否则委托 provider |
| 协议 | `ide/.../Server/LanguageServerHost.cs` | `semanticTokens/full` 改 async 分派；delta 编码仍在宿主 |
| 测试 | `tests/.../SemanticColoringTests.cs`（新增） | 4 例：token 只落 C# 岛内 / 关键符号语义类型（field/property/method/variable）/ 关键字与数字 / legacy 回落与同步逐字一致 |

**验证**：`EclipseUI.Ide.Tests` **188/188**（184 + 新增 `SemanticColoringTests` 4）；
`EclipseUI.Compiler.Tests` **375/375**（生成文本 / `#line` golden 契约不变）、
`EclipseUI.Headless.Tests` **372/372**、`EclipseUI.Rendering.Tests` **106/106**；
全解决方案构建 0 警告 0 错误。design/18 §5.8 闭合（语义着色 Roslyn 化；代码修复 / 签名帮助
按 4.9.2 可用性保留并记录）；D 编号由维护者分配，本文不代填。

### 12.13 C# 岛转定义接通统一落点链（2026-11-11）

**用户实测**：`@code` 里点类型（`TaskList.ui` 的 `List<TaskItem>` 的 `TaskItem`）F12 返回
`definition: no result`，尽管 `TaskItem.cs` 就在同一项目里声明了它。标签 / 属性能跳，C# 岛里的
类型 / 成员不能。

**根因（机制链）**：两条 C# 岛路径——`{expr}` 的 `ExpressionDefinition` 与 `@code` 的
`CodeBlockDefinition`——最后都只走 `FromLocation`，而它只认两种落点：① 兄弟 code-behind 树
（`IsCodeBehindTree`）② 能回映进本文档 `@code` 块的成员（`TryMapMemberSpanToUi`）。其余一律
`null`。声明在**别的** .cs 的类型（`TaskItem`）两条都不命中；元数据 / dll 类型（`List<>`、框架
类型）更没有 `IsInSource` 位置，第一步就 null。对比标签 / 属性 / Style 跳转走的是完整三路
`FromSourceLocation`（任意真实源码文件）→ `WorkspaceSourceDefinition`（跨项目源码）→
`DecompileProvider`（dll 反编译）——C# 岛当初没接上这条兜底链。

**修法**：抽出共享 `SymbolDefinition(analysis, symbol)`，`ExpressionDefinition` /
`CodeBlockDefinition` 改走它：

1. 有源码位置 → `FromLocation`（本文档回映 / 兄弟 code-behind）?? `FromSourceLocation`（任意真实 .cs）；
2. 无源码位置 = 元数据符号 → 类型符号取 `OriginalDefinition`、成员取 `ContainingType`，
   `WorkspaceSourceDefinition`（跨项目源码）?? `DecompileProvider`（反编译，成员级定位）。

局部 / 参数恒有源码位置，走 1 映不回即 null，不会落到 2 拿 `ContainingType` 误跳。找不到真实
位置就 null，不猜（§8 / design/06 §6.3 口径不变）。`CodeBlockDefinition` 保留 partial 方法
「先定义部分、再光标处符号」两步，两步都走 `SymbolDefinition`。

**API 真相（反射探针实测，非文档转述）**：元数据名转换 `MetadataFullName` 用
`INamedTypeSymbol.MetadataName`——实测它含泛型 arity（`List`1` / `Outer`1`）但**不含**命名空间与
外层类型；嵌套类型 `MetadataName` 只是内层名（`Inner`），需沿 `ContainingType` 用 `+` 拼接，
命名空间取 `ContainingNamespace.ToDisplayString()`。`N.Outer`1+Inner` 经
`GetTypeByMetadataName` 往返成立。`FindSourceTypes` / `DecompileProvider` 都按此口径消费。

**测试**：

- 新增 `ProjectSystemTests.Fixture11_CSharpTypeAndMemberJumpToSource`：`@code` 的 `TaskItem`
  类型引用、`{expr}` 的 `t.Name` / `t.Id` 成员访问都落到 `TaskItem.cs` 真实声明（span 罩住标识符，
  非反编译、非本文档）。
- 订正 `ExpressionProbeTests.Definition_DotAndUnknownIdentifiersReturnNull` →
  `Definition_UnknownIdentifierReturnsNull_BclMemberDecompiles`：未知标识符仍 null；BCL 成员
  `tasks.Count` 的 `Count` 源码不可达，接通后走反编译，落点是 `List<T>` 的 `Count` 声明
  （`eui-decompiled:` 虚拟文档，span 罩住 `Count`）——与框架属性跳转同一口径。旧断言「一律 null」
  是接通前的限制，不是「此处禁止跳转」的规则。

**验证**：`EclipseUI.Ide.Tests` **190/190**（189 + Fixture11）。改动只在 IDE 定义落点，不触
生成文本 / `#line`（spec/02 契约不变）。D 编号由维护者分配，本文不代填。

### 12.14 BCL 类型关键字 / 泛型名转定义 + 反编译缓存键归一（2026-11-11）

**用户实测**（接 §12.13 发布后）：`String` 跳到**空文件**；`string`、`List`、`int`、`bool`
F12 **没反应**（找不到实现）。

**根因（机制链，两支独立）**：

1. **关键字 / 泛型名落 `null`**：`CodeBlockDefinition` 的 `token.Parent` switch 原来只有
   `IdentifierNameSyntax` 一支接类型名。但 `int`/`bool`/`string` 的语法节点是
   **`PredefinedTypeSyntax`**（预定义类型关键字），`List<TaskItem>` 的 `List` 是
   **`GenericNameSyntax`**（泛型名）——两者都不是 `IdentifierNameSyntax`，全落 `_ => null`，
   用户侧就是「F12 没反应」。`String`（大写类型名）走 `IdentifierNameSyntax` 能解析，所以它
   的症状不同（见第 2 支）。
2. **反编译缓存键形态漂移（空文件真根因）**：`DecompileProvider` 存 / 取都用 `BuildUri`
   的原始 uri 当键。但客户端内容提供器回传的不是原始 uri——见下「API 真相」实测：回传形态
   把空 authority 的三斜杠折叠成一斜杠、并解码 query 的 `%XX`。存 / 取键对不上 → **所有**
   反编译类型缓存未命中 → 客户端跳空文件（文件名对、内容空）。

**修法**：

1. switch 把 `IdentifierNameSyntax` 扩成 **`SimpleNameSyntax`**（其基类，涵盖
   `IdentifierNameSyntax` + `GenericNameSyntax`），并新增 **`PredefinedTypeSyntax`** 支：
   `model.GetSymbolInfo(predefined).Symbol` 给底层 BCL 类型（`int`→`Int32`、`bool`→`Boolean`、
   `string`→`String`），随后走 §12.13 的统一三路反编译。
2. `DecompileProvider` 存 / 取都经 `Key(uri)` 归一：用 `new Uri(uri)` 拆组件，重建
   `scheme + ":" + AbsolutePath + Uri.UnescapeDataString(Query)` 当键。三斜杠 / 一斜杠经
   `AbsolutePath` 归一、`%60` / `` ` `` 经 query 解码归一，两种回传形态落到同一键。

**API 真相（真实 vscode-uri 包实测，两次纠正臆断）**：客户端内容提供器回传
`uri.toString(true)`。装真实 `vscode-uri` 包跑 `URI.parse(serverUri).toString(true)`，实测它做
两件事：① 空 authority 的 `eui-decompiled:///Path.cs` 折叠成 `eui-decompiled:/Path.cs`
（**三斜杠→一斜杠**，非 file scheme 不补 `//`）；② 解码 query 里的 `%XX`（`List%601` →
`` List`1 ``）。**斜杠折叠才是空文件的主因**——只 `UnescapeDataString` 补不平斜杠差。
（第一版曾据源码摘要断言「`toString(true)` 保留 `%XX`、空文件是客户端陈旧缓存」，被用户复现
否定；装包实测后纠正：query 确实解码、且还有斜杠折叠。教训：uri 往返要拿真实包跑，不靠源码
摘要推断。）服务端反编译本身一直正常（`String` 11 万字符），断点只在缓存键。

**测试**：

- 新增 `ExpressionProbeTests.Definition_CodeBlockBclTypeDecompilesWithContent`（Theory 5 例）：
  `@code` 里 `int`/`bool`/`string`/`String`/`List<int>` 光标落在类型名上 → 反编译
  `Int32`/`Boolean`/`String`/`List`，span 罩住类型名，且 `Content(原始 uri)` 与
  `Content(客户端回传形态)`（按实测变换：三斜杠→一斜杠 + query 解码）都回传非空同源文本。
- 新增 `ProjectSystemTests.Fixture12_BclGenericNameDecompilesWithContent`：项目模式
  （`GeneratedDocument` 映射路径）下 `TaskList.ui` 的 `List<TaskItem>` 的 `List` 反编译
  `List`1`，原始 / 客户端回传两种形态都命中缓存。
- 移除诊断期临时探针 `TempProbe_BclTypes`。

**验证**：`EclipseUI.Ide.Tests` **196/196**（190 + Theory 5 + Fixture12）。改动只在 IDE 定义
落点与反编译缓存键，不触生成文本 / `#line`（spec/02 契约不变）。D 编号由维护者分配，本文不代填。

### 12.15 补全完整编辑与指令命名空间（2026-11-13）

用户实测的三个场景是本次验收入口：输入 `@using` 没有命名空间候选；选择尚未导入的类型后
没有补入命名空间；在 `@code` 成员声明处输入 `override` 没有可覆盖方法。已有的成员访问
`task.` 测试不能证明这三个编辑流程正确。

**当前契约**：

1. `@using` 参数查询项目 Compilation 的命名空间和类型，不再局限于控件目录。
   空参数、部分限定名（如 `System.Col`）、点号后的名称及别名目标必须提供候选；编辑替换
   当前限定前缀，不能重复生成 `System.System.Collections`。
2. 选择未导入的标签或 C# 类型时，补全项同时交付主 `textEdit` 与 `additionalTextEdits`。
   主编辑只替换用户正在输入的位置；命名空间按明确的模板指令规则插入 `@using`，保留
   LF/CRLF 并避免重复。命名空间在模板自身查找链或已有导入中可见时不新增指令。
3. `override` 是成员声明上下文；候选必须来自实际根基类中可覆盖且未被覆盖的成员。
   `OnInitialized`、`OnAttached` 等生命周期方法与其他基类方法遵循相同规则，不维护专用名称表。
   生成器占用的 `InitializeUi`、`Render` 不提供为作者候选。提交候选后应得到可继续编辑的
   完整声明及正确的光标位置。
4. 多处编辑不是丢弃候选的充分条件。每一处编辑可精确回映时一起交付；C# 导入不能直接
   使用生成文件的偏移，而要转换成上面的模板 `@using` 编辑。其余生成独有且无法明确转换的
   编辑仍拒绝，避免损坏模板结构。

这条规则取代 §12.10 当时「只接受单处 TextChange，多处一律丢弃」的实现限制；保留 §8
的精确映射边界，没有允许近似套用生成坐标。编译器生成文本与 `#line` 不需要为补全改写。

**编辑期恢复**：有效根标签下的无关属性错误可以阻止生产生成，但不能抹掉正确 `@code` 的
C# 补全。`EditingCSharpDocument` 在没有生产生成文档时复用 `UiCatalogFactory.WithTemplateTypes`
的根类型符号视图，把原文代码块放入临时 Roslyn Document，并用符号视图自产的 `#line` 边界与
全文匹配建立逐字映射；不生成模板结构、不替换项目快照、不清除任何真实诊断。无法解析的根类型
不虚构替代基类。缺少 `@namespace` 时只为编辑上下文暂用项目命名空间，构建仍保持原有错误。

**回归检查**覆盖 provider 与真实 LSP 会话两层：命名空间/别名候选、未导入标签与类型的最终
编辑、导入去重、LF/CRLF、override 候选与提交后的声明、已覆盖成员过滤，以及模板输入尚未
完成时的补全。`ProtocolTests` 对主编辑和附加编辑使用原文行列坐标一起应用，验证编辑器收到的
不是仅有说明文字的候选，并覆盖不支持 snippet 的客户端。`EditingCSharpCompletionTests`
检查错误模板下的真实 Roslyn 候选、重复/空代码块的精确映射，以及生产快照和诊断保持不变。
最终运行结果由本次实施报告或提交说明记录，不沿用历史测试计数。

**范围仍需区分**：服务端能接收未保存 `.cs` 缓冲区，不代表客户端已经转发该语言；当前薄壳
只订阅 `.ui/.eustyle`。通用 C# CodeFix/重构、完整 QuickInfo、格式化配置和调试器仍是独立工作，
本次不能因三项补全通过就宣布 `.ui` 已具备完整 C# IDE 功能。

**实际提交链路**：Roslyn 的未导入类型候选可能只返回类型名，其原生编辑器会另行补 using。
LSP 服务不能把这种候选原样交付；本实现将它转换为明确的类型替换和模板导入，避免同时出现一条
不导入的同名候选。若新增命名空间会使文档中的已有类型名歧义，提交完整限定类型名保留原有绑定。

Roslyn 4.9 的 override 提交会受增强 `#line` 和不完整声明后的生成成员影响。补全使用临时文档
屏蔽行映射字符（保留偏移与换行）、隔离未完成声明，再提取 Roslyn 生成的新成员和光标；临时
边界分号及生成器 trivia 不写回模板。生产生成文档、诊断与调试映射不因该编辑视图改变。

项目引用优先级的附带修正见 [引用上下文报告](./reports/ide-project-reference-context-2026-11-13.md)。
