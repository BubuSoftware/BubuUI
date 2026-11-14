# 24. 控件库与交互契约

2026-11-14。实现范围来自本次控件缺口盘点；本页描述当前可用的基础行为。类型和属性继续由真实程序集扫描进入模板目录，不维护第二份控件注册表。

## 控件清单

目录包含 42 个公开标签（含 Window），相对原有 10 个标签新增 32 个。

| 类别 | 类型 | 当前行为 |
|---|---|---|
| 文本编辑 | TextBox、PasswordBox | 文字输入、指针选区、Shift 选择、按字素移动/删除、复制/剪切/粘贴、100 步撤销重做、只读、MaxLength、占位文字、多行、组合输入与光标滚动/闪烁；密码禁止复制/剪切且绘制时遮盖正文和组合文字 |
| 开关与选择 | CheckBox、RadioButton、ToggleButton、ToggleSwitch | 三态复选框、命名/同父单选组、单选组方向键导航、开关与按钮状态 |
| 数值 | Slider、ProgressBar、NumericUpDown | 范围钳制、指针拖动、刻度吸附、键盘步进；确定/不确定进度；数字解析、上下按钮/方向键、滚轮、无效编辑回退 |
| 集合 | ItemsControl、ListBox、ListBoxItem | Items/ItemsSource、条目工厂、集合变更通知、容器保留、单选/多选/扩展选择、Shift 范围/Ctrl 切换、方向键/Home/End/Ctrl+A、选中项进入视口 |
| 下拉 | ComboBox | 共享 ListBox 选择模型，真实 Popup、方向键选择、Alt+Down/Enter/Escape、指针选择、关闭后恢复焦点 |
| 浮层 | Popup、ToolTip、ContextMenu | 顶/底/左/右/指针定位，视口内钳制，外部点击/Escape 关闭；浮层绘制和命中晚于普通内容、不受中间祖先裁剪影响；提示绑定目标进入/离开，右键菜单绑定目标 |
| 菜单 | Menu、MenuItem | 水平/垂直菜单、方向键遍历、具名 Submenu 插槽、命令 Click、可勾选项、叶项执行后关闭祖先菜单 |
| 页面与树 | TabControl、TabItem、TreeView、TreeViewItem | 页签头部点击、左右键/Ctrl+Tab、隐藏非选中页面；树节点展开/折叠、选择、上下/左右/Home/End、源数据选择器和环检测 |
| 表格 | DataGrid、DataGridColumn | 列头/宽度、属性路径或 getter/setter、定制单元格、稳定类型排序、行选择、可编辑单元格、提交/拒绝事件、集合通知、虚拟化与容器复用 |
| 日期 | Calendar、DatePicker | 公历月视图、有效日期范围、闰年、月导航、键盘日期导航、日期输入校验、弹出日历；Enter/点击确认，导航期间保留浮层 |
| 内容与图形 | ContentControl、Border、Image、Rectangle、Ellipse、Line、Path | 单内容、边框/内边距/圆角、RGBA 图像及缩放/裁剪、形状填充和描边 |
| 布局 | StackPanel、WrapPanel、Grid、Canvas、DockPanel | 水平/垂直堆叠，流式换行，Auto/固定/星号与跨行/跨列，绝对定位，边缘停靠/末项填充 |
| 滚动与虚拟化 | ScrollViewer、VirtualizingStackPanel | 滚动条宽命中区拖动、轨道点击及按住连滚、捕获取消；PageUp/Down、Ctrl+Home/End、Shift 横滚；等高/变高窗口、精确行高提供器或测量估计、可选容器复用 |
| 既有展示 | TextBlock、Button、Window | 原契约继续可用；Button 增加 Content 插槽，支持图标和任意元素内容 |

DataGridColumn 是列描述对象，通过 C# 或表达式传入 Columns；它不是 UI 元素标签。TextRun 和运行时块仍不作为公开模板标签。

## 输入、焦点与浮层

