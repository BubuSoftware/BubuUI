# Linux 桌面平台

`EclipseUI.Platform.Linux` 面向 glibc Linux x64 / arm64，使用 SDL3 3.2+ 管理 X11 / Wayland 窗口，复用 Runtime 的元素树、输入路由和生命周期，以及 Vello 的 GPU 呈现。`Gallery.Desktop` 根据运行系统选择 `UseWindows()` / `UseLinux()`，目标框架统一为 `net10.0`；Win32 API 仍有 Windows 平台标注和运行时保护。

## 启动与依赖

```csharp
using EclipseUI;
using EclipseUI.Platform.Linux;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;

return AppBuilder.Configure<MyApplication>()
    .UseLinux()
    .UseVello()
    .UseHarfBuzzText()
    .Start(args);
```

桌面运行需要：

- SDL3 3.2 或更新版本，动态库 `libSDL3.so.0`。安装发行版的 SDL3 运行库；没有 SDL3 软件包的系统可参考 `.github/workflows/linux.yml` 从 SDL 官方固定版本构建。
- Vulkan loader（`libvulkan.so.1`）和可用 ICD。支持硬件驱动，也可用 Mesa lavapipe 做软件验证。
- 可用的 X11 或 Wayland 会话。SDL 自动选择；可通过 `SDL_VIDEODRIVER=x11` / `wayland` 指定。
- 字体。Debian/Ubuntu 推荐 `fonts-dejavu-core fonts-noto-cjk fonts-noto-color-emoji`。HarfBuzz Linux 原生库随 NuGet 依赖发布。主字体和回退链可通过 `UseHarfBuzzText` 选项覆盖；非标准字体目录应显式配置。
- .NET 的系统运行依赖（包括 ICU、OpenSSL）。安装发行版的 .NET SDK/运行时时会带入这些依赖；自包含发布仍需要操作系统提供这些库。

无窗口的 `--cpu --shot` 路径只需字体和发布包中的原生渲染依赖，不加载 SDL3，也不连接显示服务器。

```bash
dotnet run --project samples/Gallery.Desktop -- --frames 10
dotnet run --project samples/Gallery.Desktop -- --cpu --shot out/gallery-linux.png
dotnet publish samples/Gallery.Desktop -c Release -r linux-x64 --self-contained true -o out/linux-x64
dotnet publish samples/Gallery.Desktop -c Release -r linux-arm64 --self-contained true -o out/linux-arm64
```

发布目录包含 Vello / HarfBuzz 对应架构原生库；SDL3、Vulkan 与显示会话由操作系统提供。AOT 仍遵循项目现有的上游依赖限制。

## 平台契约

- `NativeWindowHandle` 明确保存窗口类型、窗口句柄、display 和 X11 screen；`ISurfaceFactory` 新增对应重载。已有 Win32 `Create(nint, ...)` 调用保持可用，旧工厂在收到 Linux 句柄时明确报不支持。
- Vello 对 Win32 / Xlib / Wayland 分别构建表面。先创建表面，再请求兼容此表面的 adapter；Windows 默认 DX12，Linux 默认 Vulkan，失败后尝试 wgpu 默认后端集合。
- `LinuxWindow` 负责 SDL 资源、窗口消息、标题/逻辑尺寸回推；GPU 表面释放后才销毁原生窗口。原生操作限于创建窗口的主线程。
- `WindowHost` 安装 UI 同步上下文、挂接 UiObject、排空后台续体、处理失效、布局、推进动画和提交帧；无 damage 时跳过呈现。支持主题更新、整棵 Content 替换和焦点提示。
- 布局使用 `pixelSize / displayScale`，鼠标窗口坐标使用 `windowCoordinate * pixelDensity / displayScale`。输入法候选框位置做反向换算；尺寸、屏幕和缩放变化重新配置表面并触发整帧。
- SDL 键码单独映射，不把 SDL scancode 当作 Win32 VK。左右修饰键、重复键、左/中/右键、捕获和精细滚轮按 Runtime 约定路由；失焦清除捕获和焦点。Escape 先关闭浮层，再处理窗口退出。
- SDL UTF-8 提交文本直接进入 TextInput，支持非 BMP 字符及 AltGr。组合文字按 Started / Updated / Committed / Cancelled 路由，不重复提交；组合期间导航键交给原生输入法。仅可编辑 TextBox 聚焦时启用文本输入，并每帧更新候选位置。
- 部分旧版 SDL 在 Wayland 合成器缺少 `zwp_text_input_v3` 时拒绝启动文本输入。此时回退为布局解析后的 Unicode 按键输入，保留 Shift/Caps/AltGr 和重复键，过滤快捷键及控制字符；聚焦文本框不会使应用退出。此回退不提供原生预编辑、候选框或死键组合，完整 IME 需要 X11 或支持相应协议的 Wayland/SDL 环境。
- 帧循环运行时使用 SDL 系统剪贴板，退出及异常清理恢复原有 `Clipboard.Current`。UI 先清理，再依次释放 host、presenter、native window。

