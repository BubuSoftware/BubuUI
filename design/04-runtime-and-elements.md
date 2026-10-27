# 4. 运行时与元素系统

## 组件实例模型

- **组件实例 = code-behind 对象 + 模板 RenderUnit 实例**。无组件树 diff、无参数 diff；
  父子通信靠 signals / 直接引用。

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