- Element 增加 TextInput、TextComposition、PointerEntered/Exited、PreviewKeyDown、PreviewPointerPressed 路由事件。Preview 事件沿祖先隧道传播，可在文本编辑器消费输入前处理表格选择或日期快捷键。
- KeyEventArgs 携带 Shift/Control/Alt 和重复标记；KeyUp 与字符输入均从平台转发。普通 Tab/Shift+Tab 遍历有效可见的 IsTabStop，Ctrl+Tab 可以由页签处理。
- IsVisible=false 的元素测量为零，退出绘制、命中与焦点遍历。关闭 Popup 的整棵内容子树也退出交互。
- Popup 内容始终保留在同一实际内容树中。ElementPainter 延后绘制浮层，InputRouter 优先命中浮层；没有额外的所有权树。嵌套浮层中的点击不会关闭其祖先。
- Popup 保存打开前的焦点，关闭时恢复；Windows 宿主先让 Escape 关闭浮层，再执行 CloseOnEscape。
- Win32 转发完整 UTF-16 字符（包含代理对）、键抬起和 IMM32 组合事件，设置组合/候选窗口位置。运行时只在组合提交时修改 Text。
- IClipboard 可替换。无头默认内存实现，Windows 宿主安装 Unicode 系统剪贴板。可用 FrameAnimations.Advance 在无头环境推进光标和不确定进度动画。

保持既有捕获契约：指针释放交给按下时捕获的元素，按钮仍按该既有契约触发；右键不会触发左键 Click。按钮内容命中时，其祖先按钮获得正确按下状态。

## 使用方式

完整可编译示例见 [ControlCatalog.ui](../samples/Gallery/ControlCatalog.ui)。

```xml
<TextBox @bind-Text={query} Placeholder="Search" />
<Slider @bind-Value={amount} Minimum="0" Maximum="100" />
<ListBox ItemsSource={rows} SelectionMode="Extended" />
<Button>
    <Slot Name="Content">
        <StackPanel Orientation="Horizontal" Gap="8">
            <Ellipse Width="16" Height="16" Fill="#45C491" />
            <TextBlock>Save</TextBlock>
        </StackPanel>
    </Slot>
</Button>
```

Button 的文字内容继续写入 Label，任意元素内容用 Content 插槽。ContentControl/Border/TabItem/Popup 直接接受一个子元素。ContextMenu 的直接子元素写入 Items；复合控件内部元素不需要由调用方手工接线。

## 绘制扩展与样式

Element.Render(IElementDrawingContext) 提供局部坐标的绘制入口；运行时接口不引用 Rendering 或平台包。上下文支持矩形、圆角、椭圆、路径、线、文字、图像及裁剪，并提供已解析主题/样式的前景、背景、边框、强调色。自定义绘制与内置绘制共用 retained 段、失效版本和 damage。

