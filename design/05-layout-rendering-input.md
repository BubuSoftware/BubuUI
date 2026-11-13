# 5. 布局、渲染与输入

## 布局

> **2026-11-13 约束契约修订：目标已明确，运行时尚未按本节完成修正。** 保留
> `Measure → Arrange` 两遍布局和子树失效机制。本节替代 D116 中“期望尺寸可以不受
> available 限制，且这是滚动前提”的解释；原实现和已有测试不代表修订已经落地。

### 尺寸含义与职责

父级向下传递约束，子级向上报告受约束的测量结果，父级分配排列槽。必须区分下列尺寸：

| 尺寸 | 含义与边界 |
|---|---|
| 内容需求 | 控件在给定测量条件下计算的内容尺寸；单行文字、固定尺寸等可能需要更大空间。保留在控件内部，不能直接当作祖先必须分配的尺寸。 |
| `DesiredSize` | `Element.Measure` 对父级报告的结果，含 Margin；每个有限轴均不得超过本次传入的 available。 |
| 排列槽 | 父级交给 `Arrange` 的区域，含 Margin；用于决定实际尺寸与位置，不等于内容自然尺寸。 |
| `FinalRect` | 元素排列后的实际矩形，不含 Margin；绘制、命中测试及脏区计算使用它。 |
| `Extent` / `Viewport` | 滚动内容范围与可视范围，使用一致的内容坐标和单位；独立于滚动容器向父级报告的 `DesiredSize`。 |

内容画到边界外、元素因显式尺寸超出排列槽、滚动内容大于视口，是三种不同情况。
裁剪控制可见像素，不能代替测量约束；内容需求也不能自动扩大所有祖先的布局宽度。

### Element 的统一测量与排列契约

约束处理由 `Element` 的公共入口完成，各控件在 `MeasureCore/ArrangeCore` 中实现具体布局：

1. **测量输入**：available 各轴为非负有限值或正无穷；不接受 NaN、负值。正无穷表示
   该轴没有父级上限，不表示控件可以返回无限尺寸。测量产物必须有效、非负、有限。
2. **测量过程**：先扣 Margin，再把显式 `Width/Height` 和 `Min*/Max*` 应用于本地
   测量条件，使换行等计算使用正确宽度；调用 `MeasureCore`，对其结果应用自身尺寸
   约束，加回 Margin，最后按父级 available 限制对外的 `DesiredSize`。
   内部内容需求与对外结果分别保留；不能只在返回结果上截宽，却仍按错误宽度计算文本高度。
3. **尺寸优先级**：`Width/Height = NaN` 表示未设置；显式尺寸受 Min/Max 限制，
   Min > Max 时 Min 胜。父级空间小于显式尺寸或 Min 时，向上报告的 `DesiredSize`
   仍受 available 限制，不要求祖先扩大。实际元素可以因这些显式要求发生局部溢出，
   由排列和裁剪规则处理；这不等于普通内容可以突破约束。
4. **排列过程**：先从槽中扣 Margin。默认 Stretch 以剩余槽尺寸为基准；非 Stretch
   以受约束的期望尺寸与槽尺寸的较小值为基准。再统一应用显式尺寸和 Min/Max，
   计算对齐位置，最后调用 `ArrangeCore`。Max 对 Stretch 同样有效；Stretch 因尺寸
   约束未铺满时居中，Left/Top、Center、Right/Bottom 分别按各自位置落位。
5. **约束与溢出**：显式尺寸或 Min 导致的实际溢出，不回写祖先已分配的排列槽。
   普通绘制溢出按 `ClipToBounds` 与祖先裁剪处理，命中测试须遵循相同几何和裁剪边界。
6. **缓存与重测**：测量缓存包含传入约束；宽度、内容、字体和尺寸属性变化必须使相关
   测量失效。若排列采用的换行宽度与测量时不同，须经受控重测得到一致高度，不能只改变
   `FinalRect` 而保留旧的断行结果；失效传播必须收敛。

这是适用于本项目两遍模型的契约。Flutter 的约束传递原则可作为参考，但它使用
`BoxConstraints` 的一次布局往返模型，不能声称它与 Avalonia 的两遍机制完全相同。

### 布局控件与文本

- **垂直 StackPanel**：扣 Padding 后，横向传递父级可用宽度，纵向给子项无穷约束以
  累加高度与 Gap；面板自身的对外结果仍经统一 `Measure` 限制。默认拉伸且没有显式
  宽度要求的子项，横向跟随面板内容区，不因某个子项的长文字把整张卡片撑宽。
  Avalonia 排列中的 `max(面板宽, child.DesiredSize.Width)` 依赖前面的统一测量约束；
  不能仅复制这行表达式，并把未约束的内容需求当作 `DesiredSize`。
