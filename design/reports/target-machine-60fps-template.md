# 目标机 60fps 复测报告模板

> 配套脚本：`tools/bench-60fps.ps1`。本模板只记录**目标机**（≥60Hz 真实显示器、具备 GPU 适配器）的复测结果。
> **本机为 29Hz 虚拟显示器（Intel UHD 630 + GameViewer 虚拟显示），刷新率不足 60Hz，本机数据不得据此下 60fps 结论**；
> GPU 适配器可用（wgpu Dx12，历史 GPU bench 有数据），GPU 分支结果仅作帧成本参考。
> 关联：`README.md:126-137`、`design/reports/render-gate-2026-11.md:128-138`（复现命令）与 `:153`（backlog #7 像素 golden 基础设施）。

## 1. 环境（需回填）

| 字段 | 值 | 来源 / 采集方式 |
|---|---|---|
| 复测日期 | | |
| 执行人 | | |
| 分支 / commit | | `git rev-parse --short HEAD` |
| 机器名 / CPU | | 脚本日志头 / `Win32_Processor` |
| 操作系统 | | |
| .NET SDK | | `dotnet --version` |
| 显卡 / 驱动 | | 脚本日志「显卡:」行 / `Win32_VideoController` |
| 显示器型号 | | |
| **CurrentRefreshRate** | ____ Hz | `Win32_VideoController.CurrentRefreshRate`（脚本自动采集） |
| **≥ 60Hz？** | 是 / 否 | 否 → 结论只能记「不适用」，不得判 PASS |
| **PresentMode** | | GalleryApp `backend: ... present=...`（脚本采集） |
| vsync 设置 | `--no-vsync`（固定） | GalleryApp 参数 |

## 2. 复现命令（固定顺序）

```powershell
# 默认：不弹窗，仅 Release 构建 + CPU 渲染测试 + RenderSpike bench
powershell -File tools/bench-60fps.ps1

# 目标机全量（会打开真实窗口，--frames 自动退出）
powershell -File tools/bench-60fps.ps1 -RunWindowMeasure -IncludeWindowSpike `
    -Iterations 20 -Frames 1200 -MaxFps 60
```

等价手工命令（逐条，便于定位失败步骤）：

```powershell
dotnet build EclipseUI.slnx -c Release
dotnet test tests/EclipseUI.Rendering.Tests -c Release
$env:RENDERSPIKE_ITERATIONS = 20
dotnet run --project samples/RenderSpike -c Release -- bench
dotnet run --project samples/GalleryApp -c Release -- --always-render --no-vsync --max-fps 60 --frames 1200
dotnet run --project samples/WindowSpike -c Release -- --frames 1200 --no-vsync --max-fps 60
```

窗口测量需人工确保窗口前台、不被遮挡；脚本默认 `-RunWindowMeasure` 关闭，避免无人值守时弹窗。
日志输出到 `out/bench-60fps-<时间戳>.log`（`out/` 已被 `.gitignore` 忽略）。

## 3. 判定口径

- **16.7ms 预算** = 1000 / 60。PASS 需**同时**满足：帧平均 ≤ 16.7ms 且 p95 ≤ 16.7ms。
- 仅当 `CurrentRefreshRate ≥ 60Hz` 时判定有效；< 60Hz（如本机 29Hz）一律记「不适用」，不得据帧时间下 60fps 结论。
- 必须记录 **PresentMode**：`Fifo` / `AutoVsync` 受垂直同步限制，`Immediate` / `AutoNoVsync` 才反映真实渲染上限。
- 分段耗时：`acquire`（等待可用图像 / 垂直同步）、`render`（GPU 提交 / 绘制）、`present`（上屏尾耗）。
  任一超预算需定位对应阶段，不能只看总帧时；分段由 `WindowSpike` 的 `stages:` 行采集。
- `RenderSpike bench` 的 GPU 分支在无适配器时输出 `NaN` / stderr `useCpu=False failed`，脚本优雅跳过，仅 CPU 数据有效。
- CPU 后端每次 Render 约 60ms 固定开销，只用于 CI 像素测试与离线导出，**不作为逐帧路径**；逐帧必须走 GPU。
- CPU 像素 golden 测试（本片 B1）必须全绿：`dotnet test tests/EclipseUI.Rendering.Tests --filter FullyQualifiedName~PixelGoldenTests -c Release`。

## 4. 结果（需回填）

| 指标 | 实测 | 来源 | 判定 |
|---|---|---|---|
| CPU 像素 golden 测试 | 通过 / 失败 | 脚本 ② 步骤 | 必须通过 |
| RenderSpike GPU 200 元素 1280×800 | ____ ms | bench 表「GPU 渲染」列 | 参考 |
| RenderSpike CPU 200 元素 1280×800 | ____ ms | bench 表「CPU 渲染」列 | 仅参考，不判 60fps |
| 帧平均 avg | ____ ms | GalleryApp `frame=...ms` | ≤ 16.7 |
| 帧 p95 | ____ ms | GalleryApp `p95=...ms` | ≤ 16.7 |
| 分段 acquire | ____ ms | WindowSpike `stages:` | 定位用 |
| 分段 render | ____ ms | WindowSpike `stages:` | 定位用 |
| 分段 present | ____ ms | WindowSpike `stages:` | 定位用 |
| ticks / rendered / skipped | | GalleryApp 汇总行 | 参考 |

## 5. 结论（需回填）

- [ ] PASS：刷新率 ≥ 60Hz 且 avg、p95 均 ≤ 16.7ms，分段无异常瓶颈。
- [ ] FAIL：任一帧指标超预算，记录超出的阶段与数值。
- [ ] 不适用：刷新率 < 60Hz（本机 29Hz 即此情形）或无 GPU 适配器。

补充说明 / 偏差记录：

```
（填写：测试期间窗口是否被遮挡、是否切换了电源计划、是否有后台负载、GPU 驱动版本等）
```

## 6. 已知本机限制（复测时勿复用本机数据）

- 本机显示器 `CurrentRefreshRate = 29Hz`（虚拟显示器），窗口帧测量受刷新率限制。
- 本机 GPU 适配器可用（wgpu Dx12，历史 GPU bench 有数据）；若本次 `RenderSpike bench` GPU 列为 `NaN`，
  表示适配器初始化失败，仅记录、不影响 CPU 结论，也不据此下 60fps 结论。
- 本机 CPU 后端固定开销约 60ms/帧，与 60fps 目标无关。
- 字体依赖系统字体（Arial / Segoe UI / 微软雅黑）；CPU 像素 golden 场景只用非文本图元，规避字形差异。

## 7. 关联待办

- backlog #7「像素测试基础设施：探针 DSL + 失败时输出 diff 图」：本片已落地文本基线 + 探针 + 失败时 `TestResults/` 实际 PNG；
  「探针 DSL」仍为后续项（当前探针是 C# 元组常量，非独立 DSL）。
- CPU 侧 retained / 局部重绘原型（`src/EclipseUI.Rendering/SceneGraph/RetainedFrameCompositor.cs`）为原型：
  后端无 scissor，scratch 仍整帧渲染，仅合成层采纳脏区；要真正减少光栅化工作量需后端提供脏区裁剪能力。
