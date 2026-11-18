# 05 · 无内置外观控件与主题

2026-11-17。本文定义控件、视觉模板和主题包的边界。

## 控件与视觉元素

交互控件只拥有属性、状态、输入、焦点、数据及模板部件契约，不内置特定主题的绘制、颜色或尺寸。Border、Shape、TextBlock、TextPresenter 等基础视觉元素继续提供通用绘制。无主题时不回退到内置的控件皮肤。

所有 Element 都通过相同的 Theme、Template、TemplateRoot 和 ApplyTemplate 机制承载模板，不要求改变现有控件的继承链。ControlTemplate<T> 的 T 必须为 Element。显式 Template 优先于 Theme 提供的模板。模板每次生成独立视觉树；更换模板释放旧视觉资源，保留调用方内容、数据和输入状态。

模板根与控件的公开内容集合分开存储。模板通过 ItemsPresenter 投影集合，通过 ContentPresenter 投影单项内容。投影采用真实 Parent 链，内容仍属于源集合；换模板先解除旧投影，再建立新投影，不销毁借入内容。所有更新、绘制、命中测试、焦点和动画遍历使用同一 VisualChildren。

## 主题包

EclipseUI.Themes.Fluent 独立提供各交互控件的 .ui 模板、@theme 属性和状态规则、共享配色、外观尺寸及默认主题映射。Runtime 和通用渲染器不引用 Fluent。应用显式安装主题包，平台默认使用中性主题来源。

控件外观使用通用绘图操作；通用绘制接口不包含 Fluent 开关、控件皮肤绘制捷径或按钮、复选框等类型分发。复杂视觉元素可以在主题包中以 C# 实现，但必须由可替换的控件模板创建，不得通过控件 Render 或渲染器分支回退调用。

## 值来源和更新

同属性优先级为 local > 普通组件 template > class > 类型化应用样式 > 控件主题规则 > 视觉模板 skin > inherited > 应用主题默认值 > 属性默认值。

交互状态、数据、动画和主题变化必须刷新模板表达式及视觉失效；切换明暗模式不重建未改变的模板。默认主题按最具体的注册控件类型查找，派生控件能获得兼容皮肤，具有独立部件契约的派生控件单独注册。

## 共享类型化资源

主题包可以声明公开静态 `ThemeResource<T>` 字段，使用 `Func<Element, T>` 按接收属性值的元素解析资源。例如：

```csharp
public static readonly ThemeResource<UiColor> ControlBackground =
    new(element => element.IsLightTheme ? lightColor : darkColor);
```

`.eustyle` 中的 `Background: resource(FluentResources.ControlBackground)` 可通过 `@namespace` 将当前命名空间及其外层命名空间设为隐式查找范围，也可通过 `@using` 导入字段所在命名空间，或写类型全名。查找顺序为完整类型名、`@namespace` 自身到外层链、显式 `@using` 导入。引用程序集中的公开静态字段使用同一语法。编译器必须验证字段可访问、静态、公开、类型为 `ThemeResource<T>`，且 T 与目标 `StyledProperty<T>` 的值类型一致；名称歧义、字段缺失和类型不匹配均产生样式文件诊断。

`.eustyle` 的 `@namespace` 只控制控件、模板和类型化资源的名称查找，不决定生成的 `StyleBootstrap` 命名空间；生成代码仍使用项目 `RootNamespace`。每个样式模块至多声明一次。内联 `.ui` 的 `@style` 块不能声明 `@namespace`，并沿用宿主 `.ui` 的命名空间。

生成代码直接引用字段及其 `Resolve` 方法，将其作为动态 setter。在控件状态或应用主题变化时重新求值，明确的 local/样式覆盖继续遵守值来源优先级。编译阶段不得执行资源初始化器或解析委托，运行阶段不使用反射或字符串资源注册表。

`Theme: resource(MyTheme)` 继续引用同一编译模块中的命名控件主题。类型化静态资源不扩展为通用动态资源字典、运行期名称搜索或隐式资源继承系统。

## 验收

- 未安装主题的交互控件不产生内置皮肤绘制。
- Fluent 包为全部内置交互控件提供可替换模板；ui 与纯 C# 路径一致。
- 更换模板保留借入内容、选择、编辑、滚动和焦点，解除旧部件事件。
- 输入、命中、测量、浮层、动画与 retained 绘制在模板视觉树中一致。
- 源码结构检查保证 Runtime 无主题绘制，渲染器无控件外观类型分发，平台无 Fluent 依赖。
