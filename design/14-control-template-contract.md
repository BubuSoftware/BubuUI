# 14. 控件与模板引擎契约（详细设计）

> 状态：v4 换轨（2026-10-29）。控件元数据声明规则仍以本文为准（待迁移为 `spec/04`）；
> 模板反应式、生成形态与组件参数语义已分别由 [spec/03](../spec/03-reactivity-and-binding.md)、
> [spec/02](../spec/02-generated-code-contract.md) 取代。2026-10-28 首版与 v2/v3 修订记录保留在 14.15–14.17。
> 实现按 14.12 的 C1–C6 与 [spec/03 §13](../spec/03-reactivity-and-binding.md) 的 S1–S5 分阶段落地。
> 关联：[03 模板引擎](./03-template-engine.md)、[04 运行时与元素系统](./04-runtime-and-elements.md)、
> [09 开发计划](./09-development-plan.md)、[13 damage v0](./13-g2-damage-v0.md)、
> [15 状态与反应式（v3 历史）](./15-state-and-reactivity.md)。
> 范围：C# 控件与 `.ui` 组件如何向模板引擎声明接口；模板如何校验、生成代码、在运行期更新。
> 样式、主题、ControlTemplate（换肤）不在本文，见 G4。
>
> **规范迁移（2026-10-29）**：§14.8 的生成代码契约已由 [spec/02](../spec/02-generated-code-contract.md) 取代；
> 模板侧语法以 [spec/01](../spec/01-template-syntax.md) 为准。
>
> **v4 换轨**：模板反应式改为**组件级失效重算**（spec/03）；模板状态是普通 CLR 成员。
> 组件参数一律是普通属性：父 Build/渲染推送 + 相等保护 + 子 `Invalidate()`；双向用 `{P}Changed` 回调。
> `[UiState]`/`[UiObservable]`/`[UiComputed]`/`Signal<T>` 参数/EUI044 不再是契约（EUI044 停用）。
> §14.6–§14.8、§14.7.1、§14.7.4 中与反应式/生成形态冲突的条款由 spec/02、spec/03 取代；
> 14.15–14.17 的修订记录为历史，不再作为实现依据。

## 14.1 现状与问题

当前"控件 ↔ 模板引擎"只有一套隐式约定，没有正式契约：