- **WrapPanel**：在有限横向约束内分行；横向确实无界时可以形成单行。
  单个不可收缩子项的溢出，不意味着可以向受约束的祖先报告无限制的宽度。
- **Grid**：Auto 轨道可以先测量内容需求，星号轨道按有限剩余空间分配。
  求出有效轨道宽度后须按单元格约束重测依赖宽度的内容，再确定行高；不能把首次
  无穷宽测量直接当作最终换行高度。固定轨道、Auto 轨道发生局部溢出时，Grid
  对父级的 `DesiredSize` 仍受约束。
- **文本**：先 shaping 再测量，布局与绘制共用结果。`TextWrapping` 默认 NoWrap；
  Wrap 在有限宽度内按词界断行，超宽长词（含 CJK 无空格串）字符级切分，折叠行首尾空白。
  `TextBlock.TextLines` 是换行绘制依据。无界横向测量时 Wrap 可退化为自然单行。
  Button/CheckBox 的单行标签可以有较大的内容需求，但不能凭此扩大整条祖先布局；
  是否支持标签换行、截断或局部裁剪属于控件自身能力。

### ScrollViewer 的方向与范围

默认 Horizontal = Disabled、Vertical = Auto。各方向独立决定给内容的测量约束；
表中的“视口约束”指扣除 Padding 后的可用尺寸：

| 模式 | 是否可滚 | 内容在该轴的测量约束 | 滚动条 |
|---|---|---|---|
| Disabled | 否 | 保留视口约束 | 不显示 |
| Auto | 是 | 放开为正无穷 | Extent 超过 Viewport 时显示 |
| Hidden | 是 | 放开为正无穷 | 不显示 |
| Visible | 是 | 放开为正无穷 | 恒显示 |

滚动之所以能量到完整内容，是滚动容器主动放开对应轴的约束，而不是所有控件都可以
违反传入约束。滚动容器自身向父级报告的尺寸仍被父级 available 限制。
虚拟化或逻辑滚动可以由内容协议报告完整 Extent，不必实例化、测量所有条目。

- **测量**：按上表构造内容约束，得到内容范围；Extent 与 Viewport 分开保存。
  普通物理滚动的 Extent 来自放开可滚轴后的内容测量；不能从被视口限制的结果或
  当前可见像素反推完整范围。虚拟化范围来自条目数量、尺寸等布局信息。
- **排列**：Viewport 以最终内容区为准。可滚轴给内容 `max(Viewport, Extent)`，
  Disabled 轴给视口大小；各元素的显式尺寸约束仍按统一规则处理。
  偏移按 `[0, max(0, Extent - Viewport)]` 钳制；Disabled 轴有效偏移与 Scrollable 均为 0。
  缩小内容或改变模式后，范围与有效偏移必须同步有效，排列中不得制造失效循环。
- **无界父级**：某轴没有有限视口上限时，按有限内容范围确定该轴尺寸，不凭空产生
  可滚动视口。纵向滚动页面须由窗口或 Grid 等祖先提供有限高度。
- **裁剪与滚动条**：滚动容器始终裁剪到视口；裁剪不改变已计算的布局和 Extent。
  当前叠加绘制的滚动条不占布局空间；若以后采用占位滚动条，须把占用纳入视口并受控重测。
- **单列页面用法**：横向 Disabled 保持有限宽度，纵向 Auto 允许内容增长并滚动。
  主动启用横向滚动意味着内容可按自然宽度展开，WrapPanel 和文本不会再以视口宽自动换行；
  要在横滚区域内保留某部分换行，应给该部分明确的有限宽度。

本次撤销“横向 Auto 仍按视口宽测量，再从超约束 DesiredSize 检出刚性溢出”的旧差异。
这个方案依赖基础约束失效，不能与修订后的统一测量契约并存。

### 实施差距与验收

以下均为待实施项，本轮仅修正文档：

| 层次 | 当前差距 | 修正目标 |
|---|---|---|
| `Element.Measure` | 未将对外期望限制到 available；尺寸约束只在 `MeasureCore` 返回后应用 | 统一处理测量输入、内容需求、尺寸约束及对外 `DesiredSize` |
| `Element.Arrange` | Stretch 未统一应用 Min/Max；非 Stretch 直接采用内部期望 | 槽、对齐、尺寸约束和局部溢出使用一致顺序 |
| `StackPanelBase` | 使用未约束的子项期望向上传播并扩大排列槽 | 接入统一契约，验证嵌套面板与透明块的有限横向约束 |
| `ScrollViewer` | 横向始终给视口宽、纵向始终给无穷；仅排列读取方向开关 | 测量和排列都按方向模式工作；模式变化须触发重测 |
| `Grid` | 首次测量后未按求出的轨道宽度重测换行内容 | 单元格宽度、断行与行高一致 |
| 测试与演示 | 既有测试把有限约束下报告超宽 Extent 当作预期 | 修改旧预期，并按下表补齐回归；Gallery 窄窗目检 |

