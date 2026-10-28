# 10. G1 渲染 Gate 执行清单

> 状态：**已执行，结论 Go（有条件）** · 时间盒：2–3 周 · 关联计划：[09 开发计划](./09-development-plan.md#97-下一任务详细计划g1-渲染-gate)\n> 结果报告：[render-gate-2026-11.md](./reports/render-gate-2026-11.md)
>
> 本文是 G1 的可执行工作单；每完成一项就更新状态与产出链接。G1 的唯一目的是回答：
> **Vello 能不能作为 EclipseUI 的唯一渲染后端？**

## 10.1 目标与非目标

### 目标

1. 用 `vello_cpu` 在无窗口环境渲染出一张 PNG。
2. 用真实字体把 HarfBuzz glyph run 画出来，验证布局测量与绘制一致。
3. 逐项验证 dash、阴影、渐变、clip/blend、图像缩放、彩色 emoji/fallback。
4. 记录 layout / display list / FFI / 光栅化的分段耗时。
5. 给出 Go / Conditional Go / No-Go 结论，并写入决策记录。

### 非目标

- 完整 scene graph、合成器、damage/layer 优化；
- 3D 引擎（只做 texture → Vello image 的最小混排）；
- 样式系统、控件库、LSP、热重载；
- 最终 display list 二进制格式；
- 生产级 API 稳定性。

## 10.2 任务看板

状态：`TODO` / `DOING` / `DONE` / `BLOCKED` / `CUT`

| ID | 任务 | 工期 | 状态 | 产出 |
|---|---|---:|---|---|
| G1-01 | 提交当前基线，创建 `spike/vello-render` 分支 | 0.5d | DONE | `f801f4f` / `spike/vello-render` |
| G1-02 | 建 `samples/RenderSpike`，确认 VelloSharp/FFI 原生库加载 | 1d | DONE | `out/basic.png`（256×256，中心 `#DC1E1E`） |
| G1-03 | 定义 `DisplayList` / `DrawCommand` / `IRenderBackend` | 0.5d | DONE | `src/EclipseUI.Rendering/`（含 `Brush` / `PathGeometry` / `RenderTarget` / `PngEncoder`） |
| G1-04 | 后端：clear、fill rect、clip、transform | 1d | DONE | `src/EclipseUI.Rendering.Vello/`，CPU + GPU 离屏 |
| G1-05 | 圆角矩形、描边、虚线、路径 | 1d | DONE | `PrimitiveTests` / `ScopeTests` |
| G1-06 | 接入 `HarfBuzzTextShaper`，渲染 glyph run | 1.5d | DONE | `TextTests`，`DrawGlyphs` 复用 `ShapedBuffer` |
| G1-07 | 功能矩阵：阴影/渐变/blend/图像/emoji/fallback | 1.5d | DONE | `FeatureMatrixTests` + `out/features.png`，8/9 通过 |
| G1-08 | 性能 harness：100/1k/10k 命令、200 元素场景 | 0.5d | DONE | `bench` 模式，报告 §5 |
| G1-09 | 像素探针 + 确定性测试 | 1d | DONE | `tests/EclipseUI.Rendering.Tests/`（34 个测试） |
| G1-10 | GPU 后端或 CPU readback 呈现 | 1.5d | DONE（离屏） | `useCpu: false` 渲染到 CPU 缓冲；surface 上屏移交 G2 |
| G1-11 | 3D texture → Vello image 混排（stretch） | 1d | DONE（替身） | `DrawImage` + 旋转/缩放，等价于 readback 纹理合成 |
| G1-12 | 报告、go/no-go、后续 backlog | 1d | DONE | `design/reports/render-gate-2026-11.md` |

**关键路径**：G1-01 → G1-02 → G1-04 → G1-06 → G1-09 → G1-12。  
**可并行**：G1-03 与 G1-02；G1-07 与 G1-08；G1-11 在 G1-10 之后。

## 10.3 契约 v0（已冻结项不再改）

```csharp
namespace EclipseUI.Rendering;

public readonly record struct Color(byte R, byte G, byte B, byte A = 255);

public abstract record Brush;
public sealed record SolidColorBrush(Color Color) : Brush;
public sealed record LinearGradient(Point Start, Point End, IReadOnlyList<GradientStop> Stops, GradientExtend Extend = Pad) : Brush;
public sealed record RadialGradient(Point Center, double Radius, IReadOnlyList<GradientStop> Stops, GradientExtend Extend = Pad) : Brush;
public sealed record SweepGradient(Point Center, double StartAngle, double EndAngle, IReadOnlyList<GradientStop> Stops, GradientExtend Extend = Pad) : Brush;

public abstract record DrawCommand;
public sealed record FillRect(Rect Rect, Brush Brush) : DrawCommand;                  // 另有 (Rect, Color) 重载
public sealed record FillRoundedRect(Rect Rect, double Radius, Brush Brush) : DrawCommand;
public sealed record FillEllipse(Rect Bounds, Brush Brush) : DrawCommand;
public sealed record StrokeRect(Rect Rect, Brush Brush, double Width) : DrawCommand;
public sealed record FillPath(PathGeometry Path, Brush Brush) : DrawCommand;
public sealed record StrokePath(PathGeometry Path, Brush Brush, double Width,
    IReadOnlyList<double>? DashPattern = null, double DashPhase = 0) : DrawCommand;
public sealed record DrawGlyphs(FontFace Font, ShapedBuffer Glyphs, Point Origin, Brush Brush,
    Transform? Transform = null, double Opacity = 1) : DrawCommand;
public sealed record DrawImage(ImageHandle Image, Rect Destination, double Opacity = 1) : DrawCommand;
public sealed record DropShadow(Rect Rect, double CornerRadius, double BlurRadius, Point Offset, Color Color) : DrawCommand;

public sealed record PushClip(Rect Rect) : DrawCommand;        // 压栈
public sealed record PushClipPath(PathGeometry Path) : DrawCommand;
public sealed record PopClip : DrawCommand;
public sealed record PushTransform(Transform Transform) : DrawCommand;
public sealed record PopTransform : DrawCommand;
public sealed record PushLayer(double Opacity, LayerBlend Blend = LayerBlend.Normal) : DrawCommand;
public sealed record PopLayer : DrawCommand;

public sealed class DisplayList
{
    public Size Size { get; set; }
    public List<DrawCommand> Commands { get; }
    public DisplayList Add(DrawCommand command);
    public IReadOnlyList<string> Validate();   // 栈配对校验
}

public interface IRenderBackend : IDisposable
{
    string Name { get; }
    RenderCapabilities Capabilities { get; }
    void Render(DisplayList list, RenderTarget target);
}
```

与草案的差异（已实现，作为 v0 冻结）：

- 统一用 `Brush` 而不是 `Color`，渐变与实色走同一路径（`Color` 有隐式转换与重载）。
- `PushLayer` 不再携带 `Transform`：变换统一由 `PushTransform` 表达，避免与图层裁剪矩阵语义混淆。
- 新增 `DropShadow`、`FillEllipse`、`StrokePath`、`PushClipPath`，覆盖 G1 功能矩阵。
- `Transform` 采用行向量约定，`A * B` 表示“先 A 后 B”；提供 `RotationAround` / `ScaleAround`。
- `FontFace` / `ImageHandle` 由契约层持有原始字节，后端按引用缓存原生对象。

约束：

- 契约层只依赖 `EclipseUI.Runtime` 的几何/文本类型，不依赖 VelloSharp。
- 先支持 `RenderTarget.CpuBuffer`（RGBA）；GPU surface 作为第二种 target 后加。
- glyph run 直接复用 `ShapedBuffer`，不在 spike 阶段另造文本结构。

## 10.4 测试清单

### 像素探针（必须）

- [x] 填充矩形中心点颜色精确匹配。
- [x] 圆角外透明、圆角内颜色正确。
- [x] `PushClip` 外透明、clip 内正确。
- [x] 文本区域非空；布局与绘制共用 shaping advance（位置误差 0），墨迹宽度差为 side bearing。
- [x] 同一输入两次 CPU 渲染哈希一致。

### 功能矩阵（必须逐项填）

| 项 | 结果 | 备注/降级方案 |
|---|---|---|
| dash 描边 | ✅ pass | `DashPattern` → `StrokeStyle.DashPattern` |
| 阴影/模糊 | ✅ pass | `DropShadow` → `DrawBlurredRoundedRect`，σ = radius/2 |
| 线性/径向/锥形渐变 | ✅ pass | 三种 brush 全映射 |
| clip 路径 + 复杂 blend | ✅ pass | `PushLayer` 裁剪层 + `LayerMix`/`LayerCompose` |
| 图像缩放质量 | ✅ pass | `ImageQuality.Medium`，4×/25× 放大正确 |
| 彩色 emoji | ✅ pass | seguiemj COLR/CPAL，4968 种颜色 |
| 字体 fallback | ❌ fail | 框架层缺口：需在 shaping 层实现回退链（G3/G4） |
| 2D/3D 同帧混排 | 🟡 部分 | `DrawImage` + 旋转/缩放替代 readback 纹理；共享 wgpu texture 留 G2/G7 |
| FFI 帧开销 | ✅ 数据已取 | GPU 6.36 ms（含 readback）/ CPU 68.76 ms @1280×800 |

### 性能记录（必须填）

单位 ms，1280×800，20 次迭代（预热 2）。`layout` 在本阶段不涉及（无 scene graph），
`序列化/FFI + 光栅化` 由 `Render` 一次调用覆盖，故合并为“渲染”列。

| 场景 | 命令数/glyph | layout | display list build | 渲染（CPU） | 渲染（GPU） | 总帧（GPU） |
|---|---:|---:|---:|---:|---:|---:|
| 空场景 | 0 | — | 0.74 | 66.04 | 4.65 | 5.39 |
| 200 元素 | 201 | — | 0.02 | 68.74 | 6.34 | 6.36 |
| 1k 文本 | 1009 glyph | — | 1.81 | 72.59 | 7.27 | 9.08 |
| 10k 命令 | 10001 | — | 1.99 | 113.53 | 27.47 | 29.45 |

## 10.5 验收标准

- [x] **AC-1**：`samples/RenderSpike` 一条命令输出 PNG（`out/basic.png`、`out/demo.png`、`out/features.png`）。
- [x] **AC-2**：`dotnet test` 全绿，含 34 个渲染测试（总 87 个）。
- [x] **AC-3**：功能矩阵 8/9 通过；失败项（字体 fallback）写明降级方案。
- [x] **AC-4**：CPU 渲染确定性可复现；跨机器用探针/容差；CPU/GPU 交叉一致。
- [x] **AC-5**：性能基线完整；CPU 68.76 ms 已标红并分析（固定开销，非像素量）。
- [x] **AC-6**：报告给出 **Go（有条件）** 及依据。
- [x] **AC-7**：契约层不依赖 VelloSharp；后端可替换。

## 10.6 Go / No-Go 判定

| 结论 | 条件 | 后续动作 |
|---|---|---|
| **Go** | ≥ 7/9 功能通过；文本质量可接受；总帧 ≤ 16.7ms 或 CPU 预处理 ≤ 8ms | 进入 G2：scene graph + Win32 宿主 |
| **Conditional Go** | 核心通过但 FFI/绑定有风险 | vendor `vello_ffi` 或自建 shim，1 周复测 |
| **No-Go** | 文本/clip/blend 质量不可接受，或优化后帧时间 > 33ms | 触发 ADR，评估 SkiaSharp 兜底或功能降级 |

最终结论：**Go（有条件）** — GPU 6.36 ms/帧、8/9 功能通过、CPU/GPU 像素一致。
条件：G2 第一周内验证 surface 上屏、`Renderer` 异步重建、CI 渲染测试规模；
CPU 路径固定开销约 60 ms，仅用于 CI/离线导出，不作为逐帧路径。
详见 [render-gate-2026-11.md](./reports/render-gate-2026-11.md)。

## 10.7 风险与升级路径

| 风险 | 触发信号 | 应对 |
|---|---|---|
| VelloSharp 原生库/API 不可用 | Day 1 加载失败或 API 缺失 | 1 天时间盒；vendor `vello_ffi` 或自建最小 C shim |
| vello_cpu 与 GPU 输出不一致 | 像素探针差异大 | CPU 仅用于 CI/兜底；GPU 单独基线；容差比较 |
| 文本 glyph 质量差 | 字宽/字形明显错位 | 对比 HarfBuzz 度量；必要时先用 path outline |
| 阴影/滤镜/渐变缺口 | 功能矩阵失败 | 框架层降级或明确不支持，记录 ADR |
| 没有可用 GPU surface | `vello_gpu` 集成失败 | spike 用 CPU readback 呈现；GPU 推迟到 G2 |
| 范围蔓延 | 开始做 scene graph/damage | 冻结非目标，优化全部进 backlog |

## 10.8 完成定义（DoD）

- [x] 代码在 `spike/vello-render` 分支，契约层可合并；
- [x] `dotnet test` 全绿（87 个测试）；
- [x] `samples/RenderSpike` 可重复输出 PNG；
- [x] 功能矩阵、性能表、风险与结论齐全；
- [x] `design/reports/render-gate-2026-11.md` 已提交；
- [x] 结论为 Go，无需触发 ADR 复议。

## 10.9 日志

### 2026-10-28

- 完成：契约层（`EclipseUI.Rendering`）、Vello 后端（`EclipseUI.Rendering.Vello`，CPU + GPU 离屏）、
  `samples/RenderSpike`（basic/demo/features + bench）、34 个渲染测试、功能矩阵与性能基线。
- 阻塞：无。
- 数据/截图：`out/basic.png`、`out/demo.png`、`out/features.png`；报告 §5 性能表。
- 关键发现：
  - GPU 200 元素 1280×800 = 6.36 ms/帧（含 readback），CPU/GPU 像素一致；
  - CPU 路径有约 60 ms 固定开销（与分辨率无关），仅可用于 CI/离线导出；
  - `Renderer` 创建 175–246 ms，必须缓存 + 异步重建；
  - 彩色 emoji、CJK（显式选字体）、渐变、阴影、虚线全部可用；字体 fallback 属框架层缺口。
- 明日：G2 第一项——wgpu surface 上屏 + 帧循环；并行启动 G3 的块布局/绑定修复。
