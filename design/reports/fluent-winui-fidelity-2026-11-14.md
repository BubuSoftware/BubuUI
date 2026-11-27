# Fluent / WinUI 外观对齐与底层差距

2026-11-14。目标是 Windows 11 的 WinUI 桌面 Fluent 控件。本轮已实现静态外观修正，但**尚未达到经原生截图差分验证的逐像素一致**。

## 参考基准

上一版采用了 Fluent Web 风格的强调色，再套用框架原有的控件绘制，因而尺寸、轮廓与 WinUI 差异明显。本轮直接核对微软的颜色和控件资源：

- [颜色、透明度与边框资源](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/Common_themeresources_any.xaml)
- [Button](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/Button_themeresources.xaml)、[TextBox](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/TextBox_themeresources.xaml)
- [CheckBox](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/CheckBox_themeresources.xaml)、[RadioButton](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/RadioButton_themeresources.xaml)
- [ToggleSwitch](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/ToggleSwitch_themeresources.xaml)、[Slider](https://github.com/microsoft/microsoft-ui-xaml/blob/9402930972f25cfcad82abc9f82ecab29936f404/controls/dev/CommonStyles/Slider_themeresources.xaml)

参考提交固定为 `9402930972f25cfcad82abc9f82ecab29936f404`，七份下载资源的哈希均与该提交核对。这是源码参考，并不等于已锁定某个 Windows App SDK 发布包的运行时像素基线。

## 本轮实际修改

| 部位 | 修改 |
|---|---|
| 色板 | 使用 WinUI 的半透明中性填充、文本、强描边与状态色；浅/深色默认强调色为固定的 `#005FB8` / `#60CDFF`，仍未读取系统个性化强调色 |
| Button / ToggleButton | 4 DIP 圆角、32 DIP 最小高度；浅色普通按钮及选中按钮使用底部 3 DIP 渐变，深色普通按钮使用顶部 3 DIP 渐变；按下/禁用取消立体边框；普通按钮按下采用次级文字色 |
| TextBox / PasswordBox / NumericUpDown | 普通状态 1 DIP 底线、聚焦 2 DIP 强调底线，底线按圆角裁剪；鼠标聚焦和键盘聚焦都生效；输入背景单独处理；文字、光标、选择和点击命中共用内边距 |
| CheckBox / RadioButton | 从跟随字号的约 15 DIP 改为 20 DIP 图形；32 DIP 最小高度；区分强描边与按钮弱描边；RadioButton 选中改为实心强调色外圆与反色内点 |
| ToggleSwitch | 40×20 DIP 轨道，关闭时空心轮廓，普通旋钮 12 DIP、悬停/按下 14 DIP；区分开/关状态旋钮色 |
| Slider | 22 DIP 外圆、内强调圆、4 DIP 轨道及已完成段；指针换算与绘制共用轨道端点，横向与纵向同时更新 |
| ProgressBar | 默认 3 DIP 高度，仍支持显式高度覆盖 |
| ComboBox / 列表 / 页签 | 下拉文字左对齐，普通滚动容器不再自动画白色卡片，未选中页签保持透明；选中项使用中性填充和强调标记 |
| 键盘焦点 | 增加对比色内圈，与现有外圈共同绘制；内圈位于已有 damage 范围内 |

新增 `Button.LabelAlignment`、`CheckBox.GlyphSize`、`Slider.ThumbSize`、`TextBox.HorizontalPadding/VerticalPadding`。这些值走已有主题默认值层，应用可覆盖；换回其他主题后未被显式设置的值恢复。内置 API 快照从 721 项属性更新为 734 项，标签仍为 42 个。

`IElementDrawingContext.ControlSurface` / `HasLayeredChrome` 和 `ElementTheme` 的语义令牌是本轮最小底层扩展：主题可选择输入底线和渐变边框，原有上下文接口有默认实现。圆角裁剪与渐变使用已有显示列表命令，因此 CPU/GPU 后端不需要新增协议。

## 仍存在的限制与底层改造方案

下表区分真正缺失的框架能力与需要继续实现的控件行为。不能仅靠修改主题色板完成的部分，不等于永久无法实现。

| 问题与可见差异 | 当前代码依据 | 建议的底层修改 | 验收方式 |
|---|---|---|---|
| 字体、字重、行高和字缘不能与 WinUI 完全一致 | `Runtime/TextBlock.cs` 的测量接口只接收文本和字号；`Text.Shaping/TextAppBuilderExtensions.cs` 默认加载静态 Segoe UI；HarfBuzz/Vello 不等同于 DirectWrite 的 hinting/栅格化 | 引入 `FontDescriptor`，包含 family、weight、stretch、style、variation axes、line height；贯穿测量、shaping cache key、DrawGlyphs；Windows 增加 DirectWrite 字体解析和可选原生字形栅格化路径，其他平台保留 HarfBuzz | 同字体文件、字号、DPI、系统文字设置下比较基线、宽度和字缘；单独报告文字区域差分 |
| Mica / Acrylic 目前只能看到纯色表面 | `Rendering/DrawCommand.cs` 没有背景采样/模糊命令；`Platform.Windows/Win32Window.cs` 没有系统 backdrop 接入 | 增加 `Window.Backdrop` 与平台能力接口，Windows 接入系统 backdrop 并允许透明客户区；浮层 acrylic 增加 backdrop capture、blur、tint、noise 合成；定义失焦、低性能和不支持平台的纯色回退 | 背后窗口/壁纸变化、失焦、拖动、跨屏时材料正确更新，无旧帧残留 |
| 弹层阴影是三层扩散实色，缺少真实软阴影和 elevation | `ElementPainter.PaintPopup` 通过三个扩大矩形模拟阴影 | 增加 `DrawShadow(shape, offset, sigma, spread, color)`，CPU/GPU 共用阴影语义；缓存模糊遮罩；damage 包含 blur kernel 外扩及旧位置 | 浅深色背景检查羽化，弹层关闭/移动后没有残影；CPU/GPU 输出对照 |
| 动画仍为状态瞬切，开关不滑动、勾选线不绘制过渡、滑块内圆无缓动 | `Runtime/ValueControls.cs` 根据布尔状态直接绘图；`FrameAnimations.cs` 只有逐帧接口 | 增加可中断的状态过渡时钟与 easing，将颜色、几何参数纳入 visual state；只让过渡中的控件请求帧，响应系统关闭动画设置 | 按固定时间戳取帧比较；快速反向切换连续；动画结束后恢复空闲帧跳过 |
| DatePicker、NumericUpDown、TabControl、DataGrid 的结构仍不同 | `DatePicker.cs` 用 TextBox + Button 拼日期；`ValueControls.cs` 为上下箭头；`NavigationControls.cs` 硬编码页签头；`DataGrid.cs` 用按钮作表头、常驻 TextBox 作编辑格 | 把行为和外观分开，增加主题控件模板/命名部件/VisualState；分别提供日期分段或 CalendarDatePicker 模板、NumberBox 按钮布局、TabView 和 Pivot 的明确类型、表格 display/edit 双模板。DataGrid 需另选官方 Toolkit 等参考，不能假定存在 WinUI 原生同名控件 | 模板替换后键盘、焦点恢复、无障碍树、虚拟化和编辑提交一致；再逐控件截图比对 |
| 125% / 150% 缩放仍可能出现半像素边缘、线宽与 WinUI 不同 | `ElementPainter.BuildCore` 对整棵树施加缩放；没有贯穿布局/绘图/命中的设备像素对齐策略 | 增加 `LayoutRounding` / `PixelSnapPolicy`；布局得到 device-pixel 边界后再换回 DIP；定义细线、文本基线和曲线分别如何取整，不能只四舍五入绘制坐标 | 在四档 DPI 上同时核对视觉边界与点击命中，移动跨 DPI 屏幕后重新布局 |
| 强调色、高对比和减弱动态效果尚未进入主题来源 | `ThemeBinding` 只解析明暗，FluentTheme 使用共享固定色板 | 扩展平台 `SystemAppearance`（accent palette、contrast palette、animations enabled）；主题实例按快照缓存，偏好更新触发布局/绘制失效；高对比使用系统语义色而非普通配色反相 | 注入偏好做回归，真实 Windows 更换强调色和高对比时立即刷新 |
| 字形图标、滚动条和子部件交互不完全一致 | `ControlIcons.cs` 用折线绘制箭头；CheckBox 的勾为自绘路径；`ElementPainter.PaintScrollBar` 与 Runtime 滚动几何没有完整的窄/宽状态模板 | 提供主题图标资源及填充/线帽/连接规则；滚动条轨道、按钮、拇指成为可分别命中和过渡的部件；主题统一它们的几何和颜色 | 按图标尺寸比较轮廓；滚动条 hover/drag/capture cancel 的视觉和命中同步 |
| 原生标题栏和客户区视觉不是一个完整的 Fluent 窗口 | Win32 宿主创建通用窗口，主题主要覆盖客户区 | 通过窗口装饰接口统一系统圆角、明暗标题栏、caption button 和拖动区；自定义标题栏需保留系统菜单、Snap Layout、键盘和无障碍语义 | 多显示器、最大化/还原、Snap、系统缩放和高对比下检查 |

建议顺序：先建立原生对照基线并补齐字体与设备像素对齐；再拆分控件模板/状态；最后实现阴影、材质与动画。不要继续把日期选择器或表格的结构差异压进一组通用颜色令牌。

## 验证范围与复现

`sandbox/FluentCheck/StateSheet.cs` 用实际控件、InputRouter 和 FocusManager 生成 14 行 × 5 状态的状态图，覆盖 rest、hover、pressed、disabled、keyboard focus；ProgressBar 无交互，相关列仅用于排版对照。同时生成浅/深色 × 100%/125%/150%/200% 共 8 张图，加上原有目录/浮层截图 12 张，共 20 张。

```powershell
dotnet build BubuUI.slnx --no-restore --nologo -v quiet
dotnet test tests/BubuUI.Rendering.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet test tests/BubuUI.Headless.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet test tests/BubuUI.Compiler.Tests --no-build --no-restore -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet run --project sandbox/FluentCheck --no-restore -- out/fluent-winui
```

输出位置：`out/fluent-winui/states-light-100.png`、`states-dark-100.png`，其余 DPI 用 `125`、`150`、`200` 后缀。图片不是微软原生控件截图。

新增回归覆盖鼠标聚焦的圆角底线、禁用状态、尺寸默认值与显式覆盖、渐变边框状态，以及四档 DPI 下 retained 与 fresh 绘制像素一致。后者验证缓存正确性，**不代表 BubuUI 与 WinUI 像素一致**。

逐像素验收还需独立 WinUI reference harness：锁定 Windows/App SDK/字体版本/强调色，使用相同控件数据、状态与尺寸导出原生图，生成差分图并分别统计文字、几何、材质区域。目前没有这组原生基线，不能给出可信的相似度百分比。

最终验证结果：解决方案构建 0 警告、0 错误；Rendering 214 项通过，Headless 652 项通过、3 项 macOS 键盘测试跳过，Compiler 425 项通过。合计 1291 项通过、3 项跳过。本轮没有重跑全部 IDE/平台测试项目。

最后核对并修正深浅色边框渐变方向后，重新执行全部 21 项 FluentThemeTests，通过；重新构建 Gallery 和生成所有截图。上述 214 项 Rendering 全量结果在该方向修正前取得。

Windows 原生自检使用 `dotnet run --project sandbox/ControlNativeCheck --no-restore`，结果为 `text=A你, KeyUp=3, frames=95, backend=Vello (GPU surface)`。它验证原生输入和后端可用性，不能替代 WinUI 外观差分。另生成 `gallery-light.png` / `gallery-dark.png` 两张 1000×760 主窗口快照；与 20 张控件快照合计 22 张。

已检查浅色 100% 状态图、深色 150% 状态图及主窗口截图的布局和控件轮廓。Gallery 本身还有明确写入的 class / inline 自定义颜色、圆角和布局演示，主题不会覆盖这些显式值；这些演示外观不同不是渲染框架能力缺失。
