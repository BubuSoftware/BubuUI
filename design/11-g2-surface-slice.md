# 11. G2 第一切片：窗口 + GPU Surface 呈现

> 状态：**已执行，通过** · 时间盒：1 周（G2 第 1 周）· 关联计划：[09 开发计划](./09-development-plan.md#g22d-渲染垂直切片810-周)
> 前置：[10 G1 渲染 Gate](./10-g1-render-gate.md) · 结果报告：[render-gate-2026-11.md](./reports/render-gate-2026-11.md)
>
> 本文回答 G1 留下的三个 Go 条件中最关键的一个：
> **Vello 渲染结果能不能稳定上屏到真实窗口，并且 resize 不崩、颜色正确？**

## 11.1 目标与非目标

### 目标

1. 真实 Win32 窗口（无 WinForms/WPF），自建消息循环、DPI 感知。
2. wgpu 表面呈现：`instance → adapter → device → surface → acquire → render → present`。
3. 复用 G1 的 `DisplayList` 契约：同一份 display list 既能出 PNG，也能上屏。
4. 鼠标/键盘/resize/DPI 消息桥接到运行时输入模型。
5. 记录帧时间分段（build / acquire / render / present），给出可比较的基线。
6. 验证 resize 链路：`WM_SIZE → surface.Configure → 继续出帧`。

### 非目标

- 真正的 scene graph 遍历（本切片用手写 display list 替身）；
- damage / 脏区域 / 图层复用；
- 事件路由到元素树、焦点管理接线（只到窗口事件层）；
- 60fps 产品化调优（受测试机显示器刷新率限制，见 §11.5）；
- 多窗口、弹层、IME、无障碍。

## 11.2 任务看板

状态：`TODO` / `DOING` / `DONE` / `BLOCKED` / `CUT`

| ID | 任务 | 工期 | 状态 | 产出 |
|---|---|---:|---|---|
| G2-00 | 抽取 `VelloSceneBuilder`，CPU/GPU 共用同一翻译层 | 0.5d | DONE | `src/EclipseUI.Rendering.Vello/VelloSceneBuilder.cs` |
| G2-01 | 契约层新增 `ISurfacePresenter` | 0.5d | DONE | `src/EclipseUI.Rendering/ISurfacePresenter.cs` |
| G2-02 | `VelloSurfaceBackend`：instance/adapter/device/surface/renderer | 1.5d | DONE | `src/EclipseUI.Rendering.Vello/VelloSurfaceBackend.cs` |
| G2-03 | Win32 窗口宿主：注册类、消息循环、Show/Close | 1d | DONE | `src/EclipseUI.Platform.Windows/Win32Window.cs` |
| G2-04 | 输入桥接：鼠标/滚轮/离开、键盘、字符、焦点 | 1d | DONE | `Win32PointerEventArgs` / `Win32KeyEventArgs` / `MapKey` |
| G2-05 | resize + DPI：`WM_SIZE` / `WM_DPICHANGED` / 客户区换算 | 1d | DONE | `SetClientSize`、`LogicalClientSize`、selfcheck |
| G2-06 | 窗口 spike：动画场景 + 帧时间分段统计 | 1d | DONE | `samples/WindowSpike`（`--frames` / `--parts` / `--no-vsync`） |
| G2-07 | 上屏正确性：屏幕截图 + 与 CPU 光栅化比对 | 0.5d | DONE | `tools/capture-window.ps1`、`out/window-screen.png` |
| G2-08 | 回归测试：CPU 确定性 + CPU/GPU 交叉一致 | 0.5d | DONE | `tests/EclipseUI.Rendering.Tests/SceneBuilderTests.cs` |
| G2-09 | resize 冒烟：程序化改尺寸 ×4 | 0.5d | DONE | `--selfcheck` → `selfcheck: PASS` |

**关键路径**：G2-00 → G2-02 → G2-03 → G2-06 → G2-07。
**可并行**：G2-04/G2-05 与 G2-02；G2-08 与 G2-06。

## 11.3 结构

```
.ui → 元素树 → 布局 → (G2 后续) scene graph → DisplayList ─┬→ VelloCpuBackend → RenderTarget → PNG/探针
                                                          └→ VelloSurfaceBackend → wgpu surface → 窗口像素
                                                                  ↑
                                                        Win32Window（消息循环 / resize / 输入）
```

### 分层与依赖

| 层 | 程序集 | 依赖 | 说明 |
|---|---|---|---|
| 契约 | `EclipseUI.Rendering` | `EclipseUI.Runtime` | 不感知 Vello；`ISurfacePresenter` 与 `IRenderBackend` 并列 |
| 后端 | `EclipseUI.Rendering.Vello` | 契约 + VelloSharp | `VelloSceneBuilder` 唯一翻译层，CPU / GPU 共用 |
| 平台 | `EclipseUI.Platform.Windows` | `EclipseUI.Runtime` | 纯 P/Invoke，无 WinForms/WPF |
| 样例 | `samples/WindowSpike` | 平台 + 后端 + 文本 | 帧循环、输入回显、性能统计、selfcheck |

### 契约新增

```csharp
namespace EclipseUI.Rendering;

public interface ISurfacePresenter : IDisposable
{
    string Name { get; }
    RenderCapabilities Capabilities { get; }
    int Width { get; }            // 设备像素
    int Height { get; }
    void Resize(int width, int height);
    void Render(DisplayList list);
}
```

`ISurfacePresenter` 与 `RenderTarget` 的分工：前者像素留在 GPU 上屏，后者是 CPU 缓冲区供 PNG/像素探针。
两者接受同一份 `DisplayList`，因此“屏幕上看到的”和“测试断言的”是同一条命令流。

## 11.4 关键决策与发现

### D-1：窗口用纯 Win32，不引 WinForms/WPF

- 理由：WinForms 会带来 `Microsoft.WindowsDesktop.App` 运行时与合成器假设；WPF 会引入第二套渲染栈。
- 代价：自己处理消息循环、DPI、光标、最小化状态；约 500 行 P/Invoke。
- 结果：`Win32Window` 只有 `user32/gdi32` 依赖，`dotnet build` 在非 Windows 机器上也能编译（TFM `net10.0-windows`）。

### D-2：`VelloSceneBuilder` 必须是唯一翻译层

G1 的 `VelloCpuBackend.BuildScene` 是私有的；若为 surface 复制一份，两条路径必然漂移。
现已抽出为 `VelloSceneBuilder`，持有字体/图片 native 缓存，并新增交叉一致测试
（CPU vs GPU 离屏像素差异 < 0.05%）。

### D-3：`RenderParams.Format` 必须与表面格式一致（性能 + 正确性）

`WgpuSurface.GetPreferredFormat` 返回 `Bgra8UnormSrgb`，而 `RenderParams` 默认 `RenderFormat.Rgba8`。
原先两处不一致时，`RenderSurface` 每帧做一次通道序转换：

| 场景 @1100×720 | 不一致 | 一致 |
|---|---:|---:|
| 背景 + 文本 | 18.02 ms | 9.38 ms |
| 全场景（38 命令） | 25.94 ms | 10.31 ms |

修法：`_renderFormat` 由表面格式推导（`Bgra8*` → `RenderFormat.Bgra8`）。

### D-4：sRGB 表面会二次编码（画面发白）

Vello 的 `RenderFormat` 只表达 8bit 通道序，像素本身已是 sRGB 编码值。
写入 `_SRGB` 视图时 GPU 再做一次编码，导致整幅画面被“提亮”（截图中背景由深藏青变成灰紫）。
修法：在能力列表里优先选非 sRGB 格式（`Bgra8Unorm` / `Rgba8Unorm`），并用截图对比 CPU 输出确认颜色一致。

### D-5：`WgpuInstance.GetInfo()` 会直接终止进程（上游缺陷）

`0.5.0-alpha.3` 中 `WgpuInstance.GetInfo()` 调用 `vello_wgpu_adapter_get_info`，传入的是没有被
instance 持有的 adapter id，wgpu-core 触发 `Adapter[...] does not exist` panic，Rust panic 无法 unwind → `abort()`。
同样的坑：先 `RequestAdapter()` 探测、再 `Dispose()` instance 再建新 instance，也会让残留 adapter id 失效。

结论：
- 不调用 `GetInfo()`；adapter 名称/类型在 alpha.3 不可得；
- 探测后端时必须“实例 + adapter 一起丢弃”，不能只丢 instance。

### D-6：resize 不需要重建 renderer

`WgpuRenderer` 不持有尺寸；尺寸由 `RenderParams` 传入。resize 只需 `WgpuSurface.Configure`
（约 1 帧抖动，见 §11.5 selfcheck 的 p95）。
因此 G1 条件里的“`Renderer` 异步重建”只适用于 CPU 离屏路径，GPU 上屏路径不存在该成本。

### D-7：呈现模式与刷新率

- 表面只支持 `Fifo` / `Immediate`（无 `Mailbox`、无 `AutoVsync` 之外的选择）。
- `Fifo` 下 `AcquireNextTexture()` 就是垂直同步点：本机稳定 33.3ms/帧。
- 测试机 **显示器刷新率是 29 Hz**（Intel UHD 630 输出，`Win32_VideoController.CurrentRefreshRate = 29`），
  所以 Fifo 的 33.3ms 是正确行为，不是丢帧；帧间隔 p95 = 36ms，抖动小。
- `Immediate` 下不受刷新限制，可测真实渲染成本。

## 11.5 性能基线

环境：Intel UHD 630（虚拟显示适配器 GameViewer 1920×1080，29 Hz），DPI 100%，Windows，Release，场景 = `samples/WindowSpike` 默认 38 命令。

### 分段耗时（1280×800）

| 配置 | 帧间隔 | fps | acquire | render(submit) | present |
|---|---:|---:|---:|---:|---:|
| `Fifo`（vsync，锁 29 Hz 屏幕） | 33.36 ms | 30.0 | 32.42 ms | 7.98 ms | 0.19 ms |
| `Immediate`（不限速） | 11.06–25.66 ms | 39–90 | 10.79–25.42 ms | 5.51–6.71 ms | 0.18–0.36 ms |

`Immediate` 的波动来自 `acquire` 等 GPU 队列（远程/虚拟显示合成），不是场景复杂度。

### 场景分块（1100×720，`Immediate`）

| 分块 | 命令数 | 帧间隔 | fps |
|---|---:|---:|---:|
| 背景渐变 | 2 | 9.5 ms | 105 |
| + 文本 | 7 | 9.7 ms | 103 |
| + 卡片（3 渐变圆角） | 16 | 10.6 ms | 94 |
| 全场景（含阴影/裁剪/图层/旋转） | 38 | 10.6 ms | 94 |

GPU 离屏（`VelloCpuBackend { UseCpu = false }`，同场景同尺寸）：5.69–7.84 ms/帧。
上屏相比离屏多 2–3 ms，属于 surface 拷贝/合成开销。

### 分辨率伸缩（`Immediate`，全场景）

| 尺寸 | 帧间隔 | fps |
|---|---:|---:|
| 640×400 | 5.85 ms | 171 |
| 1100×720 | 9.9 ms | 101 |
| 1280×800 | 11.1–25.7 ms | 39–90 |

结论：渲染成本随像素量近似线性；本机 GPU 为低端集显 + 虚拟显示，
**不能**用这台机器判定 G2 出口标准的 60fps，需在目标机的真实显示器上复测。

## 11.6 验证

1. **上屏正确性**：`tools/capture-window.ps1` 截取窗口客户区 → `out/window-screen.png`，
   与同一状态的 CPU PNG（`out/window-frame.png`）人工比对：渐变方向、圆角、阴影、裁剪条、
   虚线、拉丁 + CJK 文本、emoji 颜色一致。
2. **resize 冒烟**：`--selfcheck` 依次请求 800×600 / 1000×700 / 640×480 / 1100×720，
   每档校验 `presenter.Width/Height == 客户区设备像素`，全部通过（`selfcheck: PASS`）。
3. **回归测试**：`dotnet test` 36 个渲染测试全绿（新增 2 个：CPU 确定性、CPU/GPU 交叉一致，
   无 GPU 环境自动跳过 GPU 用例）。
4. **输入**：点击/按键/字符打印到控制台并回显到画面（`frame N · pointer inside · key A`）。

## 11.7 出口标准（G1 条件核对）

| G1 条件 | 结果 | 说明 |
|---|---|---|
| G2 第 1 周内完成 surface 上屏 | ✅ | 真实窗口 + Dx12 表面，截图与 CPU 输出一致 |
| `Renderer` 异步重建（resize） | ✅（不需要） | GPU 路径 resize 只重配 surface；CPU 路径仍待异步化 |
| CI 渲染测试规模 | ✅ | 36 个渲染测试、87 → 89 个总测试；GPU 用例自动降级 |

## 11.8 风险与后续

| 风险 | 触发信号 | 应对 |
|---|---|---|
| alpha.3 绑定缺陷（`GetInfo` panic） | 调用即 abort | 不调用；升级后复测，必要时自建 shim |
| 无 `Mailbox` 呈现模式 | 只能 Fifo/Immediate | G2-10：用 `Immediate` + 时钟限速，或等上游暴露 latency 配置 |
| `acquire` 长阻塞不可控 | 同场景 fps 波动 2× | 记录分段耗时；目标机复测；必要时引入帧预算与降级 |
| 颜色空间回归 | 画面发白/偏暗 | 固定“非 sRGB 表面 + 匹配 RenderFormat”，并用截图断言 |
| 显示器刷新率干扰结论 | 30fps 被误判为性能问题 | 基线记录 `CurrentRefreshRate`，性能结论只用 `Immediate` |

后续任务（G2 剩余部分）：

1. **G2-10 scene graph → display list**：元素树遍历、paint 顺序、`Visual` 缓存。—— ✅ 已在 [12 G2 第二切片](./12-g2-interactive-slice.md) 落地。
2. **G2-11 输入路由**：`Win32Window` 事件 → `Element.OnPointerPressed` / `FocusManager`。—— ✅ 已在 12 落地。
3. **G2-12 文本整段 shaping 接入布局**：`TextBlock` 使用同一 `ShapedBuffer` 绘制。—— ✅ 已在 12 落地。
4. **G2-13 damage/layer v0**：元素级脏区 + 空闲跳帧已完成（[13](./13-g2-damage-v0.md)）；retained layer / 局部重绘待后续。
5. **G2-14 像素探针与 golden**：窗口截图 → 容差比较，纳入 CI（可选跳过）。—— ⏳ 待做。
6. **G2-15 `CounterApp` / `TaskList` 真窗口可交互**（G2 出口标准）。—— ✅ 已在 12 落地（GalleryApp 自检）。

## 11.9 日志

### 2026-10-28

- 完成：`ISurfacePresenter`、`VelloSceneBuilder` 抽取、`VelloSurfaceBackend`（Dx12）、
  `EclipseUI.Platform.Windows`（窗口/消息/输入/DPI/resize）、`samples/WindowSpike`、
  `tools/capture-window.ps1`、2 个交叉一致测试、`--selfcheck`。
- 阻塞：无。
- 数据/截图：`out/window-screen.png`（真实窗口）、`out/window-frame.png`（CPU 对照）、§11.5 表。
- 关键发现：
  - 表面格式与 `RenderParams.Format` 不一致会导致每帧一次通道转换，全场景 25.9 → 10.3 ms；
  - sRGB 表面会让画面二次编码发白，改用 `Bgra8Unorm` 后与 CPU 输出一致；
  - `Fifo` 的 33.3ms 由 29 Hz 显示器决定，不是掉帧；真实成本看 `Immediate`；
  - GPU 路径 resize 只需 `surface.Configure`，无需重建 renderer；
  - `WgpuInstance.GetInfo()` 在 alpha.3 会 abort，adapter 信息不可得。
- 明日：G2-10 scene graph 遍历 + G2-11 输入路由；并行 G3 的块布局透明化与状态/反应式（后由 [15](./15-state-and-reactivity.md) 的 S1–S3 定义，v4 换轨后以 [spec/03](../spec/03-reactivity-and-binding.md) 为准）。
