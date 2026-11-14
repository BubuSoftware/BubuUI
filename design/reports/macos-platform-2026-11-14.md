# macOS 平台实现验证 · 2026-11-14

环境：macOS 26.6 / Apple Silicon arm64；.NET SDK 11.0.100-rc.2.26504.105，目标 net10.0，框架依赖运行时 10.0.8。原生依赖为 SDL3 3.4.16、HarfBuzz 14.5.1；另验证 SDL3 3.2.28 官方应用内 framework。

## 实现

- 新增 `EclipseUI.Platform.MacOS`：`UseMacOS()`、Cocoa/Metal NSView 窗口、Retina、输入/组合文字、系统剪贴板与桌面生命周期。
- `NativeWindowHandle.AppKit` 与 Vello Metal 表面；Gallery 自动选择 macOS。生命周期和原生操作检查真正的进程主线程。
- Runtime 增加独立 Meta 修饰键、Command 编辑/选择/导航、Option 词导航；原 Control 快捷键保留。
- 默认字体补齐 STHeiti/Songti。现有 macOS 的 PingFang 候选在本机不存在；原生测试已确认中文和 emoji glyph 非零且能够绘制。
- 修正 LSP `DocumentStore.UriToPath` 将 Unix 本地路径误转成反斜杠的问题，新增本机路径往返用例。编译器和 IDE 的合成 Windows 路径夹具改为可跨平台解析的路径，保留精确源映射及 Windows URI 断言。
- 新增 `.github/workflows/macos.yml`；远端工作流尚未执行。

## 验证结果

最终完整回归共 1,629 通过、1 失败、2 跳过；唯一失败是下述精确像素基线用例。

| 验证 | 结果 |
|---|---|
| 完整 solution 构建 | 通过，0 警告 / 0 错误 |
| Compiler.Tests | 425 通过 |
| Headless.Tests | 655 通过，含 7 个新增键盘用例 |
| Ide.Tests | 320 通过，含 3 个新增本机 URI 用例 |
| VisualStudio.Tests | 24 通过 |
| Linux 平台逻辑 | 32 通过，2 个 Linux 原生用例在 macOS 明确跳过 |
| macOS 平台 | 28 通过，0 跳过；含 3 个独立主线程原生场景 |
| 渲染回归 | 145 通过，1 个精确像素 golden 用例失败，见下文 |
| SDL3 最低版本兼容 | 应用内官方 3.2.28 framework 与 Homebrew 3.4.16 均 28/28 通过 |
| Debug Gallery 原生窗口 | Cocoa / Metal / Bgra8Unorm / Fifo；8 ticks / 8 rendered / 0 skipped |
| Release 裁剪自包含 arm64 | 发布及实际运行通过；8 ticks / 8 rendered / 0 skipped |
| Release 裁剪自包含 x64 | 交叉发布通过；Mach-O x86_64 apphost 与原生库，未在 Intel 运行 |
| Debug / Release CPU 截图 | 两者均 1000×760、944 commands、hash `63AAE169612AC5D5`；已查看 |

原生 Gallery 的 Retina backing size 为 2000×1504，逻辑尺寸 1000×752，scale 2.00。系统根据当前显示器可用工作区限制初始窗口高度，因此小于请求的 760；平台读取实际客户区尺寸并按此布局。原生测试另覆盖 500×360 resize、表面尺寸同步、中文/emoji、Command+A、剪贴板恢复及 Cocoa 组合文字取消/提交。

两个 Release 发布均保留现有 DataGrid 反射路径的 3 条 IL2075 裁剪分析警告；未压制。Gallery 的 DemoRow 已有显式元数据保留。发布后的 CPU 截图与 Debug 一致。

本机 Homebrew 旧版本首次在 fontconfig 安装后步骤失败；使用 `brew install --skip-post-install harfbuzz` 完成运行库安装，渲染和字体测试已通过。临时挂载的 SDL3 官方镜像已卸载，正式运行使用 Homebrew SDL3。

## 精确像素基线

`PixelGoldens_MatchBaseline` 的语义探针均通过，以下两个纯图元场景的完整 hash 与现有基线不同：

| 场景 | 现有基线 | macOS arm64 |
|---|---|---|
| solid-primitives | `FA65021E5F9A5DAD` | `545973A7E136D408` |
| transform-clip | `0AD8AFE436161042` | `320080985B30C305` |

本次没有修改 CPU 光栅算法、golden 基线、hash 或比较容差。该失败保留在最终回归结果中，不将完整渲染测试记为全绿。

已将改动前 `HEAD fd9aa285e79424a435cfde60bc54deff369a7c36` 只读导出到独立临时目录，重新构建并单独运行该用例；两个场景的失败 hash 与上表完全相同，确认差异在本次实现之前已存在。未设置更新 golden 的环境变量，临时副本基线与 HEAD 一致。复现日志为 `/private/tmp/eclipseui-macos-baseline-5GP2tt/baseline-test.log`，对应 TRX 为其测试项目的 `TestResults/macos-head-baseline.trx`。

## 产物与运行

```bash
dotnet run --project samples/Gallery.Desktop
out/osx-arm64/Gallery.Desktop --frames 8 --always-render --max-fps 60
out/osx-arm64/Gallery.Desktop --cpu --shot out/gallery-macos-release.png
dotnet test tests/EclipseUI.Platform.MacOS.Tests
```

发布目录：`out/osx-arm64`、`out/osx-x64`；截图：`out/gallery-macos.png`、`out/gallery-macos-release.png`。构建/测试日志位于 `out/macos-final-build.log`、`out/macos-final-tests.log`，最终各测试项目 TRX 为 `TestResults/macos-final.trx`。独立验证报告另有 `macos-homebrew.trx`、`macos-paths.trx`、`macos-ide.trx`、`macos-rendering.trx`。

真实中文输入法候选窗口与切换、Option 死键、触控板滚动手感、跨屏缩放和 Intel 实机仍需对应桌面人工验收。当前自动化覆盖真实 Cocoa/Metal/剪贴板和 SDL 事件队列，未将合成队列事件等同于真实输入法交互。`.app` 打包、签名和公证尚未实现。
