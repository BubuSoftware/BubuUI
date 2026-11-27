# EclipseUI

开发时修改 `.ui` / C# 的使用方式与限制见 [热重载](./design/hot-reload.md)。

生成 VS Code、Visual Studio、Rider 插件安装包：在 GitHub Actions 中运行 **Package IDE plugins**，
完成后下载 Artifacts；也可本地执行 `pwsh -File ./publish.ps1`。
详见 [IDE 插件打包说明](./ide/README.md#三个-ide-插件一键打包)。

模板引擎优先的自绘 UI 框架。`.ui` 和纯 C# 是相同 UI 类型的两种编写方式：**根标签决定生成类基类，生成对象本身就是 UI 实例。**

当前规范见 [spec](./spec/README.md)，统一实施与验证见 [design/21](./design/21-root-type-unification.md)。样式和 C# 属性过渡的用法见 [动画系统](./design/29-animation-system.md)。

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

## 应用窗口生命周期

Windows、Linux 和 macOS 共用应用窗口规则：关闭主窗口不会关闭其他应用窗口，最后一个窗口关闭后 `Start` 返回。UI 线程可调用 `application.ShowDialog(new SettingsWindow())` 同步打开独立窗口；该调用运行自己的消息循环，关闭后恢复仍打开的上层窗口。窗口、宿主、剪贴板及焦点由框架管理，无需应用自己创建平台窗口。

`application.Windows` 返回仍打开的应用窗口；`application.SetTheme(theme)` 更新所有存活窗口及后续窗口的默认主题；`application.Shutdown()` 显式关闭全部窗口。系统的退出命令同样作用于所有窗口。原生关闭时不在输入回调中同步销毁 UI，资源清理仍在各窗口消息循环结束后执行。

### Popup 模态框

`Popup.IsModal` 可用于主窗口或子窗口中的模态内容：

```xml
<Popup IsOpen="true" IsModal="true" HostMode="Window">
    <StackPanel Padding="20" Gap="12">
        <TextBlock>模态对话框</TextBlock>
        <TextBox Width="240" />
    </StackPanel>
</Popup>
```

模态打开时，指针、滚轮、键盘、文本与 IME 输入以及程序化焦点均限制在最上层模态框及其附属弹出内容；Tab 在此范围内循环，输入事件不会冒泡到父界面快捷键。支持嵌套模态及内部普通下拉菜单。外部点击、切换应用不会自动关闭模态框；Escape 默认先关闭内部下拉框，再关闭模态框，`StaysOpen="true"` 则保持模态框并吞掉 Escape。也可调用 `Popup.Close()`；关闭、移除或销毁后恢复有效焦点并清理附属弹出窗口。

`HostMode="Overlay"` 提供窗口内容内的模态输入边界；`Auto` 在原生模态或透明表面不可用时回退到 Overlay。`HostMode="Window"` 要求原生模态支持，不支持时抛出明确错误。原生模态使用独立无边框子窗口，普通下拉菜单仍使用原生 popup-menu。Windows 会禁用原生父窗口交互并在关闭时恢复原状态；Linux 使用 SDL 模态子窗口；macOS 使用 SDL/Cocoa 的 application-modal 会话，模态期间会限制同一应用的其他窗口，它不是 document-modal sheet。

## 开发与验证

仓库统一使用 .NET 11 SDK（`global.json`），运行时、平台、样例、测试和语言服务器目标均为 .NET 11。Roslyn 源生成器及其共享编译器组件保留 `netstandard2.0`，VS 进程内扩展保留 `net472`，以满足各自加载宿主的兼容要求；它们同样由 .NET 11 SDK 构建。

画廊采用共享 UI 加平台入口结构：`samples/Gallery` 是共享库，包含 `.ui`、交互逻辑和 `GalleryApp`；`samples/Gallery.Desktop` 提供 Windows / Linux / macOS 桌面入口（`net11.0`），`samples/Gallery.Android` 和 `samples/Gallery.iOS` 分别提供 Android、iOS 入口。调试时将对应平台的入口项目设为启动项目。

桌面入口和共享 UI 只需 .NET 11 SDK。解决方案的默认平台 `Any CPU` 构建桌面和 Android 项目，需准备 Android 工具链。选择解决方案平台 `iOS`（命令行追加 `-p:Platform=iOS`）会构建桌面和 iOS 项目。单独开发某个平台时，可直接构建或运行对应的入口项目。

```powershell
dotnet build EclipseUI.slnx
dotnet test EclipseUI.slnx --no-build -m:1

# 桌面画廊
dotnet run --project samples/Gallery.Desktop

# 离屏画廊与交互验证
dotnet run --project sandbox/GalleryCheck -- --cpu --selfcheck --shot out/gallery.png
```

所有样例和测试使用统一 UiObject 机制。旧组件包装、第二所有权树、来源枚举和窗口模板兼容层均已移除。

Windows 拖动窗口边框时会按最新客户区实时重新布局和上屏，文字及控件保持原有尺寸；验证方式见[交互缩放报告](./design/reports/live-resize-2026-11-14.md)。

## Linux 桌面

`Gallery` 自动选择 Windows / Linux / macOS 平台。Linux 使用 SDL3 3.2+ 创建 X11 或 Wayland 窗口，Vello 通过 Vulkan 上屏；需安装 `libSDL3.so.0`、Vulkan loader/驱动和字体（建议 DejaVu Sans、Noto CJK、Noto Color Emoji）。

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

## Android 实验宿主

Android arm64 样例使用与桌面相同的 Gallery 页面和 Fluent 控件模板。Activity 承载 EclipseView，按屏幕密度换算布局和触摸坐标，按 Android 分配给视图的实际像素尺寸进行 CPU 光栅化。支持单指点击、拖动、纵向列表滚动和主题切换。Activity 暂停时停止刷新，返回时恢复现有页面。

Android 与桌面共用 `AppBuilder`。带 `[Application]` 的 `MainApplication` 继承 `EclipseAndroidApplication<GalleryApp>`，基类在进程启动时执行 `AppBuilder.Configure<GalleryApp>().UseAndroid()`，经 `CustomizeAppBuilder` 配置后调用 `Build()`：

```csharp
protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    => builder
        .UseHarfBuzzText(options =>
        {
            options.FontPaths = ["/system/fonts/Roboto-Regular.ttf"];
            options.FallbackFontPaths = ["/system/fonts/NotoSansCJK-Regular.ttc", "/system/fonts/NotoColorEmoji.ttf"];
        })
        .UseFluentTheme();
```

可继续链式调用 `ConfigureServices` / `UseStyles`。共享 `GalleryApp` 在初始化回调中取得 `IActivityApplicationLifetime`，设置 `MainViewFactory`；桌面仍创建 `MainWindow`。`MainActivity` 只需继承 `EclipseActivity`，基类负责创建视图、暂停/恢复和释放。Activity 重建会创建新页面，应用与服务容器保留，当前应用主题继续生效。页面内容状态尚未自动保存。Android 的生命周期由系统驱动，不调用桌面的阻塞式 `Start()`；当前视图仍使用 Vello CPU 渲染，不需要桌面的 `UseVello()` 表面工厂。

安装 .NET 11 Android 工作负载、Android SDK 和 JDK，将 ANDROID_NDK_HOME 指向解压后的 Android NDK（已验证 r28c），然后从 Android 入口项目发布样例：

```powershell
dotnet publish samples/Gallery.Android -c Release
```

宿主从 NDK 打包 arm64 C++ 运行库；也可通过 MSBuild 属性 AndroidCppRuntime 指定官方 arm64 libc++_shared.so 的完整路径。已在 4 KB 页 arm64 真机验证 Release 冷启动、触摸按钮、滑动和前后台切换。当前 Vello 和 HarfBuzz 原生包仍报告 16 KB 对齐警告。点击可编辑的 TextBox 会打开 Android 系统键盘。已在真机验证中文候选词提交和返回键收起键盘，输入内容同步到 TextBox 和 TextChanged 事件。惯性滚动、无障碍桥接和 GPU 上屏尚未接入。

## iOS 实验宿主

`samples/Gallery.iOS` 支持 iOS / iPadOS 15 及以上。每个 UIKit 窗口场景通过 `IActivityApplicationLifetime.MainViewFactory` 加载共享页面。Core Graphics 在 Retina 视图上绘制控件，Core Text 使用系统字体完成文本测量、绘制和字体回退。视图适配安全区、旋转和屏幕键盘，支持单指点击、拖动、滚动以及系统明暗主题。

在 Mac 上安装 .NET 11 SDK、与 iOS 工作负载匹配的 Xcode 和 iOS 模拟器，然后运行：

```sh
dotnet workload install ios
cd samples/Gallery.iOS
xcrun simctl list devices available
dotnet run -r iossimulator-arm64 --device YOUR_SIMULATOR_UDID
```

以上命令适用于 Apple 芯片的 Mac。将 `YOUR_SIMULATOR_UDID` 替换为列表中目标模拟器的标识符。在同一目录下使用 `dotnet publish -c Release -r ios-arm64` 发布真机版本，需配置与应用标识匹配的 Apple 签名证书及描述文件。

自建应用可参照 [Gallery 的启动入口](./samples/Gallery.iOS/Program.cs) 和 [场景配置](./samples/Gallery.iOS/Info.plist)。在 `EclipseAppDelegate.CreateAppBuilder()` 中调用 `AppBuilder.Configure<MyApp>().UseiOS()`，在应用初始化回调中设置 `MainViewFactory`。切回前台会继续使用已连接场景的页面，场景断开时会释放页面。

已在 iOS 27 的 iPhone 模拟器验证按钮、滚动、下拉框、明暗主题、前后台恢复和系统键盘输入，并在 iPad 模拟器验证中文、Emoji、渐变与阴影绘制。真机 arm64 Release 已通过未签名构建，真机安装和运行仍待验证。

## FreeOTP 桌面应用

[FreeOTP](https://github.com/CeSun/FreeOTP) 已独立为单独仓库，通过 Git submodule 引用 EclipseUI。支持导入 FreeOTP Android 加密备份、TOTP/HOTP、验证码复制和本地加密保存。

## 当前能力

- 保留静态树、编译期动态槽和 if/foreach/switch 块；普通 CLR 状态＋显式失效，无依赖追踪或运行时通用树 diff。
- 单一符号目录；公开可写 CLR 属性、StyledProperty、路由事件、委托、附加属性、默认内容及具名槽。
- 双向绑定按 StyledProperty 或匹配的 PChanged 回调能力选择，与 ui/C# 来源无关。
- Class/Style、Element 控件模板与命名主题、类型化共享资源；模板默认值与 local 覆盖；声明式自定义样式属性和字面量转换器。
- Measure/Arrange、输入/焦点/路由、HarfBuzz 文本与逐字素字体回退（中文/彩色 emoji）、Vello CPU/GPU、保留显示列表及 damage/跳帧。
- UI 同步上下文与后台安全失效；构建失败临时作用域清理，真实内容树资源释放。
- LSP 诊断、补全、源映射与符号导航，共用目录和生成代码。

## 控件库

交互控件与主题外观分离。Windows/Linux/macOS 平台默认使用中性主题；应用引用 `EclipseUI.Themes.Fluent` 后显式启用 Fluent 皮肤及系统明暗跟随：

```csharp
using EclipseUI;
using EclipseUI.Themes.Fluent;

AppBuilder.Configure<MyApp>()
    .UseFluentTheme(ThemeVariant.System) // Light / Dark 固定明暗
    // 继续装配平台、渲染和文本管线
    .Start(args);
```

运行期调用 `SetTheme(FluentTheme.Dark)`，或传入 `FluentTheme.Light` / `FluentTheme.System` 切换模式。Gallery 支持 `--theme system|light|dark`，主题按钮循环切换三种模式。配色、默认值层和验证见 [Fluent 主题包](./design/27-fluent-theme.md)。所有 Element 均支持 `Theme` 和 `Template`；Fluent 为内置交互控件提供可替换 `.ui` 模板和 `.eustyle` 状态规则，Runtime 与通用渲染器不包含控件皮肤绘制。内容通过 `ContentPresenter` / `ItemsPresenter` 投影，切换模板保留调用方内容与行为状态。主题包可通过公开静态 `ThemeResource<T>` 复用类型化资源。详细契约见 [spec/05](./spec/05-control-themes.md)。

当前目录包含 Window、基础视觉元素、通用呈现器和交互控件。除文本、按钮和布局控件外，已提供 TextBox/PasswordBox、ListBox/ComboBox、开关与数值输入、浮层和菜单、TabControl、TreeView、DataGrid、DatePicker/Calendar、图像与形状，以及 Canvas/DockPanel。Grid 支持跨行跨列，滚动条可拖动，虚拟列表支持变高和可选容器复用。

桌面窗口由同一 UI 线程上的共享帧调度器驱动。打开子窗口或进入多层嵌套窗口循环时，其他窗口仍会处理已投递的异步续体、动画、主题、布局及按需绘制；模态关系只限制父窗口输入。每个窗口保留自己的同步上下文、剪贴板、帧率与 tick 上限，较慢的子窗口不会限制父窗口帧率。关闭或异常退出会注销调度；实际绘制期间仍禁止重入，避免重复使用渲染资源。Windows、macOS、Linux 的宿主使用同一调度机制。

Windows 原生标题栏随窗口最终解析出的主题同步明暗，覆盖初次显示、运行期切换与跟随系统；保留系统标题栏、边框和窗口按钮。此行为由 Windows 宿主统一处理，不要求应用调用 DWM API；不支持该属性的旧版 Windows 保留默认标题栏。
桌面端 Popup（包括下拉框、日期面板和菜单）默认使用独立的原生窗口，可越过主窗口边界，位置与大小受屏幕可用区域约束。内容仍在原有控件树中，共享焦点、主题、路由与资源生命周期；支持嵌套 Popup、外部点击/Escape 关闭和弹窗内文本输入。`HostMode="Overlay"` 可固定使用主窗口内浮层；`Auto`（默认）和 `Window` 在无原生弹窗工厂或无头环境中回退为浮层。自建桌面宿主的接线方式与验证范围见[Popup 契约](./design/24-control-library.md#独立-popup-窗口2026-11-21)。

完整行为、使用示例及能力边界见 [控件库契约](./design/24-control-library.md)。Gallery 仅保留 Fluent 交互演示，页面和控件示例均以 `.ui` 描述，支持主题切换、重置、滑块与进度条联动，窄窗口自动收紧布局。旧页面已移入测试夹具，继续覆盖编译器与 IDE 回归。也可独立验证并导出深浅主题控件状态：

```powershell
dotnet run --project sandbox/ControlCheck -- out/controls.png
dotnet run --project sandbox/FluentCheck -- out/fluent
```

## 边界

泛型模板标签暂不支持。原生窗口由平台层创建，运行时不依赖具体绘制后端。AOT 发布仍受原生渲染依赖限制。GPU 路径已支持原生场景片段缓存、脏区局部光栅化和保留帧；最终上屏仍复制完整表面。数据绑定的 ListBox/ComboBox 达到 128 项后自动虚拟化，在有限滚动视口内仅实现可见范围。实现与验收边界见[框架修复报告](./design/reports/framework-hardening-2026-11-18.md)。

需要软件栅格时可使用 `.UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = true })`：`VelloSparseBackend` 在 CPU 上绘制完整画面，GPU 仅上传纹理并呈现；变化帧全量重绘，空闲帧继续跳过。该模式不使用原生场景片段缓存或 GPU 局部光栅；圆角阴影支持高斯模糊并复用缓存。离屏软件渲染也可直接使用 `VelloSparseBackend`。

当前环境有一项已在重构前代码复现的精确像素 golden 差异；未改基线掩盖。详细命令、测试数和隔离证据见 [验证报告](./design/reports/root-type-unification-2026-11-13.md)。

## 文档与目录

| 路径 | 内容 |
|---|---|
| [spec](./spec/README.md) | 当前语言、生成、更新与元数据契约 |
| [design](./design/README.md) | 架构、实施计划与报告 |
| src | Runtime、Compiler、Rendering、平台与文本模块 |
| samples | 共享 Gallery、桌面、Android 和 iOS 入口 |
| sandbox | 渲染和交互验证壳 |
| tests | 编译器、无头、IDE、渲染回归 |
| [ide](./ide/README.md) | Visual Studio / VS Code / Rider 与 LSP 工具链 |

旧版状态日志与原始决策在 [历史快照](./design/history/2026-11-13-before-legacy-removal/README.md)，不作为当前 API 或兼容承诺。
