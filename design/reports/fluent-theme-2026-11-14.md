# Fluent 默认皮肤验证

2026-11-14，Windows。实现契约见[design/27](../27-fluent-theme.md)。

## 构建与测试

`dotnet build BubuUI.slnx --no-restore --nologo -v quiet`：通过，0 警告，0 错误。

最终全量命令：

```powershell
dotnet test BubuUI.slnx --no-build --no-restore -m:1 --nologo -v minimal --logger 'trx;LogFilePrefix=fluent-final' -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

| 项目 | 通过 | 跳过 |
|---|---:|---:|
| Compiler | 425 | 0 |
| Headless | 648 | 0 |
| IDE | 317 | 0 |
| Platform.Linux | 33 | 2 |
| Rendering | 158 | 0 |
| VisualStudio | 24 | 0 |
| 合计 | 1605 | 2 |

2 项跳过需要 Linux 原生桌面环境。FluentThemeTests 包含 12 项主题/控件回归，Linux 宿主增加 1 项跟随系统的帧循环回归。结果位于各测试项目的 TestResults/fluent-final_*.trx。

默认并行运行期间，IDE 反编译/定义导航测试出现不稳定失败；反编译测试单独运行通过，最终显式串行运行全部通过。此前一轮并行 Rendering 运行长时间未结束后已中止，不计入通过数；未修改 IDE 实现、像素基线或测试断言来规避。上表只记录最终完成的串行运行。

## 实际绘制与 Windows 宿主

```powershell
dotnet run --project sandbox/FluentCheck --no-build --no-restore -- out/fluent
dotnet run --project sandbox/ControlNativeCheck --no-build --no-restore
dotnet run --project samples/Gallery.Desktop --no-build --no-restore -- --cpu --theme light --shot out/fluent-gallery-light.png
dotnet run --project samples/Gallery.Desktop --no-build --no-restore -- --cpu --theme dark --shot out/fluent-gallery-dark.png
```

FluentCheck 通过，生成 12 张 900×1694 控件截图，包括深浅色、选中与下拉浮层、键盘焦点、悬停、按下和禁用。完整深浅色控件目录及下拉层经图像查看工具检查，文字和内容布局正常。

Windows 原生自检通过：中文/emoji 的 WM_CHAR 与字体回退、键盘编辑/KeyUp、键盘/鼠标焦点、浮层 Escape 顺序、滚动条轨道重复滚动与捕获取消，以及 Vello GPU 上屏。最终输出：`text=A你, KeyUp=3, frames=95, backend=Vello (GPU surface)`。

未改写系统明暗设置；系统变化逻辑通过可注入偏好在 ThemeBinding 和宿主帧循环测试中验证。Linux SDL 原生偏好读取已编译，真实 Linux 桌面跟随仍需 Linux 环境验证。

## 工作区

保留了开始工作前已有的 samples/Gallery/Counter.bui 与 Prefs.bui 改动。本次范围的 `git diff --check` 通过；Counter.bui 原有空白问题未更改。
