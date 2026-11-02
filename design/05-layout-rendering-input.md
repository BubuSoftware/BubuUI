# 5. 布局、渲染与输入

## 布局

- measure/arrange 双遍（constraint-based），脏标记只重排子树。
- **文本先 HarfBuzz shaping 再测量**，保证布局所见即绘制所得——文本是自绘框架
  最容易低估的部分，参与布局而非事后再排。
- 虚拟化一等公民：`VirtualizingPanel` 在元素层实现容器复用，item 模板编译为独立
  RenderUnit 按容器实例化——Blazonia "Blazor 急切渲染所有 item vs 平台惰性容器"
  的错位问题不存在。

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
