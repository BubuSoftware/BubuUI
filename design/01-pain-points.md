# 1. Blazonia 痛点分析

现状：Blazonia 是微软 MobileBlazorBindings（Xamarin 版）到 Avalonia 的移植——
Razor 编译 → `RenderTreeEdit` 流 → `NativeComponentAdapter` 影子树 → `IElementHandler`
操作真实 Avalonia 控件；生成器为独立控制台程序，约 200 个 `*.generated.cs` 提交入库。

## 1. 三重对象模型

每个逻辑控件同时存在三个对象：Blazonia 组件（身兼 `IElementHandler`）、adapter 影子节点、
真实 Avalonia 控件。`GetChildPhysicalIndex`（`src/Blazonia/Core/NativeComponentAdapter.cs:343`）
递归重数物理索引，子树一深即 O(n)。组件双重身份导致 `PhysicalTarget`、`INonPhysicalChild`、
null adapter 代表文本节点等补丁逻辑遍布桥接层。

## 2. 编辑合批算法脆弱

`AddPendingRemoval`（`NativeComponentAdapter.cs:151`）靠逐个比较平移下标把 Remove 插到
Add 前面，可读性与可验证性差；`StepIn`/`StepOut` 忽略并留有 TODO（`NativeComponentAdapter.cs:90`）；
文本靠 `IHandleChildContentText` + `TextSpanContainer` 硬凑，不支持内嵌文本的容器直接抛异常。

## 3. 生成代码入库 + 控制台工具，生态封闭

- 200 个生成文件提交入库，Avalonia 升版本需手动重跑工具，必然漂移。
- 生成器用 MSBuild Workspace 加载 Avalonia 元数据，第三方控件库无法方便接入
  （对应 issue #26）。
- 非 Roslyn 增量源生成器，无编译期即时反馈。

## 4. 参数管道字符串分发且全量重放

`AvaloniaObject.SetParametersAsync`（`src/Blazonia/Components/AvaloniaObject.cs:23`）每次
渲染把所有参数（含 `AdditionalProperties`、`AttachedProperties` 字典）逐个塞进
string-switch 的 `HandleParameter`；生成代码中 `Equals` 装箱比较、默认值回填走
`AvaloniaProperty.GetDefaultValue` 元数据查询——放弃 Avalonia 属性系统的一切优化，
退化为"每次 re-render 全属性重放"。

## 5. 列表与模板性能

- `ItemsSourceComponent` 每次渲染把 `IEnumerable` 重灌进 `ObservableCollection`，
  每个 item 包一个 `ItemHolderComponent`，注释自认 "Probably not the most performant way"。
- `DataTemplateItemComponent` 每条目订阅 `DataContextChanged`，`ShouldRender` 优化被整段
  注释掉（`DataTemplateItemComponent.cs:48`）。
- Avalonia 虚拟化惰性建容器 vs Blazor 侧急切渲染，两套模型错位，大列表双重开销。

## 6. 与 Avalonia 原生体系是"寄生"关系

- 应用外壳必须 XAML：`BlazoniaControl<T>` 是 `ContentControl`，razor 页面只能塞进其
  `Content`；窗口、主题、资源字典、样式在另一个世界。
- 每个 `BlazoniaControl` 自建 `ServiceProvider`，DI 边界混乱（issue #27）。
- axaml `Binding` 与 Blazor 状态推送形成**双真相源**，覆盖优先级无定义。

## 7. 工程健壮性欠账

无单元测试（issue #4）；`UnsafeAccessor` 反射进 `EventCallback` 私有字段、多处 `dynamic`
——对 AOT/裁剪不友好；`BlazoniaControl.Init()` 为 `async void`；异常借道
`BuildRenderTree` 重新抛出，调试体验绕。

## 8. Razor/Blazor 语法本身的债

- HTML 标签兜底：未知标签静默变成 HTML 元素帧，运行时才炸。
- 字面量属性必须 `@("xxx")`（Razor 把裸属性值硬编码为 string 字面量）。
- 附加属性是运行时字符串注册表，无类型、无 IDE 提示。
- `TItem="List<Order>"` 把类型当字符串；`@onclick` 与 `@bind` 两套特殊语法；
  属性名大小写不敏感。

## 结论

Blazonia 的根本困境是两个模型硬焊——组件运行时与语法层同时继承，等于替
Blazor 的 HTML 语法买了单。新框架的边界：语法、编译器、运行时全部自有；不复用
Blazor 运行时；Roslyn 只作为基础设施（解析器、语义模型、增量生成框架）复用。

> **K23 修订（2026-11-08，✅ S6 已实现（2026-11-08））**：上句「语法全部自有」仍成立——
> **不复用 Razor 编译器 / 语法层**；但允许**借鉴 Razor 的组织形态**：流程控制（`else`/`else if`/`@switch`）、
> 模板内代码块（`@code` 单文件）、`@using` 指令、`@* *@` 注释。§8 列出的 HTML 语法债仍是拒绝理由，
> 故不引入 HTML 宿主语言的过渡机制（`@expr`/`@()`/`@@`/`@:`/标签兜底/`@("xxx")`）。
> **「不复用 Blazor 运行时」这条核心不变**（K1–K18 保留元素树 + 失效重算）。
> 权威决策 spec/02 §14 K23，目标语法 spec/01 §1.5，偏差 D85–D88。
