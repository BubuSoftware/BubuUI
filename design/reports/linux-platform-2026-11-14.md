# Linux 平台实现验证 · 2026-11-14

初次实施环境：Windows 11 x64，.NET SDK 11.0.100-preview.7，项目目标 net10.0。首次交叉发布时尚无可用 WSL；同日安装 WSL 后已完成 Linux 原生调试，见下文补充结果。

## 结果

| 验证 | 结果 |
|---|---|
| `dotnet build BubuUI.slnx` | 通过，0 警告 / 0 错误 |
| `dotnet test BubuUI.slnx --no-build --no-restore -m:1` | 1,582 通过，2 个 Linux 原生用例明确跳过；编译器 425、无头 648、IDE 317、平台 22、渲染 146、VS 壳 24 |
| 最终新增工厂兼容性用例后，重跑 Linux 平台测试 | 23 通过，2 跳过 |
| 启动器裁剪标注后，重跑 AppBuilderTests | 9 通过 |
| Windows 原生 Gallery `--frames 3 --max-fps 60` | DX12 / Bgra8Unorm / Fifo；3 ticks、1 rendered、2 skipped，正常退出 |
| Gallery `--cpu --shot out/gallery-cross-platform.png` | 1000×760，944 commands，hash `0269ED28761D5D6A`；已查看截图 |
| Release trimmed / self-contained `linux-x64` 发布 | 通过，输出 `out/linux-x64` |
| Release trimmed / self-contained `linux-arm64` 发布 | 通过，输出 `out/linux-arm64` |
| 发布原生资源 | apphost、Vello、VelloSparse、HarfBuzz 均存在；验证 ELF machine 分别为 62 / 183，匹配 x86-64 / AArch64 |

完整回归之后的修改为启动器裁剪标注、Gallery DemoRow 元数据保留，以及新增一个表面工厂兼容性用例；最终 solution 重新构建通过，并完成上表中的定向复测和双架构发布。

发布时仍有现有 `DataGridColumn` 通用反射路径的 3 条 IL2075 分析警告。已为 Gallery 使用的 `DemoRow` 显式保留公开属性，并为 AppBuilder 应用类型保留无参构造函数；没有压制警告。自建应用裁剪时应为反射绑定数据保留元数据，或配置 `ValueGetter` / `ValueSetter`。

## Linux 运行验证范围

新平台逻辑测试覆盖输入 ABI 和映射、缩放、组合文字、真实控件输入、焦点/浮层、resize/damage/主题、Content 替换、UI 同步上下文和异常清理。2 个原生测试覆盖 SDL 事件队列和 UTF-8、系统剪贴板、Vulkan 上屏、默认 CJK/emoji 字体、CPU 绘制及完整 Linux 应用生命周期。

首次交叉发布时原生测试尚未执行；现已在 WSLg 验证，见后文。已添加 `.github/workflows/linux.yml`，在 Ubuntu + 官方 SDL3 3.2.28 + Xvfb + Mesa Vulkan 中运行原生用例，并运行 Release 发布的 Gallery 窗口和离屏截图。工作流尚未在远端执行。

跨屏、物理 GPU、IBus/Fcitx 的候选窗口和真实中英文切换仍需在目标桌面进行交互验收。启动命令和依赖见 [平台说明](../26-linux-platform.md)。

## WSL 原生调试补充

环境：Ubuntu 26.04.1 LTS / WSL2 / WSLg，SDK 10.0.115、运行时 10.0.15，SDL 3.4.2，ICU 78.2，Mesa 26.0.8 / llvmpipe (LLVM 21.1.8)。使用发行版提供的 .NET SDK、SDL3、Vulkan 与 DejaVu/Noto 字体；构建输出隔离到 `out/wsl-artifacts`。

发现并修复：

1. Vulkan 首帧在 `Texture::create_view` 原生崩溃：交换链使用了 capabilities 的全部 usage，导致 BGRA 纹理被赋予未启用设备特性的 `StorageBinding`。改用 Vello 呈现所需的 `RenderAttachment`，Windows DX12 与 Linux Vulkan 均实跑验证。
2. WSLg 缺少 `zwp_text_input_v3`，SDL 3.4.2 在启用文本输入时返回失败。对该特定兼容性错误回退布局解析后的基本 Unicode 按键输入；保留原生失败的其他诊断。原生测试增加普通字符、空格、快捷键过滤及实际 resize 后的尺寸断言；9 个新增逻辑用例覆盖非 BMP、AltGr 和控制/导航键。
3. 像素探针的百分比诊断依赖平台区域数据，Linux 文本不包含测试要求的 `25.0%`。改为固定区域的数字加百分号，Windows/Linux 均通过该用例。

| 原生验证 | 结果 |
|---|---|
| WSLg X11 平台测试 | 34 通过，0 跳过 |
| WSLg Wayland 平台测试 | 34 通过，0 跳过；覆盖缺协议的基本输入回退 |
| Xvfb + X11 + 1.25 倍缩放的平台测试 | 34 通过，0 跳过 |
| Windows / WSL Gallery 构建 | 均 0 警告、0 错误 |
| Windows DX12 连续呈现 | 5 ticks / 5 rendered / 0 skipped |
| WSL Release X11 / Wayland 连续呈现 | 两种后端分别 12 ticks / 12 rendered / 0 skipped，1000×760，Vulkan / Bgra8Unorm / Fifo |
| WSL X11 1.25 倍缩放 Gallery | 6 ticks / 6 rendered，1250×950 设备像素、1000×760 逻辑尺寸 |
| WSL Release 离屏截图 | `out/gallery-wsl-release.png`，1000×760，944 commands，hash `082E228B2E8E50EE`；已查看 |
| Windows 渲染回归 | 146 通过 |
| Linux 渲染回归 | 145 通过，1 项已有精确像素 golden 不一致，详见下文 |

WSL 下重新生成并实际运行了 `out/linux-x64` Release 裁剪自包含发布；3 条已有 DataGrid IL2075 警告仍保留。CPU 截图时 Mesa 的 EGL/Zink 探测会输出诊断，PNG 正常生成。当前 Vulkan 使用软件设备，未将其记为物理 GPU 验证。

### 精确像素基线差异

`PixelGoldens_MatchBaseline` 的 `solid-primitives` 场景：Windows hash 为基线 `FA65021E5F9A5DAD`，Linux 为 `5D49034149FF9A35`。单独在 Windows 调用同一场景绘制，并与 Linux 失败 PNG 比较：

- 总共 4096 像素，497 像素不同。
- 456 个可见像素仅 alpha 相差 1，RGB 一致。
- 41 个全透明像素的 RGB 不同，两端 alpha 均为 0。
- 场景的语义探针、颜色采样和其余 golden 场景通过；Windows 全部 146 个渲染测试通过。

差异位于 Vello CPU 图元输出；本次没有改动 CPU 渲染路径、golden 基线、hash 或比对容差。保留该失败作为原生后端跨平台精确输出的待跟进项，不将 Linux 渲染回归记为全绿。

测试 TRX 位于各测试项目的 `TestResults/wsl-*.trx`，比较图像为 `out/golden-windows.png` 和 `TestResults/PixelGolden-actual-solid-primitives.png`。真实 IME 候选窗口与中英文切换、死键组合、物理 GPU 和 arm64 运行尚未验收。

用户已有的 `samples/Gallery/Counter.bui`、`Prefs.bui` 修改未被本次工作改写。