| 事实 | 证据 | 后果 |
|---|---|---|
| 目录手写、封闭 | v3 的 `UiCatalog.CreateDefault()` 固定 5 个标签（手写目录，C2 已删除） | 新增控件必须改编译器；第三方无法进入模板 |
| 元数据双写 | 控件侧 `Button.LabelProperty`（[Controls.cs:9](../src/EclipseUI.Runtime/Controls.cs#L9)）与目录侧 `new UiPropDef("Label","string")` 各写一份 | 漂移时生成 C# 编译错误，而不是 `.ui` 诊断 |
| 生成器隐式约定 | [Emitter.cs:144](../src/EclipseUI.Compiler.Generator/Emitter.cs#L144)：`new ClrType()`、属性名=CLR 属性名、`:bind` 需 `{Name}Property`、事件挂 `{Name}Event` | 契约只存在于代码行为里，控件作者无从遵循 |
| 暴露面不全 | `Panel.Padding`/`ClipToBounds` 已注册（[Panels.cs:15](../src/EclipseUI.Runtime/Panels.cs#L15)），但目录的 StackPanel 只列了 `Gap` | `<StackPanel Padding="16">` 报 EUI002 |
| 无法自定义 | 未知标签 EUI001；组件 `Component` 只能在 C# 里 `Mount()` | 无法用 `.ui` 组合自定义控件 |
| 无契约测试 | v3 时 `tests/` 无生成器集成测试；前端测试直接用 `CreateDefault()`（现已有目录扫描/生成器契约/交叉一致性测试） | 目录与控件漂移无回归保护 |

契约表面的实测对比（本文规则生效后 vs v3 手写目录，用于 14.12 的行为变更记录）：

| | 手写目录（v3 现状） | 符号派生（本文） | C1/C2 实测（2026-11-02） |
|---|---|---|---|
| 标签 | 5 | 5（另有 `ConditionalBlock`、`TextRun` 需 `[UiIgnore]`；`Element`/`Panel`/`StackPanelBase`/`Component` 落入 HiddenTags） | 5 + HiddenTags 4 ✅ §14.12.1 |
| 模板可见属性项 | 6 | 17（`Padding`、`ClipToBounds`、`IsHitTestVisible` 全面铺开） | 17 ✅ 差集 12 项逐条评审 |
| 模板可见事件 | 1（`OnClick`） | 8（含输入/焦点 7 个，前提是按 14.4.3 v2 把 `InputEvents` 迁入 `Element`） | C1 实测 1（`InputEvents` 静态宿主 → EUI026，D69）→ **C2 ✅ 9 个事件名 / 37 事件项**（5 标签 × 7 继承事件 + `OnClick` + `OnCheckedChanged`，见 §14.12.2） |

「C1 实测」列扫的是已编译的 `EclipseUI.Runtime.dll`，见 `CatalogScannerTests`；标签/属性项在 C2 后数值不变，
事件行随 `InputEvents` 迁入 `Element` 变宽。**C2（2026-11-02）起模板实际可见表面已等于本文规则**
（D64/D69 关闭，手写目录删除），落地记录见 §14.12.2。

本文把上述隐式约定升级为**可声明、可校验、可扩展、可被 LSP 复用**的正式契约。

## 14.2 目标与非目标

**目标（按优先级）**

- **T1 单一事实源**：控件元数据从 C# 符号派生；目录不再手写。
- **T2 编译期全量校验**：标签、属性、事件、内容、字面量错误都在 `.ui` 上带 span 报出。
- **T3 第三方可扩展**：NuGet 控件/组件不改框架源码即可使用；只读符号，不执行程序集。
- **T4 AOT/裁剪友好**：运行时不反射、不扫描程序集；生成代码直接引用类型。
- **T5 LSP 同源**：分析器、生成器、LSP 共用同一份 `UiCatalog` 与诊断。
- **T6 能力对等**：模板能表达的，手写 C# 都有等价公开 API（R4）；模板表达不了的，用 code-behind 逃生舱（03 §3.7）。

**非目标（本期不做，但冻结语法/元数据形状）**

- 样式、主题、ControlTemplate/选择器（G4）。
- 附加属性的**实现**（G4；形状见 14.5.5）；泛型标签按 K19 暂缓实现、不排期（形状仍冻结）。
- 组件参数 diff / 结构重渲染（14.6.6）；参数推送只改属性并失效子组件，不重建组件模板。
- 运行时反射发现控件（明确禁止，见 R1）。
- XAML 兼容。

## 14.3 契约分层与总原则

| 层 | 事实源 | 消费者 | 时机 |
|---|---|---|---|
| 声明 | C# 类型、`StyledProperty`、`RoutedEvent`、`[Ui*]` 特性 | 符号扫描器 | 编译期 |
| 目录 | 不可变 `UiElementDef` POCO | 分析器 / 生成器 / LSP | 编译期 |
| 模板 | `.ui` 文本 | Lexer / Parser / Analyzer | 编译期 |
| 生成 | 直接引用类型的 C# | Roslyn | 编译期 |
| 运行 | 元素树 + 属性引擎 + 失效队列 | 帧循环 | 运行时 |

**R1** 编译期目录只读符号，不执行用户程序集。
**R2** 生成代码只做直接构造/赋值/块刷新/绑定，不做字符串查找、反射。
**R3** 模板赋值与双向绑定必须经过公开 CLR 属性 setter → `StyledProperty` → 失效标记，不得用 `SetValue/GetValue` 绕过属性引擎。
**R4** 每个模板构造都有等价的手写 C# 公开 API。
**R5** 元数据不合法不得让生成器崩溃：问题进入目录，按使用点报诊断。
**R6** 同一输入（元数据 + 模板）生成确定性输出，便于增量缓存与 golden 测试。
**R7** 生成的绑定/块刷新只调用公开运行时 API（`UiBinding`、`Component`/`BlockScope`、元素公开访问器）。
**R8** 每个生成资源都有唯一作用域所有者；不得存静态字段；释放幂等；组件/条目/分支卸载后无残留。
**R9** 依赖分析不执行用户代码、不加载用户程序集；生成文本只依赖输入（目录 + code-behind 签名 + 模板），确定性可复现。

R7–R9 的 v3 出处是 [15 §15.5.7](./15-state-and-reactivity.md#1557-新增不变量-r7r9)（已停止）；v4 以本节定义为准。违反任一条的实现不得合入。
旧 `Bind.TwoWay` 的 `SetValue/GetValue` 实现违反 R3，S3 起改用 `UiBinding`，旧重载保留但标 Obsolete。

## 14.4 控件作者契约

### 14.4.1 标签暴露

一个类型成为模板标签，必须同时满足：

1. 是 `EclipseUI.Element` 或 `EclipseUI.Component` 的子类（基链判定，按 metadata name）；
2. `public`、顶层（非嵌套）、非抽象、非泛型；
3. 有 `public` 无参构造函数；
4. 类型上没有 `[UiIgnore]`。

标签名规则：

- **恒等于类型短名**（`EclipseUI.Elements.Button` → `Button`）。没有改名手段：`[UiTag]` 已于 2026-11-09 退役（K32）。
- **不做命名空间优先级**。同一标签映射到 ≥2 个类型时，使用点报 EUI016 并列出候选全名，
  同时给出可直接粘贴的消歧建议 `@using <Tag>Alt = <Ns>.<Tag>`（模板别名，见 K31 / spec/01 §4.17）。
  消费端拿别名解决，**不需要、也无法**让控件作者改标签名。
- 例外：框架内部/基础设施元素必须标 `[UiIgnore]`，避免意外成为标签。当前需要标注的有
  `TextRun`、`ConditionalBlock`、`ForEachBlock<T>`（泛型本就不暴露，仍显式标注）。

诊断口径：

- 非 `public` 类型不进入目录，模板写到它 → EUI001（不可见）。
- `public` 但抽象/嵌套的类型进入**不可用索引**，写到它 → EUI017（说明原因）。
- 缺 public 无参构造 → EUI018；泛型标签在语法落地前 → EUI030。
- 不可用索引同时用于"最近名"建议，避免把拼错的名字指向不可用类型。

### 14.4.2 属性暴露

属性 P 进入模板属性表，必须满足：

1. P 是 `public` 实例属性，有 `public` getter 与 `public` setter；
2. P 的声明类型（或基类）存在 `public static readonly StyledProperty<T> PProperty`；
3. `T` 与 P 的类型一致；
4. P 上没有 `[UiIgnore]`。

**默认暴露（opt-out）**，理由：`StyledProperty<T>.Register` 本身就是"这是公开属性模型"的显式声明，
再要求 `[UiProperty]` 会制造第二份可漂移的元数据。隐藏用 `[UiIgnore]`。

配套规则：

- **继承**：派生控件暴露基类属性。例：`StackPanel` 自动获得 `Panel.Padding`、`Panel.ClipToBounds`、
  `Element.IsHitTestVisible`。模板名不变。
- **普通 CLR 属性不暴露**：没有 StyledProperty 的属性不进入模板（失效语义不可控）。组件参数是唯一例外（14.6）。
- **只读属性不暴露**：v1 没有"展示型单向源绑定"，无 setter 的属性留在 C# 侧。
- **失效契约**：控件作者 MUST 为每个 StyledProperty 声明正确的 `Invalidate` 级别
  （`Measure` / `Arrange` / `Render` / `None`）。模板引擎只负责赋值，不猜测失效范围；
  声明 `None` 的属性变更不保证重排/重绘，这是控件责任。
  **编译期无法校验这条**：`Invalidate` 来自 `StyledProperty<T>.Register(...)` 的构造实参
  （[PropertySystem.cs:48](../src/EclipseUI.Runtime/PropertySystem.cs#L48)），是运行期事实，R1 禁止在编译期执行它（D13）。
  守卫只能落在 14.11.1 的运行期快照测试：模板可见属性的 `Invalidate` 非 `None`，例外必须写进显式白名单
  （实测现状：`Panel.ClipToBounds` 与 `Element.IsHitTestVisible` 都注册为 `None`，前者必须在 C2 修为 `Render`，
  后者确实不需要重绘 → 白名单）。
- **不得与事件属性同名**：暴露属性名 MUST NOT 匹配 `On{已暴露事件名}`（如 `Button.OnClick` 属性），
  否则会被 14.5.3 的事件优先静默吃掉 → 元数据问题 EUI026。同理不得占用 `Key`/`Ref`/`Class`/`Style`。
- **属性名 = 模板属性名**，大小写敏感，不做别名映射（需要时改名属性或加包装属性）。
- 属性类型决定字面量可用性：无转换器的类型只能写 `{expr}`（EUI023）。

示例：迁移后 `<StackPanel Gap="8" Padding="16" ClipToBounds="true" />` 合法，
这是相对当前目录的**有意行为变化**（14.12）。

### 14.4.3 事件暴露

事件 E 进入模板事件表，必须满足：

1. E 声明在 `Element`/`Component` **或其派生类型**上，形如 `public static readonly RoutedEvent EEvent`；
2. 字段名以 `Event` 结尾；模板属性名为 `On{字段名去掉 Event}`（`ClickEvent` → `OnClick`）；
3. 字段上没有 `[UiIgnore]`。

**静态宿主类不是合法的事件声明位置（v2 修订，D11）**。路由事件的语义是"在元素树上传播"，
声明位置必须落在元素/组件类型上，派生类型才能继承；声明在 `public static class` 里的事件字段
既不参与扫描，也在源项目声明处报 EUI026（消息给出迁移建议）。

框架自己曾是反例：v3 把 7 个输入/焦点事件声明在 `EclipseUI.InputEvents` 静态类上
（旧 `Input.cs:20-28`）。若沿用 v1 规则，换符号目录后模板能写的事件只剩
`OnClick` 一个，而 C# 侧 `AddHandler(InputEvents.XEvent, …)` 有 19 处调用——直接违背 T6 能力对等。
**处置已落地（C2，2026-11-02）**：7 个字段迁入 [Element.cs:118-124](../src/EclipseUI.Runtime/Element.cs#L118)
（`PointerPressed/Released/Moved`、`KeyDown/Up`、`GotFocus/LostFocus`），`InputEvents` 类删除，
模板事件表面由 1 个变为 8 个（加 `CheckBox.CheckedChanged` 后共 9 个事件名）。控件作者同理：
新增事件一律声明在自己的类型上，不要建静态宿主类。

同期控件补齐（C2 已落地，不属于契约本身）：`CheckBox` 补上 `CheckedChangedEvent`
（[Controls.cs](../src/EclipseUI.Runtime/Controls.cs)）。语义注意：事件只在交互路径广播，
绑定回推走 setter 且不广播，避免模板的 `OnCheckedChanged` 被源写回再次触发。

语义：

- `On{E}` MUST 使用 `{expression}`；字面量报 EUI004；`:bind` / `@bind-` 用于事件报 EUI004/EUI040。
- 处理器允许转换为 `Action` 或 `Action<RoutedEventArgs>`（对应现有两个 `AddHandler` 重载，
  [Element.cs:221](../src/EclipseUI.Runtime/Element.cs#L221)）。参数类型更强的 typed args 属 G4。
- 冒泡/隧道由 `RoutedEvent.Strategy` 决定，与模板无关。
- 生成代码使用字段的**声明类型**引用（继承事件用基类名）：
  `global::EclipseUI.Elements.Button.ClickEvent`、`global::EclipseUI.Element.PointerPressedEvent`。

### 14.4.4 内容模型

```csharp
// 运行时新增
public enum UiContentKind { None, Children, Text, TextRuns }

public interface ITextContentHost
{
    TextRun AddRun(string text); // 返回 run，供定向更新
}
```

| 模型 | 允许的子内容 | 生成目标 | 违规诊断 |
|---|---|---|---|
| `None`（默认） | 无 | — | EUI006 |
| `Children` | 元素、`@if`、`@foreach`；纯空白文本忽略 | 目标集合 `.Add(Element)` | 非空白文本/非法结构 → EUI006 |
| `Text` | 文本段与 `{expr}` | 目标 `string` 属性（如 `Button.Label`） | 元素子内容、控制流 → EUI006 |
| `TextRuns` | 文本段与 `{expr}` | `ITextContentHost.AddRun(string)` | 元素子内容、控制流 → EUI006 |

声明方式：

```csharp
[UiContent(UiContentKind.Children)]                 // class 级
public abstract class Panel : Element { ... }

[UiContent(UiContentKind.Text, Target = nameof(Label))]
public sealed class Button : Element { ... }

[UiContent(UiContentKind.TextRuns)]
public sealed class TextBlock : Panel, ITextContentHost
{
    public TextRun AddRun(string text) { var run = new TextRun(text); AddRun(run); return run; }
}
```

- **默认无特性 = `None`**，不猜测：内容模型是公共 API 的一部分，必须显式。
- `Target` 指向成员名；省略时按模型默认：`Children` → `"Children"`，其余必须显式。
- 校验：目标存在、可访问、类型匹配（`Children` 目标需有 `Add(Element)`；`Text` 目标为 `string`；
  `TextRuns` 要求类型实现 `ITextContentHost`）。失败 → EUI021 / 元数据问题 EUI026。
- 选择接口而不是"约定方法名"，是为了让编译器可验证、错误可定位。
- `TextRuns` 引入 `ITextContentHost` 是对 `TextBlock` 的小改动：保留现有 `AddRun(TextRun)`，
  增加 `AddRun(string)`。

### 14.4.5 构造、线程与生命周期

- 模板创建的实例一律 `new T()`：**MUST** 有 public 无参构造（EUI018）。
- 所有构造、属性赋值、事件订阅都在 UI 线程同步完成；模板 Build 不异步、不等待。
- 控件 MUST NOT 在构造函数里假设已有父元素、假设已完成布局、或调用 `Measure/Arrange`。
- 控件的构造与属性 setter MUST 无副作用地可重复执行（`@if`/`@foreach` 会重建子树）。
- 控件释放资源用 `OnUnmount` 钩子（G4/K33）：`Element.Unmount()` 子树级触发
  `UnmountedEvent`（Direct）与钩子；控件自身仍不实现 `IDisposable`。
- `Ref`（G3）用于把元素实例交给 code-behind 字段；在此之前用 C# 侧的直接引用。

### 14.4.6 声明特性

```csharp
namespace EclipseUI;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class UiContentAttribute : Attribute
{
    public UiContentAttribute(UiContentKind kind = UiContentKind.Children);
    public UiContentKind Kind { get; }
    public string? Target { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class UiIgnoreAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class UiParameterAttribute : Attribute      // 组件参数，14.6
{
    public bool Required { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class UiSlotAttribute : Attribute          // 具名插槽，✅ C6
{
    public UiSlotAttribute(string name);
    public string Name { get; }
}
```

- 特性定义在 `src/EclipseUI.Runtime`：**程序集名是 `EclipseUI.Runtime`，命名空间是 `EclipseUI`**
  （csproj 只设了 `RootNamespace`，未覆写 `AssemblyName`，实测反射得到 `EclipseUI.Runtime`）。
- 编译器/生成器按 **全 metadata 名**识别特性（`EclipseUI.UiContentAttribute`、`EclipseUI.UiIgnoreAttribute`…），
  **不得按程序集名匹配**；不引用运行时程序集，避免生成器加载用户程序集。
- `UiContentKind` 与编译器内部 `UiContentModel` 的数值必须一一对应；测试断言两者一致（14.11）。

### 14.4.7 控件作者检查清单

1. `public sealed class X : Element`（或 Panel），public 无参构造。
2. 每个模板可设属性：`XProperty` 注册 + CLR 包装属性 + 正确的 `Invalidate`。
3. 需要从模板触发的事件：`public static readonly RoutedEvent YEvent` **声明在本类型上**，不放静态宿主类。
4. 声明 `[UiContent]`；不参与模板的公共属性/字段/类型加 `[UiIgnore]`。
5. 类型短名即标签名；与已有标签重名时**不要改名妥协**——消费端用 `@using XxxAlt = Ns.Xxx` 别名消歧（K31/K32）。
6. 写一个使用该控件的 `.ui` 集成测试（14.11）。
7. 确认属性在无父元素时 set 不会抛异常；重复 set 幂等。

## 14.5 目录与元数据模型

### 14.5.1 数据模型

```csharp
public enum UiElementOrigin { Control, Component }
public enum UiSlotCardinality { Single, Multiple }

public sealed record UiPropDef(
    string Name,                  // 模板属性名
    string ClrTypeDisplayName,    // Roslyn FullyQualifiedFormat，已含 global::；生成代码文本直接用
    string DeclaringTypeName,     // 属性的声明类型，同上格式
    string PropertyFieldName,     // "{Name}Property"
    bool CanWrite,
    UiTypeShape TypeShape);

public sealed record UiEventDef(
    string AttributeName,         // OnClick
    string EventName,             // Click
    string DeclaringTypeName,     // 声明该字段的元素/组件类型（继承事件为基类）
    string FieldName,             // ClickEvent
    string ArgsTypeName);         // global::EclipseUI.RoutedEventArgs

public sealed record UiParameterDef(
    string Name,
    string ClrTypeDisplayName,
    bool CanWrite,                // false = init/无 setter → EUI028
    bool Required,                // 来自 [UiParameter(Required = true)]，不是 C# required
    bool IsDelegate,
    string? ChangedCallbackName); // {P}Changed（Action<T>?）参数名；@bind 目标判定用，缺失为 null

public sealed record UiSlotDef(
    string Name,
    string PropertyName,
    string ClrTypeDisplayName,
    UiSlotCardinality Cardinality);

public sealed record UiElementDef(
    string Tag,
    string ClrTypeDisplayName,
    UiElementOrigin Origin,
    UiContentModel Content,
    string? ContentTarget,
    IReadOnlyDictionary<string, UiPropDef> Properties,
    IReadOnlyDictionary<string, UiEventDef> Events,
    IReadOnlyDictionary<string, UiParameterDef> Parameters,
    IReadOnlyDictionary<string, UiSlotDef> Slots,
    IReadOnlyList<UiMetadataProblem> Problems);

public sealed record UiMetadataProblem(
    string Code,                 // EUI0xx
    string Message,
    string DeclaringTypeName,
    string? MemberName);

public sealed record UiHiddenTag(
    string Tag,
    string ClrTypeName,
    string ReasonCode,           // EUI017 / EUI018 / EUI030
    string Reason);              // 人类可读，用于使用点诊断

public enum UiTypeShapeKind { Primitive, Enum, Delegate, Signal, Other }

public sealed record UiTypeShape(
    UiTypeShapeKind Kind,
    string CanonicalKey,                  // 转换器与绑定泛型匹配的唯一依据（见下）
    IReadOnlyList<string>? EnumMembers);
```

**类型名的两个字段各管一件事（v2 修订）**：

- `ClrTypeDisplayName` 一律取 `SymbolDisplayFormat.FullyQualifiedFormat` 的结果（`global::System.Collections.Generic.List<EclipseUI.TextRun>`
  这类嵌套/泛型/可空形态由 Roslyn 负责，扫描器不自己拼字符串）。
- `UiTypeShape.CanonicalKey` 是查表键：内置类型用 C# 关键字名（`double`、`string`、`bool`、`int`…，
  与现有 `UiLiterals.TryConvert(clrType, …)` 的键一致，[Catalog.cs:84](../src/EclipseUI.Compiler/Catalog.cs#L84)），
  其余用不含 `global::` 的命名空间限定全名。v1 用裸 `"double"` 字符串同时充当生成类型名和转换器键，
  换成派生目录后必然出现"`global::System.Double` 查不到转换器"这类静默失配。

`UiCatalog` 仍是不变集合 + 派生索引：

```csharp
public enum UiTagResolutionKind { Found, NotFound, Ambiguous, Unavailable }

public readonly record struct UiTagResolution(
    UiTagResolutionKind Kind,
    UiElementDef? Element,
    IReadOnlyList<string>? Candidates, // Ambiguous 时的候选全名
    UiHiddenTag? Hidden);              // Unavailable 时的原因

public sealed class UiCatalog
{
    public UiTagResolution Resolve(string tag);
    public IReadOnlyList<UiElementDef> Elements { get; }
    public IReadOnlyList<UiHiddenTag> HiddenTags { get; }
    public IReadOnlyList<UiMetadataProblem> Problems { get; }
    public IEnumerable<string> ElementTags { get; }
}
```

### 14.5.2 与现状映射

| 现状 | 新模型 | 说明 |
|---|---|---|
| `UiElementDef.ClrType` | `ClrTypeDisplayName` | FullyQualifiedFormat，自带 `global::`；查表另用 `CanonicalKey` |
| `UiElementDef.Props`（含事件） | `Properties` + `Events` | 事件单独建模，命名规则可校验 |
| `UiContentModel.ElementChildrenOnly` | `Children` | 同一语义 |
| `UiContentModel.LabelText` | `Text` | 同一语义 |
| `UiContentModel.TextRuns` | `TextRuns` | 目标改为 `ITextContentHost` |
| — | `Origin`、`Parameters`、`Slots`、`Problems`、`TypeShape` | 新增 |

枚举重排为 `None=0, Children=1, Text=2, TextRuns=3`，与运行时 `UiContentKind` 数值一致（测试断言）。

### 14.5.3 属性/事件解析顺序

模板属性名解析优先级（按 `UiElementDef.Origin` 分支，v2 明确）：

- **控件**：保留属性（`Key`/`Ref`/`Class`/`Style`）→ 事件（`On*`）→ `Properties` → 附加属性（G4）→ 未知。
- **组件**：保留属性 → `Parameters`（委托参数就是事件，14.6.2，不做 `On*` 前缀推断）→ 未知。
  控件与组件的发现规则不对称（D2 vs D3），解析顺序必须分支，否则组件的 `OnClose` 委托参数会被当成事件字段。
- 未实现但已保留的写法报保留诊断，不报 EUI002/EUI001 的"未知"，避免用户在正确语法上收到拼写错误提示：
  `Class`/`Style`/附加属性 → **EUI031**；泛型标签 → **EUI030**；`Ref` 目标非法 → **EUI029**；
  `:bind`/`:value` → **EUI043** Warning（弃用别名，不是保留）。
  具名插槽 `<Slot>`/`<ContentSlot>` 是解析器特判的**句法保留名**（✅ C6）：使用点错误走 **EUI022** 家族；
  目录把占用这两个名字注册的控件报 EUI031 并进 HiddenTags。
- `@bind-P` 在控件分支按属性表解析，在组件分支按参数表解析；解析失败先 `EUI002`，解析成功但目标不可绑 → `EUI040`。

未知时在**全部命名空间**里找最近名（Levenshtein ≤ 3）给建议，跨类别也提示（"did you mean event 'OnClick'?"）；
若名字命中一个**存在但没有 `{Name}Property` 的公开 CLR 属性**，提示改为"该属性未注册 StyledProperty，不参与模板"（v2 修订，见 14.9.1 EUI019 口径）。

### 14.5.4 元数据问题

目录构建不抛异常。无法满足声明规则的候选（缺字段、目标缺失、特性值未知等）进入 `Problems`：

- 源项目中的控件：在声明处报 EUI026（属性/特性 span 可用时）。✅ 2026-11-08（D74 ②/D81）：
  `UiMetadataProblem` 携带纯数据 `UiSourceLocation`，生成器的目录级出口同时消费
  `catalog.Problems`（类型级）与 `def.Problems`（成员级），成员符号声明优先、类型声明兜底；
  源项目 EUI019/020/021 problem 折叠为 EUI026，EUI026/EUI028/EUI031 保持原码。
- 引用程序集中的控件：无法定位源码，按使用点报 EUI019/EUI020/EUI021，消息带完整类型名。
- 特性枚举值来自更新版本运行时而当前生成器不认识：降级为 `None` + 问题，不崩溃（前向兼容）。
- 已进入目录但声明非法的三类（v2 补充）：事件字段声明在非元素类型/静态宿主类上、
  暴露属性名占用 `On{事件}` 或保留属性名、`[UiContent]` 目标成员不存在或类型不符。

### 14.5.5 附加属性与泛型标签（形状冻结；附加属性 G4，泛型标签暂缓 K19）

- 附加属性声明：`public static readonly AttachedProperty<T> RowProperty` + `public static GetRow/SetRow`。
- 模板语法：`Owner.Prop="..."`（如 `Grid.Row="1"`）。目录增加 `UiAttachedPropDef(OwnerTag, Name, ClrTypeDisplayName, SetterMethod)`。
- 生成：`global::Ns.Grid.SetRow(el, 1);`。
- 泛型标签：`<DataGrid<Order>>`，要求类型 public、非抽象、有对应元数据；标签名带元数（`DataGrid\`1`），
  模板侧写实参。元数据模型预留 `Arity`；附加属性实现排 G4，泛型标签按 K19 暂缓、不排期（形状保留冻结）。

## 14.6 组件契约（`.ui` + code-behind 作为标签）

组件是 `EclipseUI.Component` 子类；它的标签暴露规则与控件相同（14.4.1），额外提供参数、事件、内容与生命周期。
**v4 起组件参数只有一种形态：普通 CLR 属性**（spec/03 §10）。

### 14.6.1 参数

- `[UiParameter]` 标在 `public` 属性上；必须有 `public` setter（`init` 不允许）。
- 参数类型任意；字面量按 14.7.3 转换；表达式由 Roslyn 类型检查。
- **参数是普通属性**：不要求 `[UiState]`/`[UiObservable]`、不要求 partial、不要求 `INotifyPropertyChanged`；
  `Signal<T>` 仅作为普通参数类型（legacy），不再触发模板订阅或 `.Value` 重写。
- **更新语义**（spec/03 §10.2）：父组件在 Build 与每次 `Render` 求值动态参数；
  值变化时写子参数并 `Invalidate()` 子组件，相等保护用 `EqualityComparer<T>.Default`。
  无参数 diff、不重跑子组件 `Build()`；参数推送只让子组件重算自己的动态表达式。
- **参数的可见性**：参数值来自普通属性/字段；父只通过标签赋值，不能访问子组件内部状态。
- **必填参数**用 `[UiParameter(Required = true)]`，模板缺参报 EUI027；缺省值来自构造函数赋值。
- **不用 C# `required` 修饰符（v2 修订，D12）**：生成形态是 `new C()` 之后逐条赋值（spec/02 §4.12），
  而 C# `required` 成员在未满足时 `new C()` 本身就编译失败——实测 `CS9035 必须在对象初始值设定项或属性构造函数中设置所需的成员`。
  改用 `new C { ... }` 会打乱"按源码顺序赋值"和表达式求值顺序。参数上的 `required` 修饰符与 `init` setter
  一并按 EUI028 报出，消息指向 `[UiParameter(Required = true)]`。
  （判定可行：Roslyn 4.9.2 公开 API 有 `IPropertySymbol.IsRequired` 与 `IMethodSymbol.IsInitOnly`，实测反射确认。）
- 参数属性不得是 `[UiIgnore]`（忽略即不可传入）。
- **参数类型约束**：不得是 `ref struct`/指针/`Span<T>`（无法做 `EqualityComparer<T>`）→ `EUI028`；
  委托参数须返回 `void`、不含 `ref`/`out`/`in` → `EUI028`。
- **`@bind-P` 目标**必须是 `[UiParameter]` + public setter，并且组件声明了同名回调参数
  `[UiParameter] public Action<T>? PChanged { get; set; }`；回调类型与 `P` 不一致、或目标为委托/内容/槽/保留参数 → `EUI040`。
  回调由父模板在 Build 赋一次，子组件在内部动作中修改参数后调用 `PChanged?.Invoke(next)`（spec/03 §8.3）。
- **EUI044 停用**：v3 的"非可观察参数不刷新"护栏不再存在；所有参数都走推送 + `Invalidate()`，没有可观察性判定。
- 参数的赋值目标只来自 `Parameters`；组件上若有形如 `{Name}Property` 的字段不参与组件属性解析（组件不是 `Element`）。

### 14.6.2 组件事件

- v1 不做组件级路由事件。组件事件 = **委托类型参数**（`Action`、`Action<T>` 等），
  模板写法 `OnClose={() => ...}`，按普通参数赋值。
- 委托类型参数 MUST 用 `{expression}`；字面量报 EUI003/EUI024；`@bind-` 指向委托参数 → EUI040。
- 委托参数 build-once；父模板用 `h{N}` 委托局部包装，调用后失效父组件（spec/02 §4.6.2）。
- 需要跨组件冒泡时，组件把内部根元素的 `RoutedEvent` 转成参数回调。
- 双向参数的回调 `{P}Changed` 也是委托参数，但由 `@bind-P` 生成；它不是路由事件。

### 14.6.3 内容与插槽

- 默认内容：`[UiContent]` 标在参数属性上，类型为 `Element?`（单根）或 `IReadOnlyList<Element>`（多根）。
  消费侧未包 `<Slot>` 的子内容进入默认内容；其他类型 → `EUI021`。
- 内容由父模板 **build-once 静态展开**：`{expr}`/`P={expr}` 是 Build 时快照；内容中的 `@if`/`@foreach` 生成普通 C# `if`/`foreach`，不参与失效重算。
- 内容体内不得出现 `@bind`（`EUI040`）；`Key` → `EUI010`；裸文本 → `EUI006`。
- 内容中的子组件用 `receiver.TrackContent(cmp)` 登记到接收方，随接收方释放；不得 `Track` 到模板父组件。
- 内容中的事件处理器仍失效模板父组件（表达式属于父模板），接收方不得比父组件活得更久。
- 具名插槽（✅ C6 2026-11-05）：`[UiSlot("Header")]` 标在属性上（`Element?` 单根 /
  `IReadOnlyList<Element>` 多根，其他类型 → 声明期 EUI021）；消费侧 `<Slot Name="Header">...</Slot>`
  只允许组件标签直属子级；组件模板侧 `<ContentSlot Name="Header" />` **不是新的运行时宿主元素**——
  生成器在组件自身 `Build()` 的该位置按 build-once 语义内联拼接插槽属性
  （Single：`if (Header is { } s) parent.Children.Add(s);`；Multiple：`foreach (var s in Rows)`，
  spec/02 §4.12.5）。
- 自身插槽解析（D75）：生成器用 code-behind 符号沿基类链读 `[UiSlot]` 作权威（code-behind
  类在源码里不派生 `Component`，目录不会有它的 def）；静态前端按目录显示名与 `.ui` 文件名
  短名匹配投影（spec/02 §4.12.5 / design/14 §14.12.6）。
- 插槽/内容在 `Mount()` 前赋值；父级状态变化不重建内容（与 14.6.6 一致）。
- 其他参数与默认内容规则 v1 不变；具名插槽 C6 已落地，剩余泛型标签/附加属性见 §14.12.6 范围说明。

### 14.6.4 生命周期

生成形态（详见 spec/02 §4.12）：

```csharp
var cmp0 = new global::App.UserCard();
Track(cmp0);                 // 根作用域；块内为 Adopt(cmp0) + scopeN.AddDisposable(cmp0)
cmp0.Title = "Hi";           // 参数按源码顺序
U0();                        // 动态参数推送 / @bind 的 PChanged 赋值
cmp0.Body = content5;        // 默认内容（若有）
cmp0.Mount();
parentN.Children.Add(cmp0.Root);
```

- 顺序固定：`new` → 作用域登记 → 参数（源码顺序）→ 内容 → `Mount()` → 挂父级。
  先登记后赋值：参数 setter/内容构建/`Mount()` 失败时，调用方仍可释放组件。
- 组件实例由调用方作用域释放：根 → 父组件 `Dispose`；`@if` → 内容卸载；`@foreach` → 条目移除。
  组件内容内的子组件由接收方 `TrackContent` 登记；`Dispose` 幂等。
- 从元素树中**手工** `Children.Remove` 仍不触发 Dispose（保守原语义，保护重挂载场景）；
  显式 `root.Unmount()` 触发子树 `UnmountedEvent`，组件根订阅后自 `Dispose`（G4/K33）。
  组件 `Dispose` 会退订该事件，不产生元素树 → 组件的强引用（泄漏回归锁定）。
- `Key=` 仅 `@foreach` 模板根；key 或 item 身份变化 = 销毁旧实例 + 新建。
- `Ref=`（G3）绑定组件实例到 code-behind 字段。

### 14.6.5 第三方组件

- 包作者在类库中放置 `.ui` + code-behind，引用与本项目同版本的生成器与运行时；生成代码编入类库。
- 消费端只读类库符号（类型 + 特性 + 生成方法签名），不执行类库代码（R1）。
- 类库 MUST NOT 依赖消费端的 code-behind；跨边界只通过普通参数、委托参数、元素内容（`Signal<T>` 仅作 legacy 类型）。

### 14.6.6 已知限制（必须写进用户文档）

1. 组件没有参数 diff，不重跑 `Build()`；但父组件每次 `Render` 都会推送动态参数并失效子组件，
   子组件重算自己的动态表达式（不是"普通参数变化不刷新"）。
2. 参数推送只改属性并 `Invalidate()`，不改变子模板结构；插槽/内容 build-once，不因父状态变化重建。
3. 组件没有"参数默认值 diff"；条目复用触发的参数推送整体重赋值，setter 必须幂等。
4. `@foreach` 复用条件是 **Key 相同且 item 身份相同**；同 Key 不同 item 会重建条目与组件，禁止闭包捕获旧 item。
   item 成员可用于文本/双向绑定；item 模板可以读取组件级状态（`EUI014` 停用）。
5. 组件不做路由事件（14.6.2）；跨组件通信走委托参数。
6. 组件内部状态不算参数，父级不可见；子组件修改参数需要向上同步时调用 `PChanged`（§14.6.1）。
7. 非 UI 事件（定时器、后台线程）修改的自有状态必须显式 `Invalidate()`；框架不观察任何对象。

## 14.7 模板语义规范

### 14.7.1 属性值分类

| 写法 | 语义 | 生成时机 | 类型/诊断 |
|---|---|---|---|
| `P="literal"` | 编译期转换，一次性赋值 | Build | 转换器（EUI003/EUI023） |
| `P={expr}` | **单向**：每次组件渲染重算；控件属性相等短路，组件参数相等保护 + 子 `Invalidate()` | Build 首评 + `Render` | 目标可写；不做依赖分析（EUI019/EUI002） |
| `@bind-P={expr}` | 双向绑定；源是可写左值 | Build + 双向 | `UiBinding` / `PChanged`（EUI037/EUI040） |
| `P:bind={expr}` | 旧语法 ≡ `@bind-P`；S3 起 `EUI043` Warning | 同 `@bind-P` | 同 `@bind-P` |
| `P:value={expr}` | 旧语法 ≡ `P={expr}`；S3 起 `EUI043` Warning | 同 `P={expr}` | 同 `P={expr}` |
| `OnE={handler}` | 事件订阅 | Build | `AddHandler` 重载（EUI004/EUI024） |

- `P={expr}` 的语义由 [spec/03 §5](../spec/03-reactivity-and-binding.md) 定义：组件级失效重算，不做依赖分析；
  所有合法表达式都是动态的（没有 `EUI036`/`EUI038`）。
- 文本内容 `{expr}` 与属性赋值同一套重算规则（spec/03 §5.3）。
- `@bind-P` 要求：目标可写（控件属性需 `{Name}Property`；组件需 `{P}Changed`）、源是可写左值；
  item 内源可以 rooted at item 或组件状态（`EUI042` 停用）。详见 [spec/03 §8](../spec/03-reactivity-and-binding.md)。
- 旧 `:bind` 右侧不再接受 `Signal<T>` 对象；信号必须显式写 `.Value`（spec/01 §4.6）。
- 不得把 `P={expr}` 自动升级为双向；双向必须显式 `@bind-P`（D17）。

### 14.7.2 字符串、转义与空白

- 属性值 **token** 是双引号字符串 → 字面量；是未加引号的 `{`…`}` → 表达式。`P="{x}"` 是字符串 `{x}`，不是表达式；
  需要字面花括号时写表达式 `{"{x}"}`。
- 字符串字面量不解析 XML 实体（`&amp;` 原样保留）。03 冻结"不解析实体"；若未来支持，必须先在 03 登记语法变更。
- `Children` 模型下，纯空白文本忽略；含非空白字符的裸文本 → EUI006。
- 数字/布尔转换固定 `InvariantCulture`（现状，[Catalog.cs:80](../src/EclipseUI.Compiler/Catalog.cs#L80)）。

### 14.7.3 字面量转换器

- v1 早期已实现：`string`、`bool`、`double`、`int`。
- **C3 ✅（2026-11-03）扩展**：`float`（`R` 格式 + `f`，超 float 范围报 EUI003）、`long`（`L` 后缀）、
  `short`/`byte`（显式窄化 cast，范围检查）、`decimal`（`m` 后缀）、`char`（单字符或
  `\n`/`\r`/`\t`/`\\`/`\"`/`\'`/`\0`/`\uXXXX`，**不 Trim**）、枚举（成员名，大小写敏感）、
  `TimeSpan`（单单位 `250ms`/`1.5s`/`2m`/`1h`/`1d` 或 `[-][d.]hh:mm[:ss[.fffffff]]`，
  生成 `new global::System.TimeSpan({ticks}L)`；负值与 `\0` 转义已测）。测试 `C3LiteralAndDiagnosticsTests`。
- `Color`/`Thickness`/`CornerRadius` 在 G4 引入类型时同步提供转换器（Color：`#RGB`/`#ARGB`/`#RRGGBB`/`#AARRGGBB`；Thickness：1/2/4 个数字）。
- 枚举成员表由符号扫描写入 `UiTypeShape.EnumMembers`，分析器不引用 Roslyn 也能校验并给出候选。
- 转换器查找键固定为 `UiTypeShape.CanonicalKey`（内置类型 = C# 关键字名，其余 = 命名空间限定全名），
  **不得**用 `ClrTypeDisplayName`（自带 `global::`）当键（v2，与 14.5.1 对齐）。
- 无转换器的类型：字面量报 EUI023，提示用 `{expr}`。
- **第三方自定义转换器 v1 不支持**：编译期执行用户转换器代码违反 R1。自定义类型请用表达式构造。

### 14.7.4 表达式作用域与求值

| 位置 | 可用名字 | 重算归属 |
|---|---|---|
| 根 | code-behind 实例成员（`this`） | 组件 `Render`（`U{N}`） |
| `@if` 内容 | 同上 | 所在 `BlockScope.RenderActions`（块刷新） |
| `@if` 条件 | 同上 | `ConditionalBlock.SetCondition` + `Refresh()` |
| `@foreach` 集合 | 同上 | `ForEachBlock.SetItems` + `Refresh()` |
| `@foreach` item | item 变量 + 外层 item + 实例成员 | 条目 `BlockScope.RenderActions` |
| `Key` | item 变量 + 实例成员 | 每次枚举求值，不进渲染动作 |

- 表达式**原样输出**：没有 `.Value` 重写、没有可观察路径要求、没有依赖分析；
  `Signal<T>` 只是普通类型，读写须显式 `.Value`，写入后由作者 `Invalidate()`。
- item 模板可以引用组件级状态（`EUI014` 停用）；`Key` 可以引用任意可读表达式（`EUI039` 停用）；
  item `@bind` 源可以 rooted at 组件状态（`EUI042` 停用）。
- 表达式错误由 Roslyn 报在映射后的 `.ui` 行（`#line`），生成器不再重复报。
- 完整规则见 [spec/03 §4](../spec/03-reactivity-and-binding.md)；生成形态见 [spec/02 §4.15](../spec/02-generated-code-contract.md)。

### 14.7.5 保留属性

| 属性 | 语义 | 状态 |
|---|---|---|
| `Key={expr}` | `@foreach` 模板根的身份；Key + item 身份决定复用；可引用任意可读表达式 | 基础已实现 / S2（v4 复用） |
| `Ref={field}` | 实例赋给 code-behind 字段；仅简单标识符 | ✅ C5 |
| `Class="..."` | 样式类 | G4 |
| `Style="..."` | 内联样式 | G4 |

保留属性优先于控件同名属性；控件不得定义 `Key`/`Ref`/`Class`/`Style` 属性（元数据警告 EUI026）。

### 14.7.6 内容规则

- 见 14.4.4；控件与组件共用同一套 `UiContentModel`。
- 控制流块在 `Children` 模型下是合法子内容；在 `Text`/`TextRuns`/`None` 下报 EUI006。
- 组件的 `<Slot Name="...">` 只在组件标签内合法（G4，EUI022）。

## 14.8 生成代码契约（v3 历史）

> **已被取代**：v4 生成契约以 [spec/02](../spec/02-generated-code-contract.md) 为准。
> 本节与 §14.8.1–§14.8.4 的 `UiWatch`/`Subscribe`/`Signal<T>`/订阅形态是 v3 历史记录，
> 实现与评审 MUST NOT 依据本节生成代码。

### 14.8.1 构造形态

| 模板 | 生成代码 | 前置条件 |
|---|---|---|
| `<Button />` | `var el0 = new global::EclipseUI.Elements.Button();` | public 无参构造 |
| `Label="OK"` | `el0.Label = "OK";` | 可写属性 + 转换器 |
| `Gap={vm.Gap}` | `void U0() => el0.Gap = vm.Gap; U0(); Track(global::EclipseUI.UiWatch.Subscribe(this, path, U0));` | 可写属性 + 依赖可观察；否则只 `U0()` + `EUI036` |
| `@bind-Checked={vm.Show}` | `Track(global::EclipseUI.UiBinding.TwoWay(el0, ...CheckedProperty, readTarget: () => el0.Checked, writeTarget: v => el0.Checked = v, readSource: () => vm.Show, writeSource: v => vm.Show = v, subscribeSource: h => UiWatch.Subscribe(this, path, h)));` | 目标可写 + 源可写可观察 |
| `Checked:bind={show}` | `Track(global::EclipseUI.UiBinding.TwoWay(el0, global::EclipseUI.Elements.CheckBox.CheckedProperty, readTarget: () => el0.Checked, writeTarget: v => el0.Checked = v, readSource: () => show.Value, writeSource: v => show.Value = v, subscribeSource: h => show.Subscribe(h)));` | 旧语法，`EUI043`（S5 移除） |
| `OnClick={() => n++}` | `el0.AddHandler(global::EclipseUI.Elements.Button.ClickEvent, () => n++);` | 字段存在、签名兼容 |
| 子元素 | `el0.Children.Add(el1);` | `Children` 模型 + 目标有 `Add(Element)` |
| 文本（静态） | `el0.Label = "OK";` | `Text` 模型 |
| 文本（状态插值） | `void U1() { el0.Label = $"Count: {Count}"; } U1(); Track(((IUiObservable)this).Subscribe(nameof(Count), U1));` | `Text` 模型 + 状态属性 |
| 文本（信号插值） | `void U1() { el0.Label = $"Count: {count.Value}"; } U1(); Track(count.Subscribe(U1));` | `Text` 模型 + legacy 信号 |
| 文本（runs） | `var run0 = ((global::EclipseUI.ITextContentHost)el0).AddRun("Hi ");` | `TextRuns` 模型 |
| 组件 | `var c0 = new global::App.UserCard(); c0.Title = ...; Track(c0); c0.Mount(); el0.Children.Add(c0.Root);` | 参数规则 |
| 组件可观察参数 | `void U2() => c0.Title = vm.Name; U2(); Track(UiWatch.Subscribe(this, path, U2));` | 参数是 observable partial |

### 14.8.2 顺序与作用域

- 顺序：`new` → 按源码顺序设置属性/事件 → 构建子内容（深度优先）→ 加入父级。
- 文本插值与 `P={expr}` 的首次求值在 Build 内完成，随后订阅；首次赋值失败则 Build 失败，不建立订阅。
  `@if`/`@foreach` 的初始内容由协调器同步挂载。
- 订阅作用域：根 `Track`、`@if` 内容/条件订阅表、`@foreach` 集合依赖/item scope；`@bind-P` 的 `UiBinding`
  登记在当前作用域，随作用域释放（R8）。组件实例也加入同一作用域。
- 生成代码 MUST 使用 `global::` 全限定名，不依赖 using；MUST 不生成可变静态状态
  （`@foreach` 的 `List<>` 例外已在 C2 修复，03 D22）；MUST 通过公开 setter 写入（R3/R7）。

### 14.8.3 引擎对控件的保证

1. 每个模板节点构造一次（`@foreach` 同 Key 同 item 复用条目，只重跑 updater；同 Key 不同 item 重建条目）。
2. 属性/事件在元素加入父级之前完成；构造期父元素为 null。
3. 属性按 `.ui` 源码顺序赋值；控件不应依赖顺序，但可依赖"构造后、挂载前"。
4. 构建阶段框架不触发控件事件、不调用 `Measure/Arrange`。
5. `Measure/Arrange` 之后可能多次发生，控件布局必须无副作用、可重复。
6. 失效由属性引擎负责；模板引擎不额外调用 `InvalidateMeasure`。
7. 组件 `Dispose` 时机 = 作用域释放；元素无 Dispose（v1）。
8. 全程 UI 线程。
9. 反应式 updater 可能在 Build 之后多次运行；属性 setter 必须幂等，不得依赖"只调用一次"。

### 14.8.4 可确定性与等价性

- 同一编译输入（目录 + 模板 + code-behind 签名）生成相同文本；变量/函数编号按遍历顺序固定。
- 生成代码只用公开 API，等价于手写 C#（R4）；逃生舱不需要另一套运行时。

## 14.9 诊断契约

### 14.9.1 诊断表

现有代码保持不变；EUI016 起的码表分片实现（扫描侧元数据诊断 C1/C2；使用点 EUI023/EUI025/EUI031 C3；
其余按 C4–C6），新增码从 EUI046 起（不重用停用编号）。表中列与控件契约相关的诊断；v4 的反应式诊断口径以
[spec/03 §11.1](../spec/03-reactivity-and-binding.md) 与 [spec/01 §6.2](../spec/01-template-syntax.md) 为准。
EUI005/007/008/009/011/012/013/015 分别是 code-behind、`@foreach` 类型推断与指令诊断，不受本文影响。
v3 的 EUI014/EUI032/EUI034–EUI036/EUI038–EUI039/EUI041–EUI042/EUI044 已停用，编号不复用。

| 代码 | 条件 | 位置 | 级别 |
|---|---|---|---|
| EUI001 | 未知标签 | 标签 | Error |
| EUI002 | 未知属性/事件/参数；若同名 CLR 属性存在但未注册 `StyledProperty`，提示补注册 | 属性名 | Error |
| EUI003 | 字面量格式错（含枚举成员不存在） | 属性值 | Error |
| EUI004 | 事件缺 `{expression}` / 事件用 `:bind` 或 `@bind-` | 属性 | Error |
| EUI006 | 内容模型不允许（文本/元素/控制流） | 子内容 | Error |
| EUI010 | `Key` 位置或形式错 | 属性 | Error |
| EUI014 | **停用**：v3 item 模板禁止组件级状态；v4 允许条目闭包读取 `this` | — | — |
| EUI016 | 标签冲突（≥2 类型） | 标签 | Error |
| EUI017 | 标签类型不可暴露（public 但抽象/嵌套） | 标签 | Error |
| EUI018 | 缺少 public 无参构造 | 标签 | Error |
| EUI019 | 属性不可写 / `{Name}Property` 类型不符（**仅对已进入目录的属性成立**：源项目里缺 `{Name}Property` 的属性根本不进目录，用户撞到的是 EUI002；EUI019 只在引用程序集带问题时目录里才触发，见 14.5.4。✅ C5 使用点：`def.Problems` 在用点暴露，消息带完整类型名；`UiElementDef.IsFromReferencedAssembly` 区分来源） | 属性名 | Error |
| EUI020 | 事件字段缺失或类型不是 `RoutedEvent`（✅ C5 使用点：`On{x}` ↔ `{x}Event` 字段名匹配，仅引用程序集） | 属性名 | Error |
| EUI021 | 内容目标缺失或类型不匹配（✅ C5 使用点：引用程序集控件内容被降级为 None 时，在使用点接管 children 诊断） | 子内容 | Error |
| EUI022 | 插槽非法（✅ C6）：消费侧 `<Slot>` 未知插槽（含建议）/重复填充/位置非法（只允许组件标签直属子级）/缺 `Name` /非字面量 `Name` /多余属性/单根槽多根填充；宿主侧 `<ContentSlot>` 未知/重复放置/位置（元素容器内才合法；块、根非法）/缺 `Name` /带内容/多余属性（错误明细见 spec/01 §6.2） | 标签/属性 | Error |
| EUI023 | 属性类型没有字面量转换器 | 属性值 | Error |
| EUI024 | 事件处理器签名不匹配（可静态判定时；✅ 2026-11-08/D82 已落地，见 §14.11.2 与 spec/02 §12 D82） | 属性值 | Error |
| EUI025 | 同一标签重复属性 | 属性名 | Error |
| EUI026 | 控件元数据声明非法（源项目） | 声明处 | Error/Warning |
| EUI027 | 组件必填参数缺失 | 标签 | Error |
| EUI028 | 组件参数/委托声明非法：非 public / 无 setter / `init` / 使用 C# `required`（应改 `[UiParameter(Required = true)]`）/ 非法泛型实参 / 委托含 `ref`/`out`/`in`；✅ C6 起含 `[UiSlot]` 声明项：重复插槽名、`[UiParameter]+[UiSlot]` 同属性（参数胜出）、`[UiIgnore]+[UiSlot]`（忽略胜出，D71） | 声明处/属性名 | Error |
| EUI029 | `Ref` 目标非法（✅ C5：语法级=非简单标识符，分析器报；语义级=字段缺失/只读/静态/类型不符，生成器探针报并引用 C# 诊断；生成形态 spec/01 §4.3 / spec/02 §4.14） | 属性值 | Error |
| EUI030 | 泛型标签暂不支持 | 标签 | Error |
| EUI031 | 语法已保留、尚未实现（`Class`/`Style`/附加属性）；✅ C6 起还覆盖**保留标签名冲突**——控件/组件把 `Slot`/`ContentSlot` 注册为标签名（解析器特判这两种写法为槽节点，`<Slot>`/`<ContentSlot>` 使用点错误由 EUI022 接管） | 标签/属性 | Error |
| EUI032 | **停用**：原"非 `Signal<T>` 组件参数一次性赋值"；后继 EUI044 亦停用 | — | — |
| EUI033 | 同一目标重复赋值（文本内容 + 显式目标属性；`P=` 与 `@bind`/`:bind`/`:value` 冲突） | 属性值/文本 | Error |
| EUI034 | **停用**：v3 非法 `[UiState]`/`[UiObservable]` 声明；状态生成器移除 | — | — |
| EUI035 | **停用**：v3 非法 `[UiComputed]`；`[UiComputed]` 移除 | — | — |
| EUI036 | **停用**：v3 "反应式位置读取不可观察成员 → 一次性"；v4 无依赖分析 | — | — |
| EUI037 | `@bind` 源非法（不可写/方法/索引器/`?.`/静态/只读/类型不符/自绑） | 表达式 | Error |
| EUI038 | **停用**：v3 "不可分析结构 → 一次性"；v4 任意表达式合法 | — | — |
| EUI039 | **停用**：v3 `Key` 不得依赖可观察状态；v4 Key 可引用任意可读表达式 | — | — |
| EUI040 | `@bind` 目标非法（不可写/无 `{Name}Property`/事件/内容/保留属性/组件缺少 `{P}Changed`/类型不符） | 属性名 | Error |
| EUI041 | **停用**：v3 集合未实现 INCC；v4 每次刷新重新枚举 | — | — |
| EUI042 | **停用**：v3 item `@bind` 禁止组件级源；v4 允许 | — | — |
| EUI043 | 旧语法 `:bind`/`:value` 弃用 | 属性名 | Warning |
| EUI044 | **停用**：v3 非可观察组件参数不刷新；v4 所有参数推送 + `Invalidate()` | — | — |
| EUI045 | code-behind 与生成代码冲突（`Build`/`Render`/`U{N}`/`__` 字段/基类/隐藏 `Invalidate`） | code-behind 声明 | Error |

**EUI045 已落地（2026-11-08，D57/D80）**：`TemplateGenerator.ProbeCodeBehindConflicts` 覆盖
spec/02 §10 五类触发，诊断用纯数据 `UiSourceLocation` 定位到 code-behind 源声明，
生成器不改名也不跳过输出；`CodeBehindConflictTests` 23 例。口径正本在
[spec/02 §10](../spec/02-generated-code-contract.md#10-eui045-与-code-behind-边界)，
台账见 [spec/02 §12 D80](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d88)。
**D74 ② 已落地（2026-11-08，D81）**：`UiMetadataProblem` 携带纯数据 `UiSourceLocation`
（成员符号声明优先、类型声明兜底；引用程序集无源位置 → null），生成器注册目录级
`RegisterSourceOutput(catalogProvider)` 把源项目 problem 报在 C# 声明处，成员级取
`def.Problems`、类型级取 `catalog.Problems`，与逐文件管道解耦、每条只报一次；
源项目控件的 EUI019/EUI020/EUI021 problem 按 §14.9.2 折叠为 EUI026，EUI026/EUI028/EUI031 保持原码。
标签使用点按类别分流：冲突 → EUI016，不可用按 `HiddenTag.ReasonCode` → EUI017/EUI018/EUI030/EUI031，
只有真正未知才落 EUI001。`MetadataOutletTests` 14 例；口径正本见
[spec/02 §12 D81](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d88)。
EUI024 事件处理器签名匹配已落地（✅ 2026-11-08/D82）：`TemplateGenerator.ValidateEventHandlerSignature` +
`TemplateProbe.IsHandlerCompatible` 复用 `ProbeTree` 合成 partial 方法体，借 Roslyn 把处理器表达式放进
code-behind 符号上下文，仅当「表达式自身语义干净 + 对目标委托的转换确定失败」时在 `.ui` 属性值报
Error（短路生成输出，与 EUI004 一致）；符号未解析/重载失败/`dynamic`/表达式内部错误静默，交回生成代码
经 `#line` 由 C# 报错（spec/02 §2.4 规则 8 不重复报告）。控件事件复用 `Emitter.EventHandlerType`
（`internal static`）、组件委托参数用 `UiParameterDef.ClrTypeDisplayName`，保证诊断与生成代码类型判定一致。
`EventHandlerSignatureTests` 16 例；口径正本见 [spec/02 §12 D82](../spec/02-generated-code-contract.md#12-实现偏差台账-d42d88)。

### 14.9.2 责任划分

- **模板侧错误**（用错）：分析器报，span 精确到标签/属性/值。
- **元数据侧错误**（控件声明错）：源项目在声明处报 EUI026；引用程序集只能在用点上暴露（EUI019/EUI020/EUI021）。
- 建议算法只做提示，不改变解析结果；候选超过 3 个时只列前 3。
- 所有诊断消息为英文（与现有代码一致）；文档/编辑器可本地化，但代码/消息保持稳定。

## 14.10 目录构建、缓存与项目结构

### 14.10.1 项目结构

`UiCatalogFactory` 依赖 Roslyn，不能放进零依赖的 `EclipseUI.Compiler`。采用与前端相同的"源码链接"模式：

```
src/EclipseUI.Compiler/            零 Roslyn 依赖：AST、Parser、Analyzer、Catalog POCO
src/EclipseUI.Compiler.Metadata/   依赖 Microsoft.CodeAnalysis：符号扫描、目录工厂
  └─ 源码链接进 EclipseUI.Compiler.Generator（同 Compiler 前端的做法）
  └─ G5 的 LSP 直接引用本项目
```

✅ **C2 已落地**：`Metadata` 源码链接进生成器程序集（`Compile Include`，排除
`EclipseUI.Compiler.Metadata/Polyfills.cs`——`EclipseUI.Compiler/Polyfills.cs` 已提供
`internal IsExternalInit`）；`EclipseUI.Compiler.Metadata` 仍是独立项目（`ProjectReference` 指向
`EclipseUI.Compiler`，只为共用 §14.9.1 诊断码常量，`PrivateAssets=all` 不外流）。LSP（G5）可直接引用本项目。
并入动作与 §14.10.2 的伪码差异都记在 §14.12.1 / §14.12.2 / 台账 D65。

### 14.10.2 扫描算法

```
Build(Compilation c):                       // 纯函数：同样的 Compilation → 同样可结构比较的目录值
  exposed = []; hidden = []; problems = []
  for asm in [c.Assembly] + c.SourceModule.ReferencedAssemblySymbols:
      if asm != c.Assembly && !ReferencesEclipseUiRuntime(asm): continue   // 跳过 System.* 等
      for type in WalkNamespace(asm.GlobalNamespace):
          if IsStaticEventHostAbuse(type):                 // public 静态类上有 RoutedEvent 字段
              problems.Add(EUI026(...)); continue          // 14.4.3 v2：事件必须声明在元素/组件上
          if !DerivesFromElementOrComponent(type): continue
          if !IsPublic(type): continue                     // 不可见 → EUI001
          if !CanExpose(type, out reason):                 // 抽象/嵌套/无参构造/泛型
              hidden.Add(HiddenTag(type, reason)); continue
          if TagOf(type) in {"Slot", "ContentSlot"}:       // ✅ C6：句法保留名（解析器特判成槽节点）
              hidden.Add(HiddenTag(tag, EUI031)); problems.Add(EUI031(...)); continue
          exposed.Add(Describe(type))                      // 标签/属性/事件/内容/参数/插槽
  groups = GroupByTag(exposed)                             // 同 Tag ≥2 → Ambiguous（EUI016）
  return UiCatalog(groups, hidden, problems)               // 记录类型 + 稳定排序 → 可结构比较
```

不再有 `cache.TryGet(asm.Identity)`（v2 修订，D14）：增量生成器**不持有可变静态状态**，
手工缓存与 14.8.2 的约束、R6 的确定性以及管道自带的值比较缓存互相冲突。缓存交给管道——
`UiCatalog` 由 `IncrementalValuesProvider` 产出，配合 record 结构相等 + 稳定排序，
**目录每轮重建但下游生成输出不重发**（实测见 14.10.3）。

`Describe` 要点：

- 程序集过滤：只扫源程序集与**直接引用 `EclipseUI.Runtime`** 的程序集（第三方控件必须直接引用运行时才能继承 `Element`/`Component`），避免扫描 `System.*` 等无关类型。
- 基链判定用 `EclipseUI.Element` / `EclipseUI.Component` 的 metadata name，逐级 `BaseType` 比较。
  **跨程序集可行（实测）**：读 CounterApp.dll 元数据时 `Counter`/`Prefs`/`TaskList` 的 `BaseType` 解析为
  `EclipseUI.Component`，且 public 无参构造可见，所以 14.6.5 的第三方组件发现不依赖源码。
- 属性：遍历类型 + 基类的 `IPropertySymbol`；找 `PProperty` 字段；校验类型一致与可写性
  （`IPropertySymbol.IsRequired`、`SetMethod.IsInitOnly` 判定 `required`/`init`，Roslyn 4.9.2 实测可用）。
- 事件：遍历类型 + 基类的 `IFieldSymbol`；类型为 `EclipseUI.RoutedEvent`、名字以 `Event` 结尾。
  基类字段一并继承（迁移后的 `Element.PointerPressedEvent` 会出现在每个标签上）。
- 内容：沿基链找最近的 `UiContentAttribute`；参数属性上的特性优先。
  注意 `GetAttributes()` **不返回继承来的特性**，基链必须自己走（`Panel` 上的 `[UiContent(Children)]` 要传到 `StackPanel`）。
- 参数/插槽：只扫组件；`[UiParameter]` / `[UiSlot]` 按属性读取；参数只记录名称、类型、可写性、必填与委托形状，
  不记录可观察性（v4 无该概念）；`@bind` 目标另查同名 `{P}Changed` 参数，记入 `ChangedCallbackName`。
  插槽（✅ C6）：`[UiSlot("名")]` → `UiSlotDef(名字, 属性名, 类型显示名, 基数)`；类型 `Element?` → Single、
  `IReadOnlyList<Element>` → Multiple、其他 → EUI021 问题（槽不进表面）；重复名 → EUI028；
  `[UiParameter]+[UiSlot]` 同属性 → 参数胜出（EUI028）；`[UiIgnore]` 胜出（D71）。
  同名成员按基链派生优先，但**只有派生侧显式写了模板特性或 `[UiIgnore]` 才算覆盖**：
  Roslyn 的 `GetAttributes()` 不把特性带进 override，若一律按名字去重，
  `public override Element? Header` 会把基类 `[UiSlot("Header")]` 静默丢掉（✅ C6，偏差 D76）。
  code-behind 类不是 Component 派生，因此**不在目录里**——生成器经 `ResolveOwnComponent`
  按符号沿基类链单独读 `[UiSlot]`（14.12.6 / spec/02 §4.12.5，偏差 D75）。
- 类型形状：枚举成员、委托、`Signal<T>` 类型名识别；用于字面量与表达式规则；`Signal<T>` 不改变模板语义。
- 类型名：`ClrTypeDisplayName` 取 `FullyQualifiedFormat`；`CanonicalKey` 单独归一（14.5.1）。

### 14.10.3 缓存与增量（v2 按实测重写）

**实测**（Microsoft.CodeAnalysis.CSharp 4.9.2，本机 .NET 10，对象 = GalleryApp/CounterApp 真实输出目录的引用集）：

| 量 | 值 |
|---|---|
| 引用程序集数 | 14 |
| 全量无过滤扫描类型数 | 688 |
| 全量无过滤扫描（热态） | **45.1 ms** |
| `直接引用 EclipseUI.Runtime` 过滤判定 | 5.6 ms |
| 过滤后需扫描的程序集 | 7 个，热态单个 0.2–11.2 ms |

结论：本仓库规模下目录构建 ~10–20 ms，**远低于 14.10.2 原设想的 100 ms 预算**，
所以不预先做任何手工缓存。增量形态固定为：

- 目录由 `IncrementalValuesProvider<UiCatalog>` 产出，`UiCatalog` 及其成员全是 record + 稳定排序，
  用结构相等比较；**缓存交给管道**，生成器实例内不保存任何可变状态。
- 目录依赖 `Compilation`，项目里任何改动都会让它**重建**（这是事实，别再承诺"不重建"）；
  目标是重建后**下游生成输出不变**，即 `CSharpGeneratorDriver` 的 `GetRunResults()` 里
  每个文件的 `IncrementalStepRunReason` 为 `Cached`/`Unchanged`。14.11.2 的增量测试按这个口径断言。
- 目录构建仍是**每批编译一次**，不是每个 `.ui` 一次（✅ G3 2026-11-08：管道根改为
  `AdditionalTexts → UiFileInput` 与 `Compilation → UiCatalog` 两条；逐文件 `Combine` 后
  `Select` 出 `(Input, Catalog, UiFileFacts)` 再逐文件 `RegisterSourceOutput`。
  `UiFileFacts`（code-behind 表面 + 探针事实）是值相等 record，与目录同口径每轮重建、
  下游按值命中缓存；单个 `.ui` 变化只重跑该文件的发射。
  验收：`Incremental_UnrelatedSourceChange_KeepsPerFileOutputsCached`（无关源码改动，
  两个文件均 `Cached`/`Unchanged`）、`Incremental_SingleUiChange_RerunsOnlyThatFile`
  （只改 A.ui，A `Modified`/`New`、B `Cached`/`Unchanged`）。
  更正旧测试注释：Roslyn 4.9.2 的 `TrackedSteps[...].Outputs` 元素是
  `(object Value, IncrementalStepRunReason Reason)` 元组，reason **可以**公开断言，
  此前「公开 API 没有 step reason」的说法是误读。facts 与目录一样每轮重建，
  不做没有数字支撑的预优化。）
- 若引用集增长到 100+ 程序集或过滤后仍 > 100 ms，再按实测数据决定是否加 `WithUpdateReason` 级别的拆分；
  不做没有数字支撑的预优化。

### 14.10.4 版本与兼容

- 0.x 期间生成器与运行时同版本发布；第三方控件包声明对 `EclipseUI.Runtime` 的同线依赖。
- 元数据形状变更（新增字段/枚举值）保持向后兼容：旧控件包 + 新生成器 = 按默认规则工作；
  新控件包 + 旧生成器不在支持范围（包依赖约束）。
- `UiContentKind` 数值与 `UiContentModel` 数值绑定；测试断言（14.11）。

### 14.10.5 LSP 消费

- 补全：标签来自 `UiCatalog.Elements`；属性/事件/参数按 `UiElementDef` 过滤；不可用标签（`HiddenTags`）
  在补全中隐藏，冲突标签标注歧义。
- 悬停：只显示**符号可派生**的信息——类型全名、声明程序集、是否可写、内容模型、可用字面量形态、枚举成员候选。
  **`Invalidate` 级别与默认值不在编译期目录里（v2 修订，D13）**：它们来自 `Register(...)` 的构造实参与
  构造函数赋值（`ListBoxItem` 在构造里设 `Padding = 8`，[Panels.cs:89](../src/EclipseUI.Runtime/Panels.cs#L89)），
  R1 禁止编译期执行；如果将来要显示，只能由 LSP 另走运行期查询并在 UI 上标注"运行期值"，不得进 `UiCatalog`。
- 签名帮助：`On{E}` 展开 `Action` / `Action<RoutedEventArgs>`；`@bind-P` 提示目标是否可绑（控件属性需 `{Name}Property`，
  组件参数需 public setter，组件 `@bind` 另需 `{P}Changed`），`:bind`/`:value` 标注弃用。
- 诊断：与生成器同一套代码、消息、span；LSP **不得**另建硬编码目录。

## 14.11 一致性与测试矩阵

### 14.11.1 运行时一致性

- 测试断言：从符号派生的内置目录中，每个属性都能在 `StyledPropertyRegistry` 找到
  （owner/name/type 匹配），每个事件字段存在且类型正确。
  ✅ **C1 实测通过**（`CatalogScannerTests.EveryDerivedProperty_ExistsInTheRegistryWithMatchingOwner` /
  `EveryDerivedEvent_FieldExistsWithRoutedEventType`：17 个属性项、1 个事件字段全部命中注册表/字段）。
- **失效级别守卫（v2 新增）**：模板可见属性的 `Invalidate` 必须非 `None`，例外走显式白名单常量
  （当前白名单：`Element.IsHitTestVisible` —— 只影响命中测试，不影响重排重绘）。
  这条是 14.4.2「失效契约」唯一的可执行检查，因为编译期拿不到该值（D13）。
  ✅ **C1 实测通过**：17 个属性项里 `Invalidate == None` 的只有 5 个 `*.IsHitTestVisible`（同一注册项在不同标签上重复出现），
  白名单外例外 0 个——`ClipToBounds` 已按 §14.12 的待办把级别改到 `Render`（D67）。
- **v4 无状态生成器**：模板状态是普通 CLR 成员，测试不再断言 `[UiState]` 订阅分支；
  改为断言生成代码只包含 spec/02 §2.3 白名单成员，且不含已停用/禁止 token（spec/02 §9 T15）。
- 迁移完成后删除 `UiCatalog.CreateDefault()`。✅ **C2 已删除**（`Catalog.cs` 只剩 `UiLiterals`/`Suggest`）。
  **测试目录不得再手写一份**（那会制造 T1 禁止的第二事实源）：
  `UiTestCatalogs.BuiltIn()` 的做法是用真实扫描器扫已编译的 `EclipseUI.Runtime.dll`（测试项目 net10.0，可引用 Roslyn），
  结果与仓库内的 golden 快照文件比对（`TestData/BuiltInSurface.golden.txt`，`ECLIPSEUI_UPDATE_SURFACE_GOLDEN=1`
  重新生成——表面变宽必须走显式评审，见 diff 即知）。
  ✅ **C2 修订**：golden 是**扫描结果的渲染快照**，不是第二事实源，也没有反序列化路径——
  全扫实测仅 45 ms（14.10.3），纯结构测试各自现扫 `Compilation`，比从快照反序列化更少一道漂移源。→ D72 已收敛。
- 内置控件表面快照（迁移后预期；`In` = 继承自 `Panel`/`Element`）：
  - `TextBlock`：`FontSize`、`Padding`(In)、`ClipToBounds`(In)、`IsHitTestVisible`(In)；内容 `TextRuns`。
  - `StackPanel`/`ListBoxItem`：`Gap`、`Padding`(In)、`ClipToBounds`(In)、`IsHitTestVisible`(In)；内容 `Children`。
  - `Button`：`Label`、`IsHitTestVisible`(In)、事件 `OnClick` + 8 个继承事件；内容 `Text`。
  - `CheckBox`：`Label`、`Checked`、`IsHitTestVisible`(In) + 继承事件；内容 `Text`。
  - 合计：5 标签 / 17 属性项 / 8 个事件（对照现状 5 / 6 / 1，见 14.1 与 14.12）。
  ✅ **C1 实测**：5 标签 / **17 属性项** 与上面的逐项列表逐字吻合（`BuiltInSurface_MatchesGoldenSnapshot`）；
  逐项实测表与差集评审见 §14.12.1。
  ✅ **C2**：8 个事件全员可见（+`CheckBox.OnCheckedChanged`）；实测 9 个事件名 / 37 事件项，见 §14.12.2。

### 14.11.2 测试矩阵

| 类别 | 用例 | 期望 |
|---|---|---|
| 发现 | 内置控件快照；标签=类型短名；`[UiIgnore]`；internal/抽象/嵌套/泛型 | 入目录/不入目录符合规则 |
| 属性 | 继承的 `Padding`；普通 CLR 属性；只读属性；类型不符的 `PProperty` | 正确暴露/忽略/元数据问题 |
| 事件 | `ClickEvent`；字段缺失；字段类型错；**静态宿主类上的 `RoutedEvent` 字段**；`InputEvents` 迁入 `Element` 后 8 事件全员可见；`:bind` | `OnClick` 可用/EUI020/EUI026/EUI004 |
| 事件签名（EUI024） | ✅ 2026-11-08/D82 契约：可静态判定（表达式自身语义干净 + 对目标委托转换确定失败）→ EUI024 报在 `.ui` 属性值并短路生成输出；符号未解析/重载失败/`dynamic`/表达式内部错误静默交回生成代码经 `#line` 由 C# 报错（不重复报告）；控件事件复用 `Emitter.EventHandlerType`、组件委托参数用 `UiParameterDef.ClrTypeDisplayName`；`EventHandlerSignatureTests` 16 例 | EUI024/零误报/C# 兜底分工 |
| 内容 | Text/Label、TextRuns、Children、None；空白文本；控制流 | 生成/EUI006 |
| 字面量 | double/int/bool/string/enum/TimeSpan；格式错；无转换器；**`CanonicalKey` 与 `global::` 显示名不串键** | 转换/EUI003/EUI023 |
| 组件 | 参数、`[UiParameter(Required=true)]` 缺参、委托参数、`{P}Changed`、C# `required`/`init` 拒绝、默认内容/单根、✅ C6 插槽：`<Slot>` 填充解析/重复/多根拼接、`<ContentSlot>` 宿主解析/重复放置/块内非法、各自未知名带建议 | 生成/EUI027/EUI028/EUI040/EUI021/EUI022 |
| 反应式 | `P={expr}` 根级/块内、`@if`/`@foreach` 刷新、Keyed 复用（Key + item 身份）、`@bind-P` 源/目标（EUI037/EUI040）、控件事件自动失效 | 失效重算正确/诊断正确/停用码零出现 |
| 冲突 | 两个程序集同标签；暴露属性名占用 `On{事件}` / 保留名 | EUI016 + 候选名 / EUI026 |
| 保留语法 | `Class=`、`Style=`、附加属性、把 `Slot`/`ContentSlot` 注册为控件/组件标签名（✅ C6 保留冲突进 HiddenTags）；`:bind`/`:value` | EUI031（不是"未知"）；EUI043 Warning |
| 生成代码 | 每类构造的 golden 文本；`Track`/`BlockScope` 作用域登记 | 确定性一致 |
| 集成 | `CSharpGeneratorDriver` 编译含 `.ui` 的测试项目；模拟第三方程序集（含跨程序集组件发现） | 无诊断 + 生成代码可编译 |
| 增量 | 无关文件修改后跑 `CSharpGeneratorDriver`，读 `GetRunResults()` | ✅ G3 2026-11-08：目录重建但**每个生成文件 `IncrementalStepRunReason` 为 `Cached`/`Unchanged`**；单 `.ui` 变化只重跑该文件（v2 口径，见 14.10.3；`GeneratorContractTests` 3 例） |

测试项目：新增 `tests/EclipseUI.Compiler.Tests`（net10.0，引用 Microsoft.CodeAnalysis.CSharp、生成器、运行时）。
现有 `EclipseUI.Headless.Tests` 继续负责前端/分析器的纯结构测试。

## 14.12 迁移计划

| 阶段 | 内容 | 出口 | 主要文件 |
|---|---|---|---|
| **C1** ✅ | 特性、目录模型、符号扫描器、一致性/快照测试；不改分析器与生成器行为 | 派生目录 **⊇** 手写目录，差集（属性 6→17）逐条评审并写进变更记录；`dotnet test` 全绿 | `Runtime/UiAttributes.cs`、`Compiler.Metadata/*`、`Catalog.cs` |
| **C2** ✅（2026-11-02） | 生成器切到派生目录；内置控件加 `[UiContent]`；基础设施元素加 `[UiIgnore]`；**输入事件字段从 `InputEvents` 迁入 `Element`（19 处调用改名，删静态类）**；`ClipToBounds` 失效级别改 `Render`；删除手写目录 | Gallery/Counter 渲染与交互不变；`Padding` 等属性、8 个事件变为模板可用（更新文档）—— ✅ 全部落地（落地记录见 §14.12.2） | `Input.cs`、`Element.cs`、`InputRouter.cs`、`FocusManager.cs`、`Controls.cs`、`TemplateGenerator.cs`、`Panels.cs`、`TextBlock.cs`、`Blocks.cs` |
| **C3** ✅（2026-11-03） | 转换器扩展（float/decimal/long/short/byte/char/枚举/TimeSpan；Color 预留 G4）；使用点诊断 **EUI023**（无转换器）/ **EUI025**（纯普值重复属性）/ **EUI031**（Class/Style/点号附加属性/`<Slot>`/`<ContentSlot>`）落地；属性暴露一致性测试 | 诊断表前半可用；无转换器类型正确报 EUI023 —— ✅ 全部落地（落地记录见 §14.12.3） | `Catalog.cs`、`Analyzer.cs`、`Metadata/UiCatalogFactory.cs` |
| **C4** ✅（2026-11-05） | 组件标签：参数元数据（`[UiParameter(Required=true)]`、`{P}Changed`）、委托事件、默认内容、生命周期与作用域释放；生成器集成测试 | 第三方/自定义组件可写入 `.ui`；参数/内容生成与 spec/02 §4.12 一致 —— ✅ 全部落地（落地记录见 §14.12.4） | `TemplateGenerator.cs`、`Emitter.cs`、`Component.cs`、`Metadata/UiCatalogFactory.cs`、`Analyzer.cs` |
| **C5** ✅ | `Ref`（G3）；保留语法诊断与剩余分析器收敛（**EUI019/EUI020/EUI021** 使用点与 **EUI029**；EUI025/EUI031 已由 C3 落地，EUI033 为 S2 既有） | G3 非反应式缺口收敛；诊断不再落"未知" | `Analyzer.cs`、`Emitter.cs` |
| **C6** | 具名插槽 + `ContentSlot` 宿主（✅ 2026-11-05，§14.12.6）；附加属性实拆留 G4，泛型标签暂缓（K19） | 与 03 语法表对齐 | 编译器/运行时/样式 |

状态/反应式不在 C1–C6 内重复排期：失效队列/`U{N}`/根级重算属
[spec/03 §13](../spec/03-reactivity-and-binding.md) 的 **S1**；块 v4/键控复用属 **S2**；
`@bind-P`/`UiBinding`/`PChanged` 属 **S3**；所有权/参数推送/线程/`ShouldRender` 属 **S4**；基准/AOT/清理属 **S5**。
C4 只负责组件标签与参数/内容元数据，C5 只保留 `Ref` 与语法收敛。

第一批（C1–C2）不改**语法**，只把元数据来源换成符号；但模板**表面**一定变宽，差异必须写进变更记录：

- ✅ C2 模板可用属性项 6 → 17：新增 `Padding`、`ClipToBounds`、`IsHitTestVisible`（以及未来所有 StyledProperty）。
- ✅ C2 模板可用事件 1 → 8：`InputEvents` 迁入 `Element` 后，`OnPointerPressed/Released/Moved`、
  `OnKeyDown/Up`、`OnGotFocus/LostFocus` 在所有标签上可用（14.4.3 v2）。
- ✅ C2 补齐：`CheckBox` 补上 `CheckedChanged` 事件（`@bind-Checked`/`Checked:bind` 的对端可感知用户勾选）。
- ✅ C1 暴露前修正 `Panel.ClipToBoundsProperty` 的失效级别：`None` → `Render`（裁剪影响像素，运行期变更必须重绘）。
- ✅ C2 `TextRun`、`ConditionalBlock`、`ForEachBlock<>` 不作为标签；`InputEvents` 静态类删除（否则按新规则报 EUI026）。
- ✅ C2 未知标签建议的候选集从 5 个扩展到全部可见控件/组件。

### 14.12.1 C1 落地记录（2026-11-02）

扫描真实 `EclipseUI.Runtime.dll` 得到的**派生目录实际表面**（`CatalogScannerTests.BuiltInSurface_MatchesGoldenSnapshot` 逐标签钉死，
属性/事件名按 Ordinal 排序）：

| 标签 | 内容模型:目标 | 属性项 | 事件项 |
|---|---|---|---|
| `Button` | `Text:Label` | `IsHitTestVisible`、`Label` | `OnClick` |
| `CheckBox` | `Text:Label` | `Checked`、`IsHitTestVisible`、`Label` | — |
| `WrapPanel` | `Children:Children` | `ClipToBounds`、`Gap`、`IsHitTestVisible`、`Orientation`、`Padding` | — |
| `ListBoxItem` | `Children:Children` | `ClipToBounds`、`Gap`、`IsHitTestVisible`、`Padding` | — |
| `StackPanel` | `Children:Children` | `ClipToBounds`、`Gap`、`IsHitTestVisible`、`Padding` | — |
| `TextBlock` | `TextRuns:—` | `ClipToBounds`、`FontSize`、`IsHitTestVisible`、`Padding` | — |
| **合计** | 6 标签 | **22 属性项** | **1 事件项** |

隐藏标签 4 个（`Component`/`Element`/`Panel`/`StackPanelBase`，均 EUI017「类型是抽象的」）；
`TextRun`/`ConditionalBlock`/`ForEachBlock<T>` 走 `[UiIgnore]` 完全退出表面，不进 `HiddenTags`。
静态宿主类按**类型**报一条 EUI026（不是每个元素一条）：实测内置目录只有 1 条，指向 `global::EclipseUI.InputEvents`
（`StaticEventHost_IsReportedAsMetadataProblemNotATag`），其 7 个 `RoutedEvent` 字段全部不入事件表面 → D69。

**差集（手写 6 → 派生 17）逐条评审**，全部是继承来的 `StyledProperty`，无一是新增控件 API，
因此判定为「opt-out 暴露策略的必然结果」（14.13 第一条风险），接受：

| # | 新增属性项 | 来源 |
|---|---|---|
| 1–5 | `Button.IsHitTestVisible`、`CheckBox.IsHitTestVisible`、`ListBoxItem.IsHitTestVisible`、`StackPanel.IsHitTestVisible`、`TextBlock.IsHitTestVisible` | `Element.IsHitTestVisibleProperty` |
| 6–8 | `ListBoxItem.Padding`、`StackPanel.Padding`、`TextBlock.Padding` | `Panel.PaddingProperty` |
| 9–11 | `ListBoxItem.ClipToBounds`、`StackPanel.ClipToBounds`、`TextBlock.ClipToBounds` | `Panel.ClipToBoundsProperty` |
| 12 | `ListBoxItem.Gap` | `StackPanelBase.GapProperty`（手写目录只给 `StackPanel` 标了 `Gap`） |

这 12 项在 C1 时**尚未对模板生效**：C1 不改分析器/生成器；生效点在 C2（D64）—— ✅ **C2 已生效**（§14.12.2）。
事件表面当时同样是 1 项（D69），8 项随 `InputEvents` 迁入 `Element` —— ✅ **C2 已生效**。

**提前到 C1 落地的三项**（原表把它们排在 C2），理由与代价逐条记：

1. **`[UiContent]`/`[UiIgnore]` 等运行时标注**（原 C2）：派生目录要 ⊇ 手写目录，必须先看运行时怎么声明内容模型；
   没有标注，`Button`/`TextBlock` 的内容就只能靠约定猜测，C1 出口无法验证。标注是纯元数据，不改任何生成行为。→ 台账 D63
2. **`Panel.ClipToBounds` 失效级别 `None`→`Render`**（原 C2）：14.11.1 的失效级别守卫要求白名单只有
   `Element.IsHitTestVisible` 一项；不改这一行就得在守卫里塞第二个例外，等于放宽契约。运行期无任何测试依赖旧级别。→ D67
3. **`[UiIgnore]` + `[UiParameter]`/`[UiSlot]` 同时出现按 EUI028 报出**：14.6.1 只写「不得」，没给这条分配码，
   暂用 EUI028；C4 若要细化请另起新码，不复用。→ 台账 D71

**扫描器与 14.10.2 伪码的一处实现差异**：伪码写 `c.SourceModule.ReferencedAssemblySymbols`，
Roslyn 4.9.2 的公开 API 没有这个成员（公开的是 `IModuleSymbol.ReferencedAssemblies : ImmutableArray<AssemblyIdentity>`，
只有标识符没有符号）。实现改为遍历 `Compilation.References` → `GetAssemblyOrModuleSymbol` 取回 `IAssemblySymbol`，
再按 `assembly.Identity.Name == "EclipseUI.Runtime"` 或任一 module 的 `ReferencedAssemblies` 命中该名字来过滤；
语义等价（同样是「源程序集 + 直接引用运行时的程序集」）。

**测试覆盖**（`tests/EclipseUI.Compiler.Tests/CatalogScannerTests.cs`，42 例，Theory 按数据行计数）：
发现规则 12（含暴露判定 6 数据行）、属性暴露 5、内容模型 8（含内置标注 5 数据行）、
组件参数/插槽与空编译 5、注册表/事件字段/失效级别交叉校验 3、派生 ⊇ 手写与差集枚举 3、
标签解析与冲突 3（含真正跨两个已编译程序集的同标签 → `Ambiguous` + 候选名）、快照与确定性 2、
`CanonicalKey` 与 `global::` 显示名不串键 1。

### 14.12.2 C2 落地记录（2026-11-02）

全部 C2 工作项完成，`dotnet test` 257 例全绿（Headless 103 / Compiler 93 / Rendering 61）：

| 工作项 | 结果 |
|---|---|
| 生成器切到派生目录 | `TemplateGenerator` 的管道值是 `UiCatalogFactory.Build(compilation)`；分析器 `EUI040` 目标判定改读派生 `CanWrite`（spec/03 D62 关闭）。样例项目（Counter/Prefs/TaskList 组件经 `.ui`）与合成第三方程序集均由扫描器/生成器间接覆盖 |
| 内置控件加 `[UiContent]` / 基础设施元素加 `[UiIgnore]` | C1 已随标注落地（§14.12.1） |
| `InputEvents` 迁入 `Element` | 7 个 `RoutedEvent` 字段迁入 `Element`（[Element.cs:118-124](../src/EclipseUI.Runtime/Element.cs#L118)），静态类删除；19 处 `InputEvents.X` 调用点（Runtime + 测试）机械改名，路由行为测试全绿 |
| `CheckBox.CheckedChangedEvent` | 已补（`Controls.cs`）；仅交互路径广播，绑定回推走 setter 不广播 |
| 手写目录删除 | `Catalog.cs` 重写为只剩 `UiLiterals`/`Suggest`；`CreateDefault()` 全仓零引用 |
| golden 快照落成文件 | `tests/EclipseUI.Compiler.Tests/TestData/BuiltInSurface.golden.txt`（`ECLIPSEUI_UPDATE_SURFACE_GOLDEN=1` 再生）+ 枚举快照测试钉 9 个事件名 / 37 事件项。D72 按修订语义收敛：快照是评审门，不是反序列化源 |
| `Metadata` 源码链接进生成器 | 生成器程序集 `Compile Include` Compiler + Metadata（排除 Metadata 的 `Polyfills.cs`）；测试项目只引用生成器，避免重复类型身份。D65 关闭 |
| `@foreach` 生成代码 `global::` 限定 | Emitter 不再生成未限定 `List<...>`，生成代码零 `ImplicitUsings` 依赖。D22 关闭 |

**模板表面净值**：标签 5 不变；属性项 6 → 17（12 项继承属性生效）；事件 5 个标签合算 37 事件项、
9 个不同事件名（7 继承输入/焦点 + `OnClick` + `OnCheckedChanged`）。
样例（Counter/TaskList/S1Probe）语法零改动即通过——表面变宽是纯增量。

**一处附带代码结构变化**（记为 C2 表面变更的一部分）：内置控件命名空间从 `EclipseUI` 迁到
`EclipseUI.Elements`（`Panels.cs`/`TextBlock.cs`/`Controls.cs`），生成代码引用全部走 `global::` 限定，
`.ui` 标签名不受影响；code-behind 若直接声明控件存量引用需带 using（样例已调整）。

**运行期不变量**复测通过：`StyledPropertyRegistry`/事件字段交叉一致、失效级别白名单
（唯一例外仍是 `Element.IsHitTestVisible`）均随新表面复跑。

### 14.12.3 C3 落地记录（2026-11-03）

全部 C3 工作项完成，`dotnet test` 281 例全绿（Headless 127 / Compiler 93 / Rendering 61）：

| 工作项 | 结果 |
|---|---|
| 转换器扩展 | `UiLiterals` 重写为按 `UiTypeShape` 判定：新增 `HasConverter()`（EUI023 判定）与 `TryConvert(shape,…)`；`Catalog.cs` 手写类型表从此不再参与分析（文件只剩常量与转换器）。`float` 越界检查、整数显式窄化、枚举 `global::Ns.Type.Member`（候选成员随目录 `EnumMembers` 给出）、TimeSpan 编译期换算 ticks 均落地 |
| EUI023 | 与 EUI003 拆分：无转换器 → EUI023（提示 `{expression}`）；有转换器但格式/范围/成员错 → EUI003（design/03 D14 关闭）；`UiCatalogFactory.CanonicalKey` 补 `System_Int16 → "short"` |
| EUI025 | 全重复普值属性在属性级收敛；**同名且任一侧是绑定（`@bind-P`/`:bind`）仍走目标级 EUI033**，与 S2 测试语义保持一致（spec/01 §3.1/§3.3）。design/03 D6 关闭 |
| EUI031 | `Class`/`Style`、点号附加属性（任意 `Owner.Prop`）在使用点报 EUI031；`<Slot>`/`<ContentSlot>` **仅当目录无同名真实控件时**报 EUI031。design/03 D2/D3/D19 关闭 |
| 测试 | `tests/EclipseUI.Headless.Tests/C3LiteralAndDiagnosticsTests.cs`：24 例（合成 Probe 标签测使用点诊断；`UiLiterals` 单元级直测，含负 TimeSpan、`\0`/`\uXXXX` 转义、char 不 Trim、范围拒绝） |

**范围说明**：EUI016/EUI019/EUI020/EUI021/EUI026 的扫描侧元数据诊断已在 C1/C2 落地（`Problems`/`HiddenTags`），
其**使用点**口径已在 C5 落地（§14.12.5）；`Ref`（EUI029）也在 C5 落地（此前即 EUI002）。
`Color`/`Thickness`/`CornerRadius` 转换器随 G4 类型引入。

### 14.12.4 C4 落地记录（2026-11-05）

全部 C4 工作项完成，`dotnet test` 320 例全绿（Headless 145 / Compiler 114 / Rendering 61）：

| 工作项 | 结果 |
|---|---|
| 参数目录 | `UiParameterDef` 扩展 `TypeShape`/`DelegateArity`/`IsContent`/`ContentIsMultiple`；扫描器 `CollectComponentMembers` 重写：委托形状校验（非 void 返回/`ref`/`out`/`in` → EUI028）、非法泛型实参（`ref struct`/指针/`Span<T>` → EUI028）、`[UiContent]` 参数识别（`Element?` 单根 / `IReadOnlyList<Element>` 多根，其余 → EUI021）、类级 `[UiContent]` 拒绝、多内容参数拒绝；`{P}Changed` 约定链接（继承含在内）。参数显示名按 R4 归一为 `global::System.*`（special-type 关键字补齐） |
| 语义分析 | `Analyzer.VisitElement` 按 `Origin` 分支：组件 = 保留属性 → 参数表（委托参数即事件，不做 `On*` 推断，EUI002 消息为 "unknown parameter"）→ 未知；`@bind-P` 组件目标（EUI040，含内容参数/委托参数/无 `{P}Changed` 三种原因提示）；内容体内 `@bind` → EUI040；内容参数经 `P=` 赋值 → EUI021；委托参数字面量 → EUI004；必填缺失 → EUI027；内容上下文 `ContentSingle`/`ContentMulti` 穿透嵌套块（内容内 `@if`/`@foreach`/动态属性保持 build-once 语义），单根静态多根 → EUI021；`Key` 在内容内 → EUI010（既有 allowKey 机制直接覆盖） |
| 生成形态 | `Emitter.EmitComponent` 按 spec/02 §4.12 冻结顺序：new → 作用域登记（根 `Track` / 块 `Adopt + AddDisposable` / 内容 `receiver.TrackContent`）→ 参数（源码顺序；字面量 §4.2、动态推送 §4.3.3 `U{N}`/`R{N}` + `EqualityComparer` 保护、委托事件 §4.6.2 `h{N}` + `a0..aN` 包装、`@bind` §4.4.3 回调 + 推送）→ 默认内容 §4.12.3（`List<Element>` 累加、单根 `content{N}.Count == 0 ? null : content{N}[0]`）→ `Mount()` → `parent.Children.Add(cmpN.Root)` / 根返回 `cmpN.Root`。块内 `scopeN.AddRoot(cmpN.Root)`，内容内 `contentN.Add(cmpN.Root)`。根级动态参数/绑定参数 → `__cmp{N}` 字段提升（`U{N}` 可捕获）；内容内动态属性为 Build 快照（不生成 `U`/`R`），内容内 `@if`/`@foreach` 生成普通 C# 语句（`EmitPlainIf`/`EmitPlainForeach`） |
| 生成器校验 | `ValidateBindSource` 扩展到组件目标（`@bind-P` 源可写性按参数类型判定，spec/02 §4.4.3 规则 8 无 EUI042） |
| 测试 | `C4ComponentTests`（Headless 18 例：参数解析/EUI002 参数建议/EUI004/EUI027/EUI040 三形态/EUI021 双场景/EUI010 内容/EUI033 重叠；合成 `ProbeCard`/`ProbeList` 目录）+ `CatalogScannerTests` 9 例（EUI028 形状/类型/内容参数/类级 `[UiContent]`/多内容参数/继承/约定链接）+ `GeneratorContractTests` 11 例（生命周期顺序/字段提升/U 推送形态/回调形态/块级 Adopt+R/TrackContent/字面量化/EUI027/EUI040/EUI021/EUI004 阻断生成/内容快照无 U/R） |

**范围说明**：具名插槽（`[UiSlot]` 消费）✅ C6 已落地（§14.12.6）（✅ C5 已由 §14.12.5 落地 Ref 与使用点诊断）；`{P}Changed` 类型与参数严格匹配静态不可判，
由 C# 编译错误兜底（spec/02 §12 D73）；组件参数推送不会重建子组件模板（无参数 diff，spec/03 §10.2）。

### 14.12.5 C5 落地记录（2026-11-05）

全部 C5 工作项完成，`dotnet test` 347 例全绿（Headless 160 / Compiler 126 / Rendering 61）：

| 工作项 | 结果 |
|---|---|
| Ref 解析 | 分析器在保留属性分支新增 `Ref`（spec/01 §4.3/§4.15）：值必须是 `{expr}` 形态的简单标识符（`IsValidIdentifier`），非法 → **EUI029**（语法级，ValueSpan）；合法 → `attr.RefTarget` 进入生成。`@bind-Ref`/`Ref:bind` 仍走既有保留名 `EUI040`，重复 `Ref` 走 EUI025 |
| Ref 生成 | `Emitter` 按源码顺序在属性循环内发 `{field} = elN;`（控件 = 元素实例）/ `{field} = cmpN;`（组件 = 组件实例，design/14 §14.6.4），`#line` 映射属性值位置；**不进 `Render()`、不参与字段提升**；块内条目重建随构造赋值 → 字段始终指向最新实例。生成形态冻结于 spec/02 §4.14 |
| Ref 语义探针 | `TemplateProbe.IsRefTarget`：把 `{target} = __refValue`（`__refValue` 为元素/组件类型）放进合成方法体。**C# 诊断优先于符号**——缺失 CS0103 / 类型不符 CS0029 / 只读 CS0191（readonly 实测 bound 失败把 Symbol 变 null，必须先问诊断）/ const / static 由赋值 span 内错误回答；干净后要求 `IFieldSymbol` 非静态实例字段；目标命中 `@foreach` item 名直接拒绝 |
| EUI019/020 使用点 | `UiElementDef` 新增 `IsFromReferencedAssembly`（扫描器按 `compilation.Assembly` vs 引用程序集标记，进值相等/哈希——增量缓存按值比对不受影响）。分析器未知属性分支先查 `def.Problems`：`PropertyNotBindable` 按属性名精确匹配 → **EUI019**；`On{x}` 按 `{x}Event` 字段名匹配 `EventFieldMissing` → **EUI020**；消息带完整类型名（`ClrTypeDisplayName`）+ "declared in a referenced assembly"。源项目控件的同类问题仍落 EUI002（声明处归 EUI026），满足 14.9.1 v2 口径 |
| EUI021 使用点 | 内容被降级为 None 且 def 带 `ContentTargetInvalid` 问题（引用程序集）时，用点 children 接管既有 EUI006 指引 → **EUI021**；源项目控件保留 EUI006 指引不变 |
| 测试 | `C5UseSiteTests`（Headless 15 例：Ref 合成标识符/字面量/成员访问/裸 token（EUI100×2+EUI029）/重复/`@bind-Ref`；引用程序集 EUI019/EUI020/EUI021 + 源项目对照（仍 EUI002/EUI006）+ 自闭合不触发）+ `GeneratorContractTests` 12 例（Ref 控件/组件生成形态、缺失/类型/readonly/静态探针、非标识符/字面量 EUI029、**真实 Emit 引用程序集**端到端 EUI019/EUI020/EUI021 与干净不触发）|

**范围说明（C5 后仍开放）**：EUI026 声明处诊断在 D74 ② 之前确实未消费（目录 problem 只进 `HiddenTags`/`def.Problems`/`catalog.Problems`），**2026-11-08 已由 D74 ②/D81 消费**（见 §14.9.1 后说明）；Roslyn tokenizer 已落地（2026-11-05：`@foreach` 头与事件委托形态判定从正则换成解析/深度感知扫描）；**EUI024 处理器签名匹配已落地（2026-11-08，D82）**，契约用例见 §14.11.2；G4 样式不变（插槽项已由 §14.12.6 的 C6 关闭）。

### 14.12.6 C6 落地记录（2026-11-05）

具名插槽双侧落地（消费侧 `<Slot>` + 宿主侧 `<ContentSlot>`），`dotnet test` 409 例全绿（Headless 190 / Compiler 158 / Rendering 61）；本切片新增 54 例（`C6SlotTests` 26 + `GeneratorContractTests` C6 13 + `CatalogScannerTests` C6 15）：

| 工作项 | 结果 |
|---|---|
| 句法保留名 | `<Slot>`/`<ContentSlot>` 由**解析器特判**成槽节点（`Parser.CreateSlotNode` → `UiSlotNode`/`UiContentSlotNode`），不再依赖 C3 的「目录无同名真实控件才拦截」口径；目录把注册这两个标签名的类型（类名）挡进 `HiddenTags` 并记 **EUI031** 保留冲突（`UiCatalogFactory`） |
| 声明侧 | `UiSlotDef(名字, 属性名, 类型显示名, 基数)`；`Element?`/`Element` → Single、`IReadOnlyList<Element>` → Multiple（严格匹配 OriginalDefinition，`List<Element>` 不算）、其余类型 → **EUI021** 且槽不进表面；重复槽名 / `[UiParameter]+[UiSlot]`（参数胜出，槽退出）/ `[UiIgnore]+[UiSlot]`（忽略胜出）/ `[UiSlot]` 空名 → **EUI028** |
| 基链胜出规则 | 成员基链自己走（Roslyn 不把特性带进 override），但同名成员**只在派生侧显式声明模板特性或 `[UiIgnore]` 时才算覆盖**：否则基类声明继续生效（override 不写特性 = 继承，写 `[UiIgnore]` = 显式退出）。契约原文未定此规则，登记 **D76** |
| 消费侧分析 | 位置（只允许已解析组件标签的直属子级；控件容器、`@if`/`@foreach`、文档根、未知标签下 → EUI022）/ `Name` 形状（缺失、非字面量、重复、多余属性）/ 未知槽名含建议 / Single 的重复填充与多根填充 → **EUI022** 家族；解析成功按基数进 `ContentSingle`/`ContentMulti`，继承 build-once 内容规则（`@bind` → EUI040、`Key` → EUI010、裸文本 → EUI006）；单根多根报错仍保留首根解析（与 §4.12.3 同口径） |
| 宿主侧分析 | 自身插槽两级解析（**D75**：生成器按 code-behind 符号沿基类链读 `[UiSlot]` 作权威，静态前端按目录显示名与 `.ui` 文件名短名匹配）；位置（元素容器子级才合法；`@if`/`@foreach`、模板根、`Text`/`TextRuns`/`Label` 模型、组件使用点的槽填充内 → EUI022）/ 同名重复放置 / 带内容 / 多余属性 / 非字面量 `Name` / 自身插槽不可解析 → **EUI022** |
| 生成形态 | spec/02 §4.12.5 冻结：消费侧按填充源码顺序聚合到同一条 `List<Element>`，Single 赋 `slot.Count == 0 ? null : slot[0]`、Multiple 整表一次赋值，位置在参数与默认内容之后、`Mount()` 之前（子组件首次 `Build()` 在 `Mount()` 内读槽属性）；无填充不生成任何插槽代码。宿主侧**不引入运行时元素**，在组件自身 `Build()` 的该位置内联拼接（Single `if (P is { } s) parent.Children.Add(s);`，Multiple `foreach (var s in P)`） |
| 测试 | `C6SlotTests`（Headless 26 例：消费侧解析/形状/未知含建议/重复填充/多根拼接/位置三态/`@bind`与`Key`继承；宿主侧单多根解析/未知/缺名/多余属性/带内容/块内/根位/重复放置/使用点/自身 def 不可解析/`Text` 模型）+ `CatalogScannerTests` 15 例（基数、EUI021 三形态、EUI028 重复/空名/参数胜出/忽略胜出、继承/override/`[UiIgnore]` 覆盖、保留标签名冲突 EUI031 + HiddenTags）+ `GeneratorContractTests` 13 例（单/多根发射形态、多填充拼接一次赋值、宿主 Single/Multiple 内联拼接、EUI022 阻断生成、**真实编译生成代码零 C# 错误**、赋值先于 `Mount()` 的顺序契约） |

**范围说明（C6 后仍开放）**：C6 只落地具名插槽——design/09 原文把「附加属性、泛型标签」与插槽并列，实拆为插槽单独成片，
附加属性（`Grid.Row`，EUI031）仍留 G4；泛型标签（`DataGrid<Order>`，契约 EUI030、当前 `EUI100`，D5）
按 K19 暂缓实现、不排期（暂缓 ≠ 取消，D5 仍是开放项），保留位诊断维持现状；
插槽转发（把收到的槽再传给下一层，`<Slot>` 内 `<ContentSlot>`）不支持 → EUI022；
槽内容随接收方 `TrackContent` 释放、build-once 不随父渲染重算（§14.6.6 口径不变）；
EUI026 声明处诊断（D74 ②）已落地（2026-11-08，D81）；EUI024 处理器签名匹配已落地（2026-11-08，D82）。

## 14.13 风险与开放问题


| 风险 | 影响 | 对策 |
|---|---|---|
| opt-out 暴露策略导致"新增 public StyledProperty 即扩大模板表面" | 控件 API 演进受模板兼容约束 | `[UiIgnore]` 显式隐藏；1.0 前评审一次属性表面；变更记录标注 |
| 符号扫描性能（大项目/多引用） | 生成器变慢 | **实测不构成风险**：14 程序集 / 688 类型全扫 45.1 ms，过滤后 7 个（14.10.3）；缓存交给管道的值比较，生成器不持可变状态（D14） |
| `InputEvents` 迁入 `Element`（C2） | 19 处手写 `AddHandler(InputEvents.X, …)` 改名 | ✅ 已落地（2026-11-02）：机械改名；`InputTests.cs`/`LayoutAndInputTests.cs` 覆盖路由行为，改名后全绿 |
| 组件参数无 diff | 用户误以为参数变化会重建组件 | 参数推送 + 子 `Invalidate()`（14.6.1）；子模板结构不重建；G4 评估参数 diff |
| 组件 `required` 参数 | 与生成形态互斥（实测 CS9035） | 必填改用 `[UiParameter(Required = true)]`；`required`/`init` 报 EUI028（D12） |
| 默认值/失效级别编译期不可知 | 悬停少两项、失效契约无法静态校验 | 划为运行期事实（D13）；由 14.11.1 运行期快照 + 白名单兜底 |
| 标签冲突（不同包同名控件） | 第三方生态冲突 | 默认报错 EUI016（列出候选全名 + 可直接粘贴的 `@using XxxAlt = Ns.Xxx` 建议）；消费端用模板别名消歧（K31/K32）；**注意冲突只在消费端编译时可见**，包作者本地编译无感 |
| 生成器/运行时版本错配 | 元数据解析错误 | 0.x 同版本发布；未知枚举值降级不崩溃 |
| `ITextContentHost` 引入改动 TextBlock | 小范围破坏 | 保留旧 `AddRun(TextRun)`；新增重载 |
| Roslyn 版本耦合（生成器 4.9.2，LSP 版本） | 扫描器/驱动不兼容 | Metadata 项目锁定与生成器一致的 Roslyn 版本；LSP 侧做版本适配层 |
| 生成器误改写表达式 | 语义/诊断漂移 | v4 不重写表达式，只做插值、括号与目标类型转换（spec/02 §4.15） |
| 模板生成器读取其他生成器输出 | 增量不稳定/循环依赖 | 只读用户声明与目录元数据（spec/02 §2.1） |
| 双向绑定绕过属性引擎 | 控件内部状态与绑定不同步 | `UiBinding` 两侧走公开 setter；`SetValue`/`GetValue` 禁止生成（R3/R7） |

**待定（设计已给推荐，落地时确认）**

1. 属性/事件 opt-out 是否在 1.0 前改为 opt-in 白名单（推荐保持 opt-out + `[UiIgnore]`）。
2. ~~组件默认内容是否提前到 C4~~ 已定：C4 做参数与默认内容，C6 做具名插槽。
3. `Ref` 的清理语义（卸载时置 null？）与作用域（code-behind 字段）。
4. 事件 typed args（`Action<TArgs>`）的泛型事件模型。
5. ~~`#line` 列映射（G3）~~ ✅ 已落地（2026-11-08，D79，口径见 spec/02 §2.4 规则 9/10）；
   剩余 `.ui` 行列号在 LSP/调试器中的统一口径；XML 实体不解析已由 03 冻结。
6. 第三方字面量转换器的长期机制（当前明确不支持，避免编译期执行用户代码）。
7. 8 个继承事件是否需要在个别标签上用 `[UiIgnore]` 裁剪（如 `TextBlock.OnKeyDown` 合法但罕用）。

**v2 已决（从待定升为决策）**：组件 `init` setter 与 C# `required` 均不允许（EUI028，D12）；
输入事件声明位置迁到 `Element`（D11）；悬停不显示运行期值（D13）；生成器不做手工缓存（D14）。

## 14.14 决策记录

| # | 决策 | 理由 |
|---|---|---|
| D1 | 单一事实源 = C# 符号（StyledProperty / RoutedEvent / `[Ui*]`）；运行时 registry 只做镜像与校验 | 编译期必须在不执行程序集的前提下拿到元数据；手写目录必然漂移 |
| D2 | 控件属性/事件默认暴露，`[UiIgnore]` 隐藏 | `StyledProperty` 注册已是显式公开声明；避免第二份元数据 |
| D3 | 组件参数 opt-in（`[UiParameter]`） | 组件的普通 CLR 属性可能是实现细节，默认暴露风险高 |
| D4 | 内容模型显式声明，默认 `None` | 内容模型是公共 API；猜测会制造隐式契约 |
| D5 | 字面量转换编译期完成、零反射；无转换器只能写表达式 | AOT/裁剪友好；错误提前到编译期 |
| D6 | 组件无参数 diff；参数是普通 CLR 属性，父渲染时按源码顺序推送，`EqualityComparer<T>` 相等保护后子 `Invalidate()` | 不重跑 Build 的结构语义不变；参数类型不受限（spec/03 §10） |
| D7 | `P={expr}` 每次渲染重算；双向必须显式 `@bind-P` | 默认单向符合直觉；无依赖分析，方法调用/索引器/lambda 一律合法（spec/01 §4.4/§4.5） |
| D8 | 标签冲突报错，不做"就近优先" | 静默优先级会随引用变化改变语义 |
| D9 | `TextRuns` 用 `ITextContentHost` 接口而非约定方法名 | 可编译期校验，错误可定位 |
| D10 | 编译期不执行用户代码（含第三方转换器） | 生成器安全边界；用户代码执行不可控 |
| D11 | 事件字段必须声明在 `Element`/`Component` 或其派生类型上；静态宿主类不参与扫描（框架自己的 `InputEvents` 在 C2 迁走） | 继承是路由事件的语义基础；否则模板事件表面只有 `OnClick` 一个，T6 能力对等破产 |
| D12 | 必填参数用 `[UiParameter(Required = true)]`，不用 C# `required` | 生成形态是 `new C()` 后逐条赋值，`required` 会让 `new C()` 直接编译失败（实测 CS9035）；对象初始化器会破坏 14.8.2 的顺序保证 |
| D13 | 默认值与 `Invalidate` 级别是**运行期事实**，不进编译期目录、不进悬停 | 它们来自 `Register(...)` 构造实参与构造函数赋值，编译期不可读（R1）；契约校验改由运行期快照测试承担 |
| D14 | 目录缓存交给增量管道的值比较，生成器不持有可变静态状态 | 手工 `AssemblyIdentity` 缓存与 R6/14.8.2 冲突；实测 45.1 ms 全扫说明不需要预优化 |
| D15 | 生成代码类型名用 `FullyQualifiedFormat` 显示名，查表键另设 `CanonicalKey` | 嵌套/泛型/可空类型不能靠字符串拼装；v1 用裸 `"double"` 同时当两者会静默失配 |
| D16 | 双向用 `@bind-P`（Blazor 风格）；`:bind`/`:value` 弃用 | 方向显式、语法可读；旧模板有弃用期（EUI043） |
| D17 | 组件参数是普通 CLR 属性；双向用可选 `{P}Changed` 回调 | 模板作者只写普通类型；不重跑 Build；状态生成器与可观察参数已取消（spec/03 §8.3） |
| D18 | `@foreach` 条目复用 = Key + item 身份；身份变化重建 | 禁止闭包/事件指向旧 item；O(变动数) |
| D19 | `Key` 可引用任意可读表达式；`EUI039` 停用 | 身份稳定性由作者负责；条目复用仍要求 Key + item 身份（D18） |
| D20 | 停用 EUI014/EUI032/EUI034–EUI036/EUI038–EUI039/EUI041–EUI042/EUI044；新增 EUI045 | 无状态生成器/依赖分析/可观察参数；下一可用码 EUI046（spec/03 §11） |
| D21 | 旧语法 `:bind`/`:value` 至少保留一个发布周期，S5 评估移除 | 现有模板可迁移；不静默破坏 |

## 14.15 v2 修订记录

对首版做可实现性实测（Roslyn 4.9.2 API 反射、`required` 语义编译验证、真实引用集扫描计时、元数据组件发现验证）后落地的问题与处置：

| # | 问题（首版） | v2 处置 |
|---|---|---|
| B1 | 事件只扫元素/组件类型上的字段 → 7 个输入/焦点事件（在 `InputEvents` 静态类）全部不可见，模板事件只剩 1 个，违背 T6 | D11；C2 把字段迁入 `Element`，事件表面 1 → 8；静态宿主类报 EUI026 |
| B2 | 14.6.1 支持 C# `required` + 14.8.1 `new C(); c.X = …` 互斥（实测 CS9035） | D12；必填改 `[UiParameter(Required = true)]`，`required`/`init` → EUI028 |
| B3 | 14.10.5 悬停要显示 `Invalidate` 与默认值——编译期拿不到 | D13；悬停只列符号可派生项；失效契约改由 14.11.1 运行期快照 + 白名单守卫 |
| B4 | 14.10.2/14.10.3 手工按 `AssemblyIdentity` 缓存 + 14.11.2「目录不重建」在增量生成器里不成立 | D14；删缓存，改「目录重建、下游 `Cached`/`Unchanged`」，附实测数字 |
| C1 | C1 出口「派生目录与手写目录等价」不可能成立（6 → 17 属性项） | 改为 ⊇ + 差集评审 |
| C2 | `UiTestCatalogs.BuiltIn()` 手写会造第二事实源 | 改为扫描真实 `EclipseUI.Runtime.dll` + golden 快照 |
| C3 | `ClrTypeName` 裸字符串同时当生成类型名与转换器键 | D15；`ClrTypeDisplayName` + `CanonicalKey` 分离 |
| C4 | EUI019 与 opt-out 发现规则冲突（未注册属性只会撞 EUI002） | 14.9.1 限定 EUI019 口径；EUI002 增加"未注册 StyledProperty"提示 |
| C5 | `Invalidate` 契约无任何可执行守卫（框架自己 2 个属性是 `None`） | 14.11.1 失效级别守卫 + 显式白名单 |
| C6 | 暴露属性可占用 `On{事件}` 名被静默吃掉 | 14.4.2 禁止 + EUI026 |
| C7 | 未实现的保留语法（`Class`/`Style`/`:value`/`<Slot>`）会报"未知" | 新增 EUI031 |
| C8 | 组件参数一次性赋值引用信号无提示 | 新增 Warning EUI032（C4 落地） |
| C9 | 14.4.6 写「程序集 `EclipseUI`」——实际程序集名是 `EclipseUI.Runtime` | 修正为按全 metadata 名（`EclipseUI.UiContentAttribute`…）识别，禁止按程序集名匹配 |
| C10 | 14.5.3 未区分控件/组件的属性解析分支 | 按 `Origin` 分支写明 |
| K32 | `[UiTag]` 与模板别名（K31）职责重叠：改名只能改自家库，改不了第三方库，且给目录引入第二事实源（类型名 ≠ 标签名） | **退役 `[UiTag]`**（2026-11-09）：标签恒为类型短名；重名一律在消费端用 `@using XxxAlt = Ns.Xxx` 消歧；EUI016 消息直接给出该建议；`UiCatalogFactory.TagOf/IsValidTag` 删除，`UiTagAttribute` 从 `EclipseUI.Runtime` 移除 |
| — | 已验证为**可行**、无需改动：跨程序集组件发现（元数据 `BaseType` 可解析）、`IsRequired`/`IsInitOnly` 判定、扫描性能、HiddenTags 建议机制、诊断码续号（EUI016+ 空闲） | 保持 |

## 14.16 与 03 语言规范对齐（v2.1）

03 重写为语言规范后，本文按"03 管语法、14 管元数据"做了一次对齐，修正正文中与冻结语义冲突的表述：

| # | 位置 | 修正 |
|---|---|---|
| A1 | 14.6.1 更新语义 | 删除"引用 signal 即生成 updater"；组件参数只在 Build/条目复用时赋值，signal 反应式走 `Signal<T>` 参数或 EUI032 |
| A2 | 14.6.1 / 14.9.1 EUI032 | 限定为**非 `Signal<T>`** 参数；`Signal<T>` 参数不报警，也不改写为 `.Value` |
| A3 | 14.6.6 #3/#4 | 参数 updater 只在条目复用重跑；item 数据刷新用 item 上的 `Signal<T>` 或数据版本 Key，不用 code-behind 信号 |
| A4 | 14.7.1 / 14.7.2 | `:bind` 右侧必须是 `Signal<T>` 对象；字面量/表达式由 **token** 决定（`P="{x}"` 是字符串） |
| A5 | 14.7.3 | v1 转换器如实列为 4 类型；`float`/`long`/…/枚举/`TimeSpan` 归 C3 |
| A6 | 14.7.4 | 信号重写按目标类型：值上下文取 `.Value`，`Signal<T>` 参数保持对象 |
| A7 | 14.5.3 / 14.9.1 | 保留语法诊断分工：EUI031（`Class`/`Style`/`:value`/插槽/附加属性）、EUI030（泛型标签）、EUI029（`Ref`） |
| A8 | 14.7.2 XML 实体 | 与 03 冻结线一致：不解析；未来支持须先在 03 登记 |

同时 03 §3.5.3 增加 D18（`ForEachBlock.Dispose` 不释放条目作用域）、D19（附加属性报 EUI002）、D20（`TextRuns` 生成形态）与 D21（元数据缺失诊断），作为 G3/C2/C3 收敛项；
03 为"同一目标重复赋值"分配 EUI033（14.9.1），由 C5 落地。
附录 A 补记 D22：`@foreach` 曾生成未限定的 `List<...>`、依赖项目 `ImplicitUsings`（C2 已修复；
生成代码类型全部 `global::` 限定，测试断言零 `ImplicitUsings` 依赖）。

## 14.17 与 03 v3 / 15 对齐（v3）

03 升级为 v3（`P={expr}` 单向反应式、`@bind-P` 双向）并新增 15 后，本文同步修订：

| # | 位置 | 修正 |
|---|---|---|
| B1 | 头部 / 14.3 | 增加 R7–R9；R3 明确禁用 `SetValue/GetValue`，旧 `Bind.TwoWay` 标 Obsolete |
| B2 | 14.5.1 | `UiParameterDef` 增加 `IsObservable`；供 `@bind` 目标与 EUI044 判定 |
| B3 | 14.6.1 | 三种参数形态（可观察 partial / 普通 / `Signal<T>` legacy）；EUI032 → EUI044 |
| B4 | 14.6.6 | 限制改写为可观察参数与 Key+item 身份重建；item 可观察属性可绑定 |
| B5 | 14.7.1 | `P={expr}` 单向反应式；新增 `@bind-P`；`:bind`/`:value` 弃用（EUI043） |
| B6 | 14.7.4 | 状态属性直接读、`Signal<T>` 取 `.Value`；item 成员不重写；EUI014 收窄 / EUI039 / EUI042 |
| B7 | 14.8.1 / 14.8.2 | 生成形态换成 `UiWatch`/`UiBinding`；订阅作用域按 15 §15.3.6；setter 幂等 |
| B8 | 14.9.1 | EUI032 停用；修订 EUI004/EUI014/EUI031；补 EUI034–EUI044 |
| B9 | 14.12 | C4 只负责组件标签/参数元数据；C5 只保留 `Ref` 与语法收敛；S1–S5 由 15 排期（S1–S3 在 G3，S4 在 G4，S5 在 G8） |
| B10 | 14.13 / 14.14 | 风险与 D6/D7 更新；新增 D16–D21 |
| B11 | 14.15 C8 / 14.16 旧记录 | 14.15 C8「新增 EUI032」与 14.16 A1–A2 的 EUI032 描述按本节作废（B3/B8 已改 EUI044）；"由 C5 落地"的 EUI033/`:value` 项作废；历史记录保留 |

**冲突处理**：03 v3/15 与本文 v2.x 冲突时，以 03 v3/15 为准；本文 14.15/14.16 仅作历史记录，不再作为实现依据。
（v4 起该规则改由 §14.18 接管。）

## 14.18 v4 换轨（2026-10-29）

v4 把模板反应式改为组件级失效重算，本文以下条款被取代：

| # | 位置 | v4 处置 |
|---|---|---|
| V1 | 14.6.1 参数形态 | 只有普通 CLR 属性；父推送 + `EqualityComparer` + 子 `Invalidate()`；双向用 `{P}Changed`（spec/03 §10） |
| V2 | 14.6.3 内容 | build-once 静态展开；`@bind` → EUI040；子组件 `receiver.TrackContent`（spec/02 §4.12.3） |
| V3 | 14.6.6 限制 | 普通参数可刷新子 UI；item 模板可读组件状态；EUI014 停用 |
| V4 | 14.7.1 / 14.7.4 | 无依赖分析/可观察路径/信号重写；表达式每次渲染重算（spec/03 §4–§5） |
| V5 | 14.8 | v3 生成形态历史；v4 以 spec/02 为准 |
| V6 | 14.9.1 | EUI014/EUI032/EUI034–EUI036/EUI038–EUI039/EUI041–EUI042/EUI044 停用；新增 EUI045（C3） |
| V7 | 14.12 / 14.13 | 反应式排期改按 spec/03 §13；C4 只做组件标签/参数/默认内容 |
| V8 | 14.15–14.17 | 历史记录；其中 D6/D7/D17/B2–B11 的反应式条款不再作为实现依据 |

**冲突处理（v4）**：语法以 spec/01 为准，生成形态以 spec/02 为准，反应式语义以 spec/03 为准；
本文只保留控件/组件元数据声明规则（待迁移为 spec/04）。