验收不能只检查卡片右缘是否被裁掉，应覆盖测量、最终几何和可滚范围：

| 场景 | 必须满足 |
|---|---|
| 父级给 300 宽，普通内容自然宽 800 | 对外 `DesiredSize.Width ≤ 300`；祖先不被普通内容撑宽 |
| 嵌套垂直 StackPanel，子项含长 Button 和 NoWrap 文本 | 默认拉伸且无显式宽度的卡片均跟随可用宽度；局部绘制溢出不扩大卡片背景 |
| 显式 Width、Min 大于槽，Max 小于 Stretch 槽 | 对外测量仍受限；局部实际尺寸、对齐及裁剪按契约处理；Margin 不重复计算 |
| TextBlock Wrap、WrapPanel、Grid 星号列 | 随有限宽度正确断行，行高匹配；窗口先缩小再放大时无旧断行残留 |
| 横向 Disabled、纵向 Auto | 内容约束为 `(视口宽, +∞)`；横向不滚、纵向范围完整 |
| 横向 Auto/Hidden/Visible，宽 800 内容置于宽 300 视口 | 内容可报告宽 800；滚动容器对外不超过 300，ScrollableWidth 为 500；出条分别按模式处理 |
| 纵向 Disabled | 不由 ScrollViewer 主动放开纵向测量；纵向不滚且有效偏移为 0 |
| 滚动后缩小内容、改变方向模式或调整窗口 | Extent、Viewport、有效偏移和断行更新一致，无布局循环 |
| 无界父级与虚拟化内容 | 布局结果有限；无界轴不产生虚假滚动；虚拟化 Extent 不等于已实现项的尺寸之和 |
| Gallery 的 Section + BodySlot + Showcase 窄窗页面 | 普通内容不撑宽第三张卡片；标题、正文测量宽度与实际布局一致；裁剪和命中正确 |

现有 `ScrollViewerTests.Horizontal_Disabled_ByDefault_ClampsContentAndForbidsScrolling`
把受 400 宽约束的内容 Extent 断言为 800，是旧契约的测试，不能用来证明新规则已实现。
其他依赖未约束期望尺寸的测试也须按使用场景迁移，而非只改截图或缩短演示文字。

### 参考依据