新控件可重写 UsesDefaultDrawing 以替换继承控件的默认外观。PushClip/PopClip 必须平衡；自身绘制默认由 GetDrawingClip 限制在布局尺寸内，普通子内容由 GetContentClip 声明区域，绘制/命中/导航焦点共用。焦点外圈和浮层有独立的绘制范围。第三方控件无需修改 ElementPainter 的类型 switch；区域与验证见 [布局契约](./05-layout-rendering-input.md#控件绘制与内容区域)。

按钮、选择控件、内容控件、菜单、树、表格、页签与日历的溢出回归、极小/零尺寸及原生宿主验证见[控件溢出报告](./reports/control-overflow-2026-11-14.md)。

UiColor 支持模板字面量 #RRGGBB/#RRGGBBAA。StyleBuilder 通过 UiStyleOwner 显式指定内建字段来源，避免 Border.BackgroundProperty 等正常同名字段干扰 Visual 的样式发射；普通第三方样式属性仍走原有 UiStyleProperty opt-in。

## 混合文字与字体回退

HarfBuzzTextPainter 默认按 Unicode 字素选择字体，保留 emoji 的 ZWJ/肤色/变体选择符序列，并把相邻同字体文字合并 shaping。Windows 默认使用微软雅黑补齐中文、Segoe UI Emoji 绘制彩色 emoji，主字体可覆盖的普通文字仍保持原有 kerning 与连字。测量与绘制复用相同字体分段、进距和行盒；TextBox 的高度、光标和组合文字使用实际字体度量。

`UseHarfBuzzText(options => options.FallbackFontPaths = paths)` 可指定回退字体路径；空列表显式关闭回退。`new HarfBuzzTextPainter(shaper)` 同样默认启用系统回退，因此 Gallery 的主窗口和离屏入口行为一致。回退字体按需加载并由 painter 释放，调用方传入的主 shaper 仍由调用方管理。

修复与可复现验证见 [中文和 emoji 输入显示报告](./reports/unicode-input-2026-11-14.md)。

## 虚拟化与资源

默认等高模式保持既有 API/生命周期。IsVariableHeight=true 时，未知行高先用探针估计，已测高度写入前缀和树，后续布局更新 Extent；ItemHeightProvider 可直接提供精确高度，不创建所有容器。

提供 ItemUpdater 后启用回收：离开视口的容器先从树摘除，再由更新器重新绑定索引，进入树时重新收到附加通知；未提供更新器的行按原契约释放。探针仍单独保留。Refresh、最终释放及异常清理覆盖探针、可见行与回收池；回收池上限 256。

源集合应在 UI 线程修改。普通对象的内部属性修改后可调用 RefreshItems；DataGrid 的列绑定可用显式 getter/setter 避免反射路径。集合变更订阅会在控件释放时解除。

## 能力边界

本次闭合盘点中的控件类型和上述基础交互；这些控件不等同于成熟商业控件库的全部扩展功能。

- 文本编辑目前使用显式换行，未提供自动折行编辑、富文本或完整双向文字编辑算法。
- DatePicker/Calendar 使用公历，默认格式 yyyy-MM-dd、星期日为一周起点。
- DataGrid 当前提供基础列、排序、选择、编辑和虚拟化；分组、冻结列、列拖动/缩放和 Excel 式范围编辑不在此契约中。
- Image 接受已解码 IRasterImage（Rendering.ImageHandle 实现该接口），不内置网络/文件解码和异步图片缓存。
- Path 支持绝对/相对 M/L/H/V/C/Q/Z，不支持完整 SVG 文档、弧线命令和 SVG 样式。
- 原生字符、按键和 GPU 表面已有自动化验证；候选窗口与真实中英文输入法切换仍需要 Windows 人工验收。

## 验证

```powershell
dotnet build EclipseUI.slnx
dotnet test EclipseUI.slnx --no-build -m:1
dotnet run --project sandbox/ControlCheck -- out/controls.png
dotnet run --project sandbox/ControlNativeCheck
```

ControlCheck 使用真实 .ui 编译产物验证双向绑定、撤销、选择、排序及浮层，并生成深浅主题截图；ControlNativeCheck 验证真实 Win32 WM_CHAR（含中文和代理对）、KeyUp、编辑、Escape 关闭顺序与 GPU 上屏。

结果及可复现证据见 [验证报告](./reports/controls-2026-11-14.md)。

## 焦点提示（2026-11-14）

控件使用 focus / focus-within / focus-visible 三种状态。普通按钮在键盘交互时显示外圈；文本输入使用自身边框；DatePicker / ComboBox 由外壳统一提示；列表、树、表格和页签提示当前行或头部。输入来源、样式配置、绘制与缓存契约见 [design/25](./25-focus-visuals.md)。

## 滚动条交互修复（2026-11-14）

ScrollViewer 的绘制、命中和拖动统一读取 TryGetScrollBarGeometry。滑块沿滚动方向的范围对应整个 14 DIP 宽的命中条带，6 DIP 的绘制宽度不再限制拖动入口。支持拖出视口/窗口后继续拖动、越界钳制、点击轨道翻页，以及按住轨道 400 ms 后每 60 ms 继续翻页；滑块到达鼠标位置后停止，鼠标移出轨道暂停、返回后继续。

重复滚动使用共享帧时钟。松手、取消捕获及摘除/释放结束交互；Win32 的 WM_CAPTURECHANGED / WM_CANCELMODE 接入 InputRouter.CancelPointerCapture。正常释放先发送 PointerReleased，再释放原生捕获，确保普通按钮的点击仍然有效。验证见 [滚动条修复报告](./reports/scrollbar-2026-11-14.md)。
