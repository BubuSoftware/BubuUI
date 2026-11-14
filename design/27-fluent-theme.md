# 27 · Fluent 默认皮肤

2026-11-14。已实施。设计参考 [Fluent 2 设计令牌](https://fluent2.microsoft.design/design-tokens)及[语义颜色](https://fluent2.microsoft.design/color-tokens)；当前实现为 EclipseUI 的 Fluent 风格桌面皮肤。

后续外观修订已改以 Windows 11 / WinUI 控件资源为基准，新增渐变边框、输入底线和可配置控件几何。当前实现、验证范围、仍有差距及底层改造方案见[WinUI 对齐报告](./reports/fluent-winui-fidelity-2026-11-14.md)。下文的初版验证数字保留为历史记录，不能作为逐像素一致的证明。

## 接入

`EclipseUI.Themes.Fluent` 是独立可打包项目，只依赖 Rendering（及其 Runtime 依赖），不引用平台或渲染后端。Windows/Linux/macOS 桌面宿主在窗口和应用都未指定主题时默认采用 `FluentTheme.System`。

```csharp
using EclipseUI.Themes.Fluent;

builder.UseFluentTheme(); // System
builder.UseFluentTheme(ThemeVariant.Light);
builder.UseFluentTheme(ThemeVariant.Dark);

// Application 子类内：
SetTheme(FluentTheme.System);
SetTheme(FluentTheme.Light);
SetTheme(FluentTheme.Dark);
```

窗口初始优先级：Window.Theme > Application.DefaultTheme > FluentTheme.System。显式传入原有 ElementTheme 或自定义主题继续生效。运行期 SetTheme 替换当前来源；换回 System 后恢复跟随。

Windows 读取 AppsUseLightTheme，每秒至多读取一次。Linux/macOS 原生窗口通过 [SDL_GetSystemTheme](https://wiki.libsdl.org/SDL3/SDL_GetSystemTheme) 读取偏好；仅真实 LinuxWindow/MacOSWindow 查询已初始化的 SDL，模拟窗口可注入 ISystemThemeProvider，无需安装 SDL。未知偏好回落浅色；无窗口的 Linux 离屏路径可使用 GTK_THEME 的 dark 标记或显式指定明暗。

ThemeBinding 在 UI 线程解析来源，系统偏好改变时替换具体不可变色板，清除 retained 段和 damage 历史。明暗未变时复用同一实例，空闲帧仍跳过上屏。没有后台线程、事件订阅或额外生命周期资源。

## 外观与控件

- 普通按钮使用中性背景和细边框；颜色令牌覆盖 normal、hover、pressed、disabled。应用的 Background/Foreground 类样式及 local 值继续优先生效。
- 复选框、单选框、开关、滑块、进度条使用语义强调色；禁用状态同时处理背景、边框和标记颜色。
- 输入框、密码框、数值输入及复合下拉使用控件背景/边框、次要文本色和自身焦点边框。
- 列表、树和页签使用独立选中背景/前景；列表和树有选中标记，页签有强调色底线。表格行复用列表容器，编辑单元格复用输入框。
- 菜单和树的普通行保持透明，悬停使用中性填充。命中内容子元素时最近的交互控件也获得悬停反馈，进入嵌套树行不会点亮所有祖先行。
- 浮层有不透明表面、边框、圆角和分层阴影；阴影范围计入 damage 膨胀，关闭后重绘旧范围。普通结构性 StackPanel 保持透明，显式设置的卡片背景仍绘制。
- 使用现有 focus-visible/focus-within 契约；键盘外圈和输入框自身边框保持分工，控件自身焦点边框使用有效圆角。

共享默认字号 14 DIP，普通控件圆角 4 DIP，面板圆角 8 DIP；按钮和输入控件默认最小高度 32 DIP，按钮横向/纵向内边距 12/5 DIP。字体文件和回退仍由文本管线选择。布局面板、图像和形状保留其布局语义与调用方设置的填充/描边。

Button 新增 HorizontalPadding/VerticalPadding StyledProperty 和 CLR 属性，Measure/Arrange 共用它们。该 API 变化已同步 BuiltInSurface.golden.txt，42 个标签不变，属性项从 715 增至 721。

## 默认值层与离屏

优先级为 local > template > class > inherited > theme default > property default。主题默认值提供者沿真实 Parent 树查找，不写入其他属性层，不依赖全局样式注册表；新增、迁移和替换内容使用所在树的主题。更换提供者使布局和绘制失效，不产生属性写入通知。

```csharp
var painter = new ElementPainter(FluentTheme.Light, textPainter);
painter.Prepare(root); // 在 Measure/Arrange 之前
root.Measure(size);
root.Arrange(new Rect(Point.Zero, size));
var frame = painter.BuildFrame(root, size);
```

底层 ElementPainter 未指定主题时仍提供原来的 ElementTheme；Fluent 的自动默认入口是桌面宿主。皮肤没有启用尚未实施的 design/23 选择器或资源重设计，也不包含系统材质、模糊及高对比专用色板。

## 验证

```powershell
dotnet build EclipseUI.slnx
dotnet test EclipseUI.slnx --no-build --no-restore -m:1 -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet run --project sandbox/FluentCheck -- out/fluent
dotnet run --project samples/Gallery.Desktop -- --cpu --theme light --shot out/fluent-gallery-light.png
dotnet run --project samples/Gallery.Desktop -- --cpu --theme dark --shot out/fluent-gallery-dark.png
```

FluentThemeTests 验证默认值优先级、动态子项、树间隔离、系统变化、显式主题、retained/damage、指针状态、内容命中、选中与键盘焦点、浮层关闭及双色资源。Linux WindowHostTests 通过可注入偏好验证真实帧循环中的跟随与固定主题。

FluentCheck 为完整控件目录生成深浅色、选中/下拉、键盘焦点、悬停、按下和禁用共 12 张截图，路径为 out/fluent/；图片经图像查看工具核对文字、表面、选中项、菜单、下拉及布局。

最终构建、1605 项测试及 Windows 原生 GPU 验证结果见[验证报告](./reports/fluent-theme-2026-11-14.md)。
