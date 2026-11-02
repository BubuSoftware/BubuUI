# 4. 运行时与元素系统

## 组件实例模型

- **组件实例 = code-behind 对象 + 保留元素树**。无组件树 diff、无参数 diff；
  父子通信靠普通 CLR 参数、委托回调与直接引用。父渲染时参数带相等保护推送，子组件 `Invalidate()` 重算自己的动态表达式，
  不重建组件模板（[spec/03 §10](../spec/03-reactivity-and-binding.md)）。

## 状态与所有权

- 状态就是 code-behind 的普通 CLR 字段/属性，不是元素/组件的一部分；框架不观察、不包装状态对象（[spec/03 §1.3](../spec/03-reactivity-and-binding.md)）。
- 模板资源按作用域释放：组件 `Track`/`Adopt`/`TrackContent`、`@if` 内容、`@foreach` item、`UiBinding`（[spec/02 §5](../spec/02-generated-code-contract.md)）。
- 运行时不做读取追踪、代理或 diff；组件 `Invalidate()` 后重算编译期已知的动态位置（[spec/03 §3–§4](../spec/03-reactivity-and-binding.md)）。

## 元素树独立于模板结构

- 模板是"如何建树和绑树"的指令，元素是真正存在、参与布局绘制的节点。
- 文本节点一等公民——`<TextBlock>hello {name}!</TextBlock>` 编译为多个 text run 的
  创建与更新指令，从根上消灭 Blazonia 的 `IHandleChildContentText` 类补丁。
- 对象模型从 Blazonia 的三重（组件 + 影子 adapter + 控件）降为两层
  （组件 + 元素），影子树整层删除。

## 属性引擎（唯一真相源）

- `StyledProperty<T>` 带元数据：默认值、是否可继承、失效回调、样式优先级。
- setter 值短路（`EqualityComparer<T>.Default`，无装箱）+ 失效分级：
  `Inherits` / `Measure` / `Arrange` / `Render`。
- 属性变更只标脏不做事：**diff 只负责"变没变"，属性引擎负责"变的代价"，
  帧调度负责"何时画"**。

## 事件

- 路由事件（tunnel/bubble），订阅表挂元素上，移除即解绑，无泄漏窗口。
- 顺带解决"第三方控件事件如何暴露"的生态问题。

## 组件注册表

- 从解决方案编译中收集组件 / 属性 / 附加属性 / 类型转换器元数据，供编译器与 LSP 共用。
- 第三方 NuGet 包里的组件自动进入补全——生态从机制上开放
  （对应 Blazonia issue #26 的架构侧面）。
- 单一事实源是 C# 符号（`StyledProperty` / `RoutedEvent` / `[Ui*]` 特性），目录在编译期派生，
  运行时 `StyledPropertyRegistry` 只做镜像与校验；控件作者契约、生成代码与诊断规则见
  [14 控件与模板契约](./14-control-template-contract.md)。
- 两条边界（14.4.3 / 14.13）：**事件字段必须声明在元素/组件类型上**，静态宿主类不参与扫描
  （框架自己的 7 个输入/焦点事件已在 C2 从 `InputEvents` 迁入 `Element`，`InputEvents` 类已删除）；
  **默认值与 `Invalidate` 级别是运行期事实**，来自 `Register(...)` 实参，编译期目录不携带，
  只能由运行期一致性测试校验。
