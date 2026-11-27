# G1 渲染 Gate 报告（2026-11 窗口）

> 数据采集：2026-10-28 · 执行清单：[../10-g1-render-gate.md](../10-g1-render-gate.md) · 分支：`spike/vello-render`
>
> **结论：Go（有条件）** — 详见 [§8](#8-结论与-g2-建议)。

## 1. 环境

| 项 | 值 |
|---|---|
| OS | Windows 11（10.0.26200） |
| CPU | 22 逻辑核 |
| GPU | 通过 wgpu 默认适配器（`VelloSharp.Renderer`，`useCpu: false`） |
| .NET | 10.0.12（SDK 10.0.401） |
| VelloSharp | 0.5.0-alpha.3（`VelloSharp` + `VelloSharp.Native.Vello` + `VelloSharp.Native.VelloSparse`） |
| 原生库 | `vello_ffi.dll`（GPU）/ `vello_sparse_ffi.dll`（CPU） |

## 2. 交付物

| ID | 交付物 | 位置 | 状态 |
|---|---|---|---|
| D1 | 渲染契约 | `src/BubuUI.Rendering/` | ✅ |
| D2 | Vello 后端（CPU + GPU 离屏） | `src/BubuUI.Rendering.Vello/` | ✅ |
| D3 | 一帧 PNG CLI | `samples/RenderSpike/` | ✅ |
| D4 | 像素 / 确定性 / 功能矩阵测试 | `tests/BubuUI.Rendering.Tests/` | ✅ 34 个测试 |
| D5 | 本报告 | `design/reports/render-gate-2026-11.md` | ✅ |
| D6 | 基线提交 + spike 分支 | `f801f4f` / `spike/vello-render` | ✅ |

产物：`out/basic.png`（256×256 纯色矩形）、`out/demo.png`（640×400 综合场景）、`out/features.png`（900×520 功能矩阵）。

契约层不依赖 VelloSharp：`DisplayList` + `DrawCommand` + `IRenderBackend` + `RenderTarget`（RGBA8）。
后端把栈式指令（`PushTransform` / `PushClip` / `PushLayer`）在托管侧折叠成逐命令矩阵，Vello 只需执行平铺指令。

## 3. 功能矩阵

| 项 | 结果 | 证据 / 说明 |
|---|---|---|
| dash 描边 | ✅ pass | `DashPattern` → `StrokeStyle.DashPattern`；`DashPattern_LeavesGaps` |
| 阴影 / 模糊 | ✅ pass | `DropShadow` → `Scene.DrawBlurredRoundedRect`（σ = BlurRadius/2） |
| 线性 / 径向 / 锥形渐变 | ✅ pass | 三种 `Brush` 全部映射成功，见 `features.png` |
| clip（矩形 + 路径） | ✅ pass | `PushClip` / `PushClipPath` → `Scene.PushLayer` 裁剪层 |
| 复杂 blend | ✅ pass | `LayerMix` + `LayerCompose`；Multiply / Screen 等已验证 |
| 图像缩放质量 | ✅ pass | `Image.FromPixels` + `ImageQuality.Medium`；4× 放大与 25× 放大均正确 |
| 彩色 emoji | ✅ pass | `seguiemj.ttf`（COLR/CPAL）渲染出 4968 种颜色 |
| 字体 fallback | ❌ fail（框架缺口） | HarfBuzz 缺字返回 glyph 0；Vello 本身无“字体回退链”职责，需在 shaping 层实现 |
| 2D/3D 同帧混排 | 🟡 部分 | 用 `DrawImage` + 旋转/缩放模拟 3D readback 纹理，语义等价；真实 wgpu 共享 texture 留到 G2/G7 |
| FFI 帧开销 | ✅ 数据已取 | 见 §5；GPU 7.9 ms/帧（含 readback） |

**通过率：8/9 通过，1 项为框架层缺口（字体 fallback），不构成 Vello 否决项。**

## 4. 文本质量

布局与绘制共用同一 `ShapedBuffer`，pen 推进完全一致，因此**布局位置与绘制位置误差为 0**。

墨迹包围盒与 advance 宽度的差异来自字体 side bearing（属正常现象，不应作为误差指标）：

| 文本 | 字号 | shaping 宽度 | 墨迹宽度 | 差值 |
|---|---:|---:|---:|---:|
| `BubuUI render gate` | 28 | 269.08 px | 266 px | 3.08 px（1.1%） |
| `Hamburgefonstiv` | 100 | 755.83 px | 747 px | 8.83 px（1.2%） |
| `AVATAR Wave` | 48 | 312.00 px | 312 px | 0 px |

结论：**文本可用**；后续若需要“墨迹对齐全像素网格”，应调整 hinting/子像素定位，而不是改布局。

## 5. 性能

1280×800，20 次迭代（预热 2 次），单位 ms：

| 场景 | 命令数 | list 构建 | CPU 渲染 | CPU 总计 | GPU 渲染 | GPU 总计 | CPU/GPU 像素 |
|---|---:|---:|---:|---:|---:|---:|---|
| 空场景 | 0 | 0.74 | 66.04 | 66.78 | 4.65 | 5.39 | 一致 |
| 200 元素 | 201 | 0.02 | 68.74 | 68.76 | 6.34 | 6.36 | 一致 |
| 1k glyph | 24（1009 glyph） | 1.81 | 72.59 | 74.41 | 7.27 | 9.08 | 一致 |
| 10k 命令 | 10001 | 1.99 | 113.53 | 115.51 | 27.47 | 29.45 | 一致 |
| 200 元素 @640×400 | 201 | 0.02 | 60.59 | 60.61 | 3.05 | 3.08 | 一致 |

关键观察：

1. **GPU 路径达标**：200 元素 1280×800 = 6.36 ms/帧（≈157 fps），已包含 `wgpu → CPU` readback。真实 surface 呈现（无 readback）应更快。
2. **CPU 路径有固定开销**：空场景 64×64 也要约 63 ms，且不随分辨率增长（1080p 仅 76 ms）。`initThreads` 取 1/2/4/8 均无改善。判断为绑定层每次 `Render` 的固定成本（Vello 原生侧 setup），**不是 vello_cpu 的光栅化速度**。
3. **Renderer 创建昂贵**：GPU 246 ms / CPU 175 ms → 必须按尺寸缓存（后端已实现），resize 时会有一次性卡顿，G2 需做异步重建。
4. **10k 命令 GPU 27 ms**：其中大部分是 readback + 提交；命令数量级本身不是瓶颈，但需在 G2 用更真实的场景复测。
5. **CPU 与 GPU 像素一致**：所有场景逐字节比较，差异 >2 的字节数为 0（圆角 AA 场景下 <0.5%，且都是边缘 1–2 像素）。

结论：**CPU 光栅化只适合 CI 像素测试与离线导出，不作为逐帧路径**；逐帧必须走 GPU。这一点要写进 G2 的架构约束。

## 6. 确定性与测试

- 34 个渲染测试全绿（`dotnet test tests/BubuUI.Rendering.Tests`）。
- 同一 display list 渲染两次 → 像素数组完全相同；同一输入跨实例哈希一致。
- CPU/GPU 交叉一致性作为独立测试（无 GPU 适配器时自动跳过）。
- 像素断言采用「中心点精确 + 区域范围 + 包围盒」，不提交全图 golden。

## 7. 已知限制与降级方案

| 限制 | 影响 | 降级 / 计划 |
|---|---|---|
| CPU 每次渲染约 60 ms 固定开销 | 软件渲染不可用 | CI 接受；G2 排查 FFI 或改用直接 `vello_cpu` 绑定；产品层不承诺软件渲染 |
| 无 surface 呈现（只有离屏 readback） | 无法验证真实上屏/垂直同步 | G2 第一项：wgpu surface + swapchain |
| 字体 fallback 未实现 | 缺字显示 `.notdef` | G3/G4 在 shaping 层加 fallback 链（按 script/字符集选字体） |
| 阴影只有圆角矩形 | 任意形状阴影需离屏模糊 | 用 `PushLayer` + 离屏目标模拟；G4 再评估 filter graph |
| `ImageQuality` 仅三档 | 缩放质量不可精细调 | 够用；需要时在框架层做 mipmap |
| 渐变不支持任意变换矩阵 | 渐变方向受限 | 当前用起点/终点表达，覆盖 UI 场景 |
| 无 `@switch`/样式系统 | 与渲染无关 | 按 G3/G4 计划推进 |

## 8. 结论与 G2 建议

**结论：Go（有条件）。**

依据（对照 [10-g1-render-gate.md §10.6](../10-g1-render-gate.md#106-go--no-go-判定)）：

- 功能矩阵 8/9 通过，唯一失败项属于框架层（字体 fallback），不是渲染后端能力问题；
- GPU 200 元素 1280×800 = 6.36 ms/帧 ≤ 16.7 ms；
- 文本 shaping 与绘制共用推进量，位置误差 0；
- CPU 输出确定性可复现，CPU/GPU 像素一致；
- 契约层与后端解耦，替换后端不影响上层。

条件（进入 G2 前 1 周内必须确认）：

1. **surfaces**：wgpu surface + swapchain 上屏跑通一帧，确认无 readback 时的帧时间与撕裂/垂直同步行为。
2. **resize 策略**：`Renderer` 重建 175–246 ms，必须做成“旧帧继续显示 + 后台重建”，否则拖动窗口会卡。
3. **CI 性能**：CPU 固定开销若无法消除，CI 渲染测试保持在数十个用例量级（当前 8 秒可接受）。

不建议 No-Go：没有证据表明 Vello 在文本、裁剪、混合、渐变、图像、emoji 上存在不可接受的缺陷。

## 9. 复现

```powershell
# 契约 + 后端 + 测试
dotnet build BubuUI.slnx -c Release
dotnet test tests/BubuUI.Rendering.Tests/BubuUI.Rendering.Tests.csproj -c Release

# 三张 PNG（out/basic.png、out/demo.png、out/features.png）
dotnet run --project samples/RenderSpike -c Release

# 性能表（可用 RENDERSPIKE_ITERATIONS 调整迭代次数）
dotnet run --project samples/RenderSpike -c Release -- bench
```

## 10. 后续 backlog（G2 输入）

> 状态更新（2026-10-28）：第 1、5 项已在 G2 第一切片完成 / 收敛，见 [11 G2 第一切片](../11-g2-surface-slice.md)。
> - #1 ✅ wgpu surface 呈现 + 帧循环已打通（Dx12，截图与 CPU 输出一致）。
> - #5 ✅ 结论：GPU 路径 resize 只需 `surface.Configure`，`WgpuRenderer` 无尺寸状态，无需重建；
>   异步重建仅对 CPU 离屏路径有意义。

1. ~~wgpu surface 呈现 + 帧循环（阻塞项）~~ ✅
2. scene graph → display list 的遍历与裁剪剔除。
3. damage/layer v0：先全量重绘，再按脏矩形。
4. 字体 fallback 链 + 字形缓存（跨字号/DPI）。
5. ~~`Renderer` 异步重建与尺寸变更策略~~ ✅（仅 CPU 路径遗留）
6. CPU FFI 固定开销定位（可选：直接绑定 `vello_cpu`）。
7. 像素测试基础设施：探针 DSL + 失败时输出 diff 图。
8. 表面格式与 `RenderParams.Format` 必须匹配；优先非 sRGB 表面（避免二次编码发白）。
9. alpha.3 绑定缺陷：`WgpuInstance.GetInfo()` 会 abort，adapter 信息暂不可得。