- [Flutter BoxConstraints](https://api.flutter.dev/flutter/rendering/BoxConstraints-class.html)：
  父级传约束、子级选取满足约束的布局尺寸；用于说明约束与尺寸的职责边界。
- [Avalonia Layoutable](https://github.com/AvaloniaUI/Avalonia/blob/main/src/Avalonia.Base/Layout/Layoutable.cs)：
  测量先应用本地尺寸约束，返回前将结果限制到 available；排列统一处理对齐及尺寸约束。
- [Avalonia StackPanel](https://github.com/AvaloniaUI/Avalonia/blob/main/src/Avalonia.Controls/StackPanel.cs)：
  堆叠方向放开测量；排列中的 max 表达式须结合布局基类契约理解。
- [Avalonia ScrollContentPresenter](https://github.com/AvaloniaUI/Avalonia/blob/main/src/Avalonia.Controls/Presenters/ScrollContentPresenter.cs)：
  普通物理滚动按可滚方向构造无穷约束，容器本身的期望仍受 available 限制。

以上官方文档与源码于 2026-11-13 核对；这是本项目的契约修订，不宣称两个参考框架的
所有尺寸、裁剪和滚动细节完全相同。D116 的原始实施记录保留于历史快照，不作为当前目标规则。

## 渲染管线（渲染器：Vello 单栈）

**决策：渲染后端只用 Vello，不引入 SkiaSharp / tgfx。**（决策依据见
[08 决策记录](./08-milestones-and-risks.md#决策记录汇总)）

```
元素树变更 → 场景图增量更新 → 合成器 vsync 帧循环：
  layout → cull → layer 合并 → damage region
  → 序列化 display list → 每帧一次 FFI → Vello 建 scene → 光栅输出
```

- 透明/变换自动提升为 layer。
- 动画（spring/tween）在合成器线程跑，改的是 layer 变换而非元素属性。
- damage region 保证只重绘脏区域。

### Vello 集成设计

- **变体分工**：`vello_gpu` 为主力渲染路径（CPU 预处理路径 + GPU 栅格合成，
  wgpu/WebGL2 后端）；`vello_cpu`（多线程 + SIMD，官方称目前最成熟）用于
  headless 测试与无 GPU 兜底。
- **FFI 边界按帧批处理**（不逐操作跨界）：.NET 合成器每帧序列化一份
  display list（自定义二进制格式：路径、定位好的 glyph run、layer、blend），
  单次 FFI 调用交给 Rust 侧 C ABI shim（cbindgen），由 Vello 建 scene 并渲染。
  每帧一次跨界，开销可忽略；代价是 C shim 与 display list 格式需与 Rust 侧
  同步演进（持续税，非一次性成本）。
- **C# 生态现状**：唯一可用绑定为 wieslawsoltes/VelloSharp（234★，MIT，
  `0.5.0-alpha` 预发布，单维护者）；linebender 官方不提供 C ABI。策略：
  **借其 Rust FFI 层（`vello_ffi`/`kurbo_ffi`/`peniko_ffi`）与全平台原生包 CI
  管线作地基，托管层按自己的 display list 设计走**——或 vendor 其 ffi crate
  进自己的 workspace 以摆脱单维护者风险（详见 08 决策记录的候选评估）。
- **许可证**：Vello 为 MIT/Apache-2.0 双许可，可商用可嵌入。
- **文本管线**：shaping 留在托管侧（HarfBuzzSharp，与布局共用同一 shaping 结果），
  把定位好的 glyph run（字形 ID + 位置 + 字体）放进 display list，字形栅格化交给
  Vello/glifo。spike 待验证：彩色 emoji（COLR/CBDT）、字体 fallback 链、
  glyph 缓存失效策略。
- **风险敞口与对策**：Vello pre-1.0，功能面（路径效果、filter、图像重采样质量）
  与 Skia 尚有差距。对策：M1 spike 按功能清单逐项验证（见里程碑），未过清单的
  功能在框架层降级实现或明确不支持，而不是回退其他后端。

### WebGPU 视口（一等能力，不做 3D 引擎）

**需求定性：跨端统一的 GPU 视口是正式需求，但框架不提供开箱即用的 3D 渲染引擎。**
控件只负责给出“一块 WebGPU 画布”；3D 场景、材质、相机、动画、模型加载都在应用/生态侧。

- **控件形态**：`WebGpuView`（暂定名），普通元素，参与 measure/arrange、hit test、
  裁剪、DPI 与 damage；display list 不增加 3D 命令段。
- **应用契约**：控件向应用暴露 WebGPU 语义的设备/队列与离屏目标纹理，应用自行建管线、
  编码绘制；框架不解释绘制内容。
- **API 形态**：优先直接暴露 VelloSharp 使用的 wgpu 绑定（与 Vello 共享 device/queue），
  **不实现完整的 WebGPU JS API 兼容层**；跨端需要时再在其上做薄封装。
- **合成（默认）**：应用先渲到 offscreen texture → Vello 以 image 绘入视口矩形。
  VelloSharp.Gpu 已提供 `CreateImageFromTexture`，目标是与 Vello 共用同一 device/queue、
  单次 submit；不做独立子窗口 / swapchain overlay（会破坏裁剪、层级与合成）。
- **降级**：CPU/CI 或纹理无法共享时走 readback → `Image.FromPixels`，功能可用、性能不保证。
- **帧调度**：`OnDemand`（应用调用 `RequestFrame`）或 `Continuous`（动画）；
  视口按自身矩形计入脏区，不默认把整窗标脏。
- **输入**：普通元素事件（指针/键盘/滚轮）转发给应用；相机控制与拾取由应用实现，
  **框架不做 3D picking**。
- **非目标**：3D 引擎、场景图、材质系统、相机/动画时间线、glTF 加载、深度拾取、内置 3D 组件。
- **spike（G7-0）**：同 device 纹理导入与单次 submit 实测、纹理生命周期/同步、
  resize/DPI 重建、readback 兜底成本。

## 输入

- 绘制完成即拥有精确 bounds，hit test 是树遍历。
- 路由事件在元素树上 tunnel/bubble（见 04 运行时）。
- 7 个输入/焦点事件（PointerPressed/Released/Moved、KeyDown/Up、GotFocus/LostFocus）已按契约 v2（D11）
  在 C2（2026-11-02）从静态类 `InputEvents` 迁入 `Element`（[Element.cs:118-124](../src/EclipseUI.Runtime/Element.cs#L118)），
  模板可在任何标签上写 `OnPointerPressed={...}` 这类处理器（连同 `OnClick`/`OnCheckedChanged` 共 9 个事件名）。
- 焦点系统 + IME 是深水区：文本编辑器需要 selection / undo / IME 组合事件，
  设计上预留接口，排在里程碑 M4（IME 靠后）。
