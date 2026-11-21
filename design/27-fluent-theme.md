# 27 · Fluent 主题包

2026-11-17。当前契约以 [spec/05](../spec/05-control-themes.md) 为准。`EclipseUI.Themes.Fluent` 提供 EclipseUI 的 Fluent 风格控件模板、属性和状态规则、共享资源及亮暗配色。早期的默认皮肤和仅 Button 模板化方案已由本文描述的实现替代。

## 显式接入

Runtime、通用渲染器及 Windows/Linux/macOS 平台包均不引用 Fluent。应用引用主题包后显式配置：

```csharp
using EclipseUI.Themes.Fluent;

builder.UseFluentTheme(); // System；也可传 ThemeVariant.Light / Dark

// Application 子类内，运行时切换：
SetTheme(FluentTheme.System);
SetTheme(FluentTheme.Light);
SetTheme(FluentTheme.Dark);
```

窗口初始主题按 `Window.Theme > Application.DefaultTheme > ElementTheme.Light` 选择。平台的默认来源保持中性，不自动安装控件皮肤。未安装主题时，交互控件保留行为、状态和显式属性，不回退到框架内置的 Fluent 外观。基础视觉元素如 Border、Shape 和文本元素仍能通用绘制。

`UseFluentTheme()` 设置应用默认主题来源；它不负责注册应用自己的选择器规则。应用的 `.eustyle` 仍通过 `UseStyles(registry, MyApp.StyleBootstrap.Apply)` 安装。

## 配色与默认主题映射

`FluentTheme.Light` 和 `Dark` 为共享的具体配色实例；`System` 是按系统偏好解析到二者之一的来源对象。Windows 读取 `AppsUseLightTheme` 并限制读取频率；Linux/macOS 原生窗口提供 SDL 系统偏好，离屏或无原生偏好提供者时采用回退来源。未知偏好回落浅色。

`ThemeBinding` 在 UI 线程把解析结果同时交给绘制器和元素树默认值来源。明暗改变时刷新布局、模板表达式和绘制缓存；模板定义未改变时保留现有视觉树。未改变的具体配色实例不会反复清除 retained 缓存。

`FluentTheme.GetDefault()` 为元素的 `Theme` 属性提供对应 `ControlTheme`，沿 CLR 类型链选择最具体的已注册类型。普通控件、纯 C# 子类和 `.ui` 生成子类使用同一机制；ToggleButton、PasswordBox、NumericUpDown、MenuItem、下拉按钮和表格列标题等具有独立外观契约的类型单独注册。

控件尺寸、颜色和交互状态主要定义在主题包的 `.eustyle` 中；字号、通用前景和焦点参数由应用主题默认值提供。常见 Fluent 尺寸包括字号 14、按钮及输入控件最小高度 32、按钮内边距 12/5、复选标记 20、滑块 22、进度条高度 3。显式属性和应用样式可覆盖这些值。

## 控件模板覆盖

| 控件范围 | 主题实现 |
|---|---|
| Button、ToggleButton | Border 和 ContentPresenter 组成的编译模板 |
| CheckBox、RadioButton、ToggleSwitch | 编译模板、文字呈现与主题包中的标记视觉元素 |
| TextBox、PasswordBox、NumericUpDown | 输入表面模板、TextPresenter 及主题内的输入装饰和数值箭头 |
| Slider、ProgressBar | 编译模板创建轨道、滑块和进度视觉元素；控件保留值和交互状态 |
| ComboBox、DatePicker | 编译模板创建按钮、列表或编辑器、日历和浮层；控件通过命名部件接入行为 |
| Calendar、TreeViewItem、TabControl | 主题视觉元素负责日期格、树标题和页签标题；布局所需尺寸为 StyledProperty |
| DataGrid | 模板安排列标题和滚动区域；核心保留列、数据、选择、排序、编辑和行虚拟化 |
| 列表、树、滚动视口和浮层 | 主题模板通过 ItemsPresenter 投影行为内容，主题负责选中表面、滚动条、浮层边框和阴影 |

Popup 阴影通过通用 `IElementDrawingContext.DrawShadow` 绘制高斯模糊圆角矩形，模糊半径采用 CSS 的两倍标准差语义。默认阴影向下偏移 4 个逻辑单位，标准差为 4，绘制范围覆盖偏移后 3 倍标准差的羽化区域；桌面透明窗口和窗口内浮层使用同一阴影。GPU 直接绘制模糊形状，软件渲染复用模糊掩码，均不再叠加实色矩形模拟阴影。

交互控件的 Runtime 实现不绘制主题外观。复杂的形状或文字定位可以写在主题包的 C# 视觉元素中，由 `.ui` 模板创建。`ElementPainter` 只遍历视觉树、执行通用绘图、文本、裁剪、焦点及浮层调度，不按 Button、CheckBox 等控件类型分派皮肤。

## 共享类型化资源

主题包可公开静态 `ThemeResource<T>` 字段，在 `.eustyle` 通过直接字段引用复用：

```scss
@using EclipseUI.Elements;
@using EclipseUI.Themes.Fluent;

Button.primary {
    Background: resource(FluentResources.ControlBackground);
}
```

资源按接收属性值的元素解析，能够响应亮暗、状态和主题变化。编译器验证公开静态字段及目标属性值类型，并生成直接调用，不执行资源初始化器，不使用运行时反射或字符串注册表。同模块控件主题仍可使用 `Theme: resource(MyButtonTheme)`。这些资源不是通用动态资源字典。

## 离屏与验证

离屏使用同一主题和测量链路：

```csharp
var painter = new ElementPainter(FluentTheme.Light, textPainter);
painter.Prepare(root);
root.Measure(size);
root.Arrange(new Rect(Point.Zero, size));
var frame = painter.BuildFrame(root, size);
```

相关回归覆盖模板替换、借入内容保留、部件事件、选择和编辑、输入与焦点、亮暗切换、浮层、动画和 retained 绘制。结构回归检查核心与平台不引用 Fluent、交互控件不包含主题绘制、绘制器不包含控件皮肤分发。

```powershell
dotnet build EclipseUI.slnx
dotnet test EclipseUI.slnx --no-build --no-restore -m:1
dotnet run --project samples/Gallery.Desktop -- --cpu --theme light --shot out/fluent-gallery-light.png
dotnet run --project samples/Gallery.Desktop -- --cpu --theme dark --shot out/fluent-gallery-dark.png
```

[早期验证报告](./reports/fluent-theme-2026-11-14.md)和 [WinUI 外观对照报告](./reports/fluent-winui-fidelity-2026-11-14.md)保留当时的测试与截图记录，不代表本轮测试结果或逐像素 WinUI 一致性。当前未提供系统材质、模糊、高对比专用配色或系统强调色自动导入。
