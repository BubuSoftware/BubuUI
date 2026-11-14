# EclipseUI

模板引擎优先的自绘 UI 框架。`.ui` 和纯 C# 是相同 UI 类型的两种编写方式：**根标签决定生成类基类，生成对象本身就是 UI 实例。**

当前规范见 [spec](./spec/README.md)，统一实施与验证见 [design/21](./design/21-root-type-unification.md)。

## 类型与使用

```text
UiObject                   初始化、更新调度、资源清理
├─ Element                 可嵌入控件；布局、属性层、绘制和输入
│  ├─ StackPanel / Grid / ScrollViewer
│  └─ Button / TextBlock / CheckBox
└─ Window                  窗口；Content 为 Element
```

`Counter.ui`：

```xml
@namespace Demo
@using EclipseUI.Elements

<StackPanel Gap="8">
    <TextBlock>Count: {count}</TextBlock>
    <Button OnClick={() => count++}>+1</Button>
</StackPanel>

@code { public int count; }
```

生成 `Counter : StackPanel`。纯 C# 直接使用：

```csharp
panel.Children.Add(new Counter());
```

纯 C# 控件也可作为 `.ui` 标签或根基类；反过来 C# 可以继承 `.ui` 生成类型。属性、事件、插槽及内容类型规则一致。

`MainWindow.ui`：

```xml
@namespace Demo
@using EclipseUI.Elements

<Window Title="Demo" Width="1000" Height="760">
    <Counter/>
</Window>
```

生成 `MainWindow : Window`，应用设置 `MainWindow = new MainWindow()`。Window 非 Element，不能放入控件内容；这由普通内容类型检查决定，不依赖窗口专用模板分支。

宿主和内容集合自动 EnsureInitialized；如需提前读取 Ref，可显式调用它。初始化晚于对象初始化器。StyledProperty 的调用方 local 值优先于根模板默认值。

## 开发与验证

```powershell
dotnet build EclipseUI.slnx
dotnet test EclipseUI.slnx --no-build -m:1

# 桌面画廊
dotnet run --project samples/Gallery.Desktop

# 离屏画廊与交互验证
dotnet run --project sandbox/GalleryCheck -- --cpu --selfcheck --shot out/gallery.png
```

所有样例和测试使用统一 UiObject 机制。旧组件包装、第二所有权树、来源枚举和窗口模板兼容层均已移除。

## Linux 桌面

`Gallery.Desktop` 自动选择 Windows / Linux / macOS 平台。Linux 使用 SDL3 3.2+ 创建 X11 或 Wayland 窗口，Vello 通过 Vulkan 上屏；需安装 `libSDL3.so.0`、Vulkan loader/驱动和字体（建议 DejaVu Sans、Noto CJK、Noto Color Emoji）。

```bash
dotnet run --project samples/Gallery.Desktop
SDL_VIDEODRIVER=wayland dotnet run --project samples/Gallery.Desktop
SDL_VIDEODRIVER=x11 dotnet run --project samples/Gallery.Desktop

# 离屏截图不需要显示服务器或 SDL3
dotnet run --project samples/Gallery.Desktop -- --cpu --shot out/gallery-linux.png

dotnet publish samples/Gallery.Desktop -c Release -r linux-x64 --self-contained true
```

自建应用通过 `.UseLinux().UseVello().UseHarfBuzzText()` 注册平台。窗口、DPI 缩放、输入/输入法、剪贴板及验证范围见 [Linux 平台说明](./design/26-linux-platform.md)。已在 WSLg 验证 X11 / Wayland 原生窗口、队列输入与缩放；仓库提供 Linux X11 原生窗口 CI。旧 SDL 配合缺少输入法协议的 Wayland 环境会回退基本键盘输入，完整 IME 可选择 X11；真实输入法候选交互仍需验收。

## macOS 桌面

macOS 使用 SDL3 Cocoa 窗口与 Vello Metal 上屏，支持 Retina、Command 快捷键、Option 词导航、中文/emoji 字体回退、输入法和系统剪贴板。

```bash
brew install sdl3 harfbuzz
dotnet run --project samples/Gallery.Desktop
dotnet run --project samples/Gallery.Desktop -- --cpu --shot out/gallery-macos.png
dotnet publish samples/Gallery.Desktop -c Release -r osx-arm64 --self-contained true
```

自建应用使用 `.UseMacOS().UseVello().UseHarfBuzzText()`；从进程主线程同步调用 `Start(args)`。Intel 发布使用 `osx-x64`。依赖、平台契约与验证范围见 [macOS 平台说明](./design/27-macos-platform.md)。

## 当前能力

- 保留静态树、编译期动态槽和 if/foreach/switch 块；普通 CLR 状态＋显式失效，无依赖追踪或运行时通用树 diff。
- 单一符号目录；公开可写 CLR 属性、StyledProperty、路由事件、委托、附加属性、默认内容及具名槽。
- 双向绑定按 StyledProperty 或匹配的 PChanged 回调能力选择，与 ui/C# 来源无关。
- Class/Style 和主题；模板默认值与 local 覆盖；声明式自定义样式属性和字面量转换器。
- Measure/Arrange、输入/焦点/路由、HarfBuzz 文本与逐字素字体回退（中文/彩色 emoji）、Vello CPU/GPU、保留显示列表及 damage/跳帧。
- UI 同步上下文与后台安全失效；构建失败临时作用域清理，真实内容树资源释放。
- LSP 诊断、补全、源映射与符号导航，共用目录和生成代码。

## 控件库

当前目录有 42 个公开标签（含 Window）。除原有文本、按钮和布局控件外，已提供 TextBox/PasswordBox、ListBox/ComboBox、开关与数值输入、浮层和菜单、TabControl、TreeView、DataGrid、DatePicker/Calendar、图像与形状，以及 Canvas/DockPanel。Grid 支持跨行跨列，滚动条可拖动，虚拟列表支持变高和可选容器复用。

完整行为、使用示例及能力边界见 [控件库契约](./design/24-control-library.md)。Gallery 第六区展示新增控件；也可独立验证并生成深浅主题截图：

```powershell
dotnet run --project sandbox/ControlCheck -- out/controls.png
dotnet run --project sandbox/ControlNativeCheck
```

## 边界

泛型模板标签暂不支持。原生窗口由平台层创建，运行时不依赖具体绘制后端。AOT 发布仍受原生渲染依赖限制。完整性能预算及真正 GPU 局部光栅化按后续路线推进。

当前环境有一项已在重构前代码复现的精确像素 golden 差异；未改基线掩盖。详细命令、测试数和隔离证据见 [验证报告](./design/reports/root-type-unification-2026-11-13.md)。

## 文档与目录

| 路径 | 内容 |
|---|---|
| [spec](./spec/README.md) | 当前语言、生成、更新与元数据契约 |
| [design](./design/README.md) | 架构、实施计划与报告 |
| src | Runtime、Compiler、Rendering、平台与文本模块 |
| samples | Gallery 与桌面入口 |
| sandbox | 渲染和交互验证壳 |
| tests | 编译器、无头、IDE、渲染回归 |
| [ide](./ide/README.md) | Visual Studio / VS Code / Rider 与 LSP 工具链 |

旧版状态日志与原始决策在 [历史快照](./design/history/2026-11-13-before-legacy-removal/README.md)，不作为当前 API 或兼容承诺。
