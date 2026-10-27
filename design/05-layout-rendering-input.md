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

### 3D 管线（一等能力，非远期）

**需求定性：跨端统一的 3D 接口是正式需求。** 这正是选 Vello/wgpu 栈的战略理由——
wgpu（Vulkan/Metal/DX12/WebGPU/WebGL 的统一抽象）同时承载 Vello 的 2D 与自写/集成
的 3D，**2D/3D 同源混排是 tgfx/Skia 栈物理上给不了的能力**。

- **框架定位（范围假设，可调整）**：提供 **3D 视口组件 + wgpu 直通协议 + 可选的
  薄 3D 组件层**；**不自研完整 3D 引擎**（场景图/材质系统/动画系统不在 UI 框架
  的职责内，交给生态：数学 glam、模型 glTF、PBR 可集成 wgpu 系 3D 库如
  rend3/three-d，或用户经直通协议自带渲染器）。
- **混排模式（默认）**：3D 内容先渲到 offscreen texture → Vello 以 image brush
  绘入视口矩形。理由：Vello 天然支持 image，与 damage/layer/transform/裁剪完全
  兼容；备选同 surface 分 render pass + scissor（省一次 copy，留作优化项）。
- **帧编排**：2D 与 3D 在同一 device/queue，单次 submit 完成一帧；3D 视口是
  **恒脏 layer**（持续动画，每帧重绘），合成器对视口区域做直通管理。
- **数据流**：display list 增加 3D 命令段（视口矩形、纹理句柄、直通回调索引），
  FFI 面相应扩展；3D 绘制指令本体不进 2D display list，由直通协议单独携带。
- **输入与拾取**：视口内鼠标/触摸事件转发至 3D 层；深度拾取提供两档——
  CPU readback（简单场景）与 GPU picking（离屏 id 渲染），接口在框架层统一。
- **跨端**：wgpu 统一兑现——Windows(DX12/Vulkan)、macOS(Metal)、Linux(Vulkan)、
  移动端、浏览器(WebGPU/WebGL) 一套代码；**跨端渲染正确性只需在一个抽象上兑现一次**。
- **spike 验证**（并入 M1，见里程碑）：2D/3D 同帧混排、3D 纹理喂回 Vello 的
  帧开销、直通协议在 wgpu 现有帧结构（surface view/encoder）上的切入点。

## 输入

- 绘制完成即拥有精确 bounds，hit test 是树遍历。
- 路由事件在元素树上 tunnel/bubble（见 04 运行时）。
- 焦点系统 + IME 是深水区：文本编辑器需要 selection / undo / IME 组合事件，
  设计上预留接口，排在里程碑 M4（IME 靠后）。
