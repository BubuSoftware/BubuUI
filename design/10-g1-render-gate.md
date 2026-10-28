# 10. G1 渲染 Gate 执行清单

> 状态：**未开始** · 时间盒：2–3 周 · 关联计划：[09 开发计划](./09-development-plan.md#97-下一任务详细计划g1-渲染-gate)
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
| G1-01 | 提交当前基线，创建 `spike/vello-render` 分支 | 0.5d | TODO | 基线提交 |
| G1-02 | 建 `spikes/vello` 控制台，确认 VelloSharp/FFI 原生库加载 | 1d | TODO | 256×256 矩形 PNG |
| G1-03 | 定义 `DisplayList` / `DrawCommand` / `IRenderBackend` | 0.5d | TODO | `src/EclipseUI.Rendering/` |
| G1-04 | CPU 后端：clear、fill rect、clip、transform | 1d | TODO | 像素探针通过 |
| G1-05 | 圆角矩形、描边、虚线、路径 | 1d | TODO | 图元 PNG |
| G1-06 | 接入 `HarfBuzzTextShaper`，渲染 glyph run | 1.5d | TODO | 文本 PNG |
| G1-07 | 功能矩阵：阴影/渐变/blend/图像/emoji/fallback | 1.5d | TODO | 矩阵 + 截图 |
| G1-08 | 性能 harness：100/1k/10k 命令、200 元素场景 | 0.5d | TODO | 分段耗时表 |
| G1-09 | 像素探针 + 确定性测试 | 1d | TODO | `tests/EclipseUI.Rendering.Tests/` |
| G1-10 | GPU 后端或 CPU readback 呈现 | 1.5d | TODO | 一帧呈现 |
| G1-11 | 3D texture → Vello image 混排（stretch） | 1d | TODO | 同帧 PNG |
| G1-12 | 报告、go/no-go、后续 backlog | 1d | TODO | `design/reports/render-gate-2026-11.md` |

**关键路径**：G1-01 → G1-02 → G1-04 → G1-06 → G1-09 → G1-12。  
**可并行**：G1-03 与 G1-02；G1-07 与 G1-08；G1-11 在 G1-10 之后。

## 10.3 契约 v0（已冻结项不再改）

```csharp
namespace EclipseUI.Rendering;

public readonly record struct Color(byte R, byte G, byte B, byte A);

public abstract record DrawCommand;
public sealed record FillRect(Rect Rect, Color Color) : DrawCommand;
public sealed record FillRoundedRect(Rect Rect, double Radius, Color Color) : DrawCommand;
public sealed record StrokeRect(Rect Rect, Color Color, double Width) : DrawCommand;
public sealed record FillPath(PathGeometry Path, Color Color) : DrawCommand;
public sealed record DrawGlyphs(ShapedBuffer Glyphs, Point Origin, Color Color) : DrawCommand;
public sealed record DrawImage(ImageHandle Image, Rect Dest) : DrawCommand;
public sealed record PushClip(Rect Rect) : DrawCommand;
public sealed record PopClip : DrawCommand;
public sealed record PushLayer(double Opacity, Transform Transform) : DrawCommand;
public sealed record PopLayer : DrawCommand;

public sealed class DisplayList
{
    public required Size Size { get; init; }
    public List<DrawCommand> Commands { get; } = new();
}

public interface IRenderBackend : IDisposable
{
    void Render(DisplayList list, RenderTarget target);
}
```

约束：

- 契约层只依赖 `EclipseUI.Runtime` 的几何/文本类型，不依赖 VelloSharp。
- 先支持 `RenderTarget.CpuBuffer`（RGBA）；GPU surface 作为第二种 target 后加。
- glyph run 直接复用 `ShapedBuffer`，不在 spike 阶段另造文本结构。

## 10.4 测试清单

### 像素探针（必须）

- [ ] 填充矩形中心点颜色精确匹配。
- [ ] 圆角外透明、圆角内颜色正确。
- [ ] `PushClip` 外透明、clip 内正确。
- [ ] 文本区域非空，包围盒宽度与 `TextMeasurer` 误差 ≤ 1px/100px。
- [ ] 同一输入两次 CPU 渲染哈希一致。

### 功能矩阵（必须逐项填）

| 项 | 结果 | 备注/降级方案 |
|---|---|---|
| dash 描边 | TODO | |
| 阴影/模糊 | TODO | |
| 线性/径向/锥形渐变 | TODO | |
| clip 路径 + 复杂 blend | TODO | |
| 图像缩放质量 | TODO | |
| 彩色 emoji | TODO | |
| 字体 fallback | TODO | |
| 2D/3D 同帧混排 | TODO | |
| FFI 帧开销 | TODO | |

### 性能记录（必须填）

| 场景 | 命令数/glyph | layout | display list build | 序列化/FFI | 光栅化 | 总帧 |
|---|---:|---:|---:|---:|---:|---:|
| 空场景 | 0 | | | | | |
| 200 元素 | ~200 | | | | | |
| 1k 文本 | ~1k glyph | | | | | |
| 10k 命令 | 10k | | | | | |

## 10.5 验收标准

- [ ] **AC-1**：`samples/RenderSpike` 一条命令输出 `out/frame.png`。
- [ ] **AC-2**：`dotnet test` 全绿，含 ≥ 5 个渲染测试。
- [ ] **AC-3**：功能矩阵 ≥ 7/9 通过；失败项写明降级方案。
- [ ] **AC-4**：CPU 渲染确定性可复现；跨机器用探针/容差。
- [ ] **AC-5**：性能基线完整；1280×800/200 元素 CPU 预处理 > 8ms 标红分析。
- [ ] **AC-6**：报告给出 Go / Conditional Go / No-Go 及依据。
- [ ] **AC-7**：spike 代码不污染生产 API；契约层可保留，后端可替换。

## 10.6 Go / No-Go 判定

| 结论 | 条件 | 后续动作 |
|---|---|---|
| **Go** | ≥ 7/9 功能通过；文本质量可接受；总帧 ≤ 16.7ms 或 CPU 预处理 ≤ 8ms | 进入 G2：scene graph + Win32 宿主 |
| **Conditional Go** | 核心通过但 FFI/绑定有风险 | vendor `vello_ffi` 或自建 shim，1 周复测 |
| **No-Go** | 文本/clip/blend 质量不可接受，或优化后帧时间 > 33ms | 触发 ADR，评估 SkiaSharp 兜底或功能降级 |

最终结论：`待定`

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

- 代码在 `spike/vello-render` 分支，契约层可合并；
- `dotnet test` 全绿；
- `samples/RenderSpike` 可重复输出 PNG；
- 功能矩阵、性能表、风险与结论齐全；
- `design/reports/render-gate-2026-11.md` 已提交；
- 若 No-Go，`design/08-milestones-and-risks.md` 的决策记录已更新。

## 10.9 每日日志模板

```md
### 2026-MM-DD
- 完成：
- 阻塞：
- 数据/截图：
- 明日：
```