## WSL 调试

已在 Ubuntu 26.04.1 / WSL2 / WSLg 上验证，SDL 3.4.2，.NET 10.0.15，Mesa llvmpipe 软件 Vulkan。X11、Wayland 及 Xvfb 1.25 倍缩放的平台测试均为 34 通过、0 跳过；Release 裁剪发布版已实际运行。

Ubuntu 26.04 可直接安装运行依赖：

```bash
sudo apt-get install -y dotnet-sdk-10.0 libsdl3-0 libvulkan1 mesa-vulkan-drivers \
  fonts-dejavu-core fonts-noto-cjk fonts-noto-color-emoji
```

WSL 与 Windows 同用一份源码时，使用独立 artifacts 目录，避免两套 SDK 共用 `obj` 内的 NuGet 路径和构建状态：

```bash
cd /mnt/d/workspace/EclipseUI
dotnet build samples/Gallery.Desktop --artifacts-path out/wsl-artifacts
SDL_VIDEODRIVER=x11 dotnet out/wsl-artifacts/bin/Gallery.Desktop/debug/Gallery.Desktop.dll
SDL_VIDEODRIVER=wayland dotnet out/wsl-artifacts/bin/Gallery.Desktop/debug/Gallery.Desktop.dll

dotnet test tests/EclipseUI.Platform.Linux.Tests --artifacts-path out/wsl-artifacts
```

当前 WSLg 缺少上述输入法协议，配合 SDL 3.4.2 时会打印一次基本输入回退诊断；如需完整原生 IME，应选择 X11。队列中的中文/emoji 提交、基本键盘输入、剪贴板和 resize 已自动验证，真实中英文输入法切换与候选交互尚待验收。

## 验证

```bash
# 无显示服务器也可执行的平台逻辑测试
dotnet test tests/EclipseUI.Platform.Linux.Tests

# Linux 原生测试：SDL 队列、Unicode、剪贴板、Vulkan、默认字体和完整生命周期
xvfb-run -a env SDL_VIDEODRIVER=x11 dotnet test tests/EclipseUI.Platform.Linux.Tests

# 在 Wayland 会话执行相同原生测试
SDL_VIDEODRIVER=wayland dotnet test tests/EclipseUI.Platform.Linux.Tests
```

测试覆盖 ABI、按键与修饰键、DPI 坐标、滚轮、组合文字、控件输入、Tab、浮层、失焦、damage/主题/resize、Content 替换、UI 续体及异常恢复。原生用例在非 Linux 或无显示会话时显式跳过。

`.github/workflows/linux.yml` 配置 Ubuntu、SDL3 官方源码构建、Xvfb、Mesa Vulkan 和 Noto 字体，运行原生测试及 Gallery 窗口/截图。新增工作流尚未在远端运行；本机 Windows 和 WSL 原生调试结果见 [验证报告](./reports/linux-platform-2026-11-14.md)。跨屏缩放、物理 GPU 与 IBus/Fcitx 候选窗口的真实交互仍需目标桌面验收。

实现依据：[SDL 窗口属性](https://wiki.libsdl.org/SDL3/SDL_GetWindowProperties)、[显示缩放](https://wiki.libsdl.org/SDL3/SDL_GetWindowDisplayScale)、[输入法区域](https://wiki.libsdl.org/SDL3/SDL_SetTextInputArea)、[SDL 3.2.28 事件 ABI](https://github.com/libsdl-org/SDL/blob/release-3.2.28/include/SDL3/SDL_events.h)、[VelloSharp](https://github.com/wieslawsoltes/VelloSharp)。

Wayland 兼容性依据：[SDL 3.4.2 启动文本输入的失败路径](https://github.com/libsdl-org/SDL/blob/release-3.4.2/src/video/wayland/SDL_waylandkeyboard.c)、[SDL 上游的相关修正](https://github.com/libsdl-org/SDL/commit/dd6d49afbdc0a93d46e3b0be9797b129991bd5a2)。
