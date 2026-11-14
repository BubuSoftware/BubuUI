# macOS 桌面平台

`EclipseUI.Platform.MacOS` 使用 SDL3 Cocoa 窗口，Vello 通过 Metal 直接呈现。项目目标为 `net10.0`，`Gallery.Desktop` 自动选择 macOS 平台；Runtime、模板、元素树与其他平台共享。

## 启动

```bash
brew install sdl3 harfbuzz
dotnet run --project samples/Gallery.Desktop
dotnet run --project samples/Gallery.Desktop -- --frames 10 --always-render
dotnet run --project samples/Gallery.Desktop -- --cpu --shot out/gallery-macos.png
```

自建应用引用平台、Vello 和文本模块：

```csharp
using EclipseUI;
using EclipseUI.Platform.MacOS;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;

return AppBuilder.Configure<MyApplication>()
    .UseMacOS()
    .UseVello()
    .UseHarfBuzzText()
    .Start(args);
```

在进程主线程同步调用 `Start`，不要先 `await` 再创建窗口。平台通过 `pthread_main_np` 检查真正的 Cocoa 主线程；后台工作仍可通过宿主安装的 UI 同步上下文回到 UI 线程。

## 依赖与发布

需要 macOS 图形桌面、Metal 设备与 SDL3 3.2+。SDL3 加载器支持应用目录的 `libSDL3.dylib` / `SDL3.framework`、应用包 `Contents/Frameworks/SDL3.framework`、Apple Silicon 和 Intel Homebrew 路径及 `/Library/Frameworks`。缺少 SDL3 时会给出安装提示。

当前 VelloSharp 0.5.0-alpha.3 的 macOS 原生库动态依赖 Homebrew HarfBuzz；HarfBuzzSharp 自带的 `libHarfBuzzSharp.dylib` 不能替代这一依赖。因此 CPU 截图也需要 `brew install harfbuzz`，但不需要 SDL3 或窗口。发布包中的 Vello/HarfBuzzSharp 原生资源按 RID 选择；自包含发布不代表已捆绑系统 SDL3/HarfBuzz。

```bash
dotnet publish samples/Gallery.Desktop -c Release -r osx-arm64 --self-contained true -o out/osx-arm64
dotnet publish samples/Gallery.Desktop -c Release -r osx-x64 --self-contained true -o out/osx-x64
out/osx-arm64/Gallery.Desktop --frames 5 --always-render
```

这生成命令行可执行的桌面程序；`.app` 打包、签名、公证与分发所需的依赖捆绑由应用发布流程处理。NativeAOT 继续受现有上游原生依赖限制。

## 平台契约

- `MacOSWindow` 创建支持高密度的 Metal 窗口，持有 `SDL_Metal_CreateView` 返回的 NSView。`NativeWindowHandle.FromAppKit` 借用此句柄；生命周期按 UI、host、presenter、Metal view、原生窗口顺序释放。
- Vello 默认选择 Metal，先创建表面，再请求兼容 adapter；Win32/Xlib/Wayland 和旧 Win32 工厂行为保持原有契约。
- 布局尺寸为 `pixelSize / displayScale`。鼠标坐标按窗口坐标、像素密度和内容缩放换算；IME 候选矩形做反向换算。窗口尺寸或显示缩放改变时重配渲染表面并重绘。
- 宿主负责失效、布局、动画、保留绘制、空帧跳过、主题与 Content 替换、焦点和浮层、UI 续体及异常清理。
- Command 映射 `KeyModifiers.Meta`，Control 独立保留。macOS 支持 Command+A/C/X/V、Command 列表选择、Command 方向键、Command+Backspace、Option 按词导航/删除；原有 Control 快捷键仍可用。
- 文本来自 SDL UTF-8 文本事件，支持非 BMP 字符与 Option/死键提交。输入法预编辑经过 Started/Updated/Committed/Cancelled 路由。Cocoa 清空预编辑后立即提交的事件序列会合并为一次 Committed。
- 仅可编辑 TextBox 聚焦时启用原生文本输入；失焦取消组合和指针捕获。系统剪贴板在宿主运行时接入，退出时恢复原有服务。
- 文本默认使用系统 Arial/Helvetica，回退 Apple Color Emoji、PingFang、STHeiti 与 Songti，覆盖缺少 PingFang 文件的 macOS 安装。应用可通过 `UseHarfBuzzText` 选项指定字体。

## 验证

```bash
dotnet test tests/EclipseUI.Platform.MacOS.Tests
dotnet build EclipseUI.slnx
```

逻辑测试覆盖 SDL ABI、修饰键、Retina 坐标、滚轮、组合文字、宿主输入/焦点/主题/damage、Content 替换和异常恢复。原生测试启动同步入口的独立进程，确保窗口使用 Cocoa 主线程，验证真实窗口、SDL 队列 Unicode/IME 顺序、系统剪贴板、Metal 呈现、resize、默认字体与完整生命周期；非 macOS 显式跳过。

`.github/workflows/macos.yml` 安装依赖并运行原生测试、发布的 Gallery 窗口与 CPU 截图。工作流尚未在远端执行。本机结果见 [验证报告](./reports/macos-platform-2026-11-14.md)。真实输入法候选窗口、死键交互、跨屏缩放和 Intel 运行应在对应设备验收。

实现依据：[SDL Metal NSView](https://wiki.libsdl.org/SDL3/SDL_Metal_CreateView)、[SDL 显示缩放](https://wiki.libsdl.org/SDL3/SDL_GetWindowDisplayScale)、[SDL 输入区域](https://wiki.libsdl.org/SDL3/SDL_SetTextInputArea)、[VelloSharp](https://github.com/wieslawsoltes/VelloSharp)。
