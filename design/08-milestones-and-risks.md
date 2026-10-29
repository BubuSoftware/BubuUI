# 8. 实施里程碑与风险

按风险排序，先做穿风险最高的部分：

| 阶段 | 内容 | 出口标准 |
|---|---|---|
| **M1 地基**（最高风险） | 文本 shaping（HarfBuzz）+ measure/arrange + **Vello 光栅化 spike（含 GPU 纹理混排可行性）** | 静态文本+盒子布局渲染正确；Vello 功能清单 + FFI 帧开销验证通过；WebGPU 纹理 → Vello 的混排路径可行（真实共享纹理留 G7-0） |
| **M2 编译器 MVP** | Lexer/Parser/语义/代码生成；Signal 系统 + 定向更新 | 模板编译出可执行 RenderUnit |
| **M3 垂直切片** | 计数器、列表增删、表单绑定——`.ui` 到像素全链路 | 三类场景跑通 |
| **M4 输入与焦点** | hit test、鼠标键盘、焦点遍历，然后 IME | 可交互 demo |
| **M5 样式主题** | 选择器编译（AOT 化）、亮暗主题、资源查找 | 主题切换 demo |
| **M6 IDE P0–P1** | grammar + 生成器诊断映射 → LSP 补全/悬停/跳转 | VS Code、Rider 同日同版发布 |
| **M7 工具链** | 热重载、无头测试 SDK、`#line` 调试映射、DevTools | 改 UI 不丢状态；CI 可跑无头测试 |
| **M8 平台扩展** | 第二、第三后端，验证平台抽象层 | 至少两个平台跑通 |

## M1 Vello spike 验证清单

渲染后端已定为 Vello 单栈（无 SkiaSharp / tgfx 回退），M1 必须逐项过：

| 待验证项 | 说明 |
|---|---|
| dash 描边 | 虚线边框是桌面 UI 常态 |
| 模糊/阴影 | 卡片、弹出层的 drop shadow |
| conic/sweep 渐变 | 图表、加载指示器 |
| clip 路径 + 复杂 blend | 圆角裁剪、混合模式 |
| 图像重采样质量 | 缩放图片的视觉质量 |
| 彩色 emoji / 字体 fallback | 文本管线完整性（glifo） |
| FFI 帧开销 | display list 序列化 + 单次调用的实测成本 |
| **WebGPU 纹理混排** | 应用渲到 offscreen texture → Vello 以 image 绘入视口。G1 用 readback 做了语义验证；同一 device/queue 的共享纹理留 G7-0（控件为一等能力，**不做 3D 引擎**） |
| **WebGPU 控件切入点** | 向应用暴露 device/queue 与目标纹理的方式，纹理生命周期/同步如何接入 damage 编排 |

未过清单的功能在框架层降级实现或明确不支持，**不回退 SkiaSharp / tgfx**。

## 两个最贵的资产

立项头两个月就要主攻，而不是留到最后补：

1. **文本排版**（最容易低估的工程量）——M1 先做穿。
2. **带 Roslyn 投影的 LSP server**（仅次于文本排版）——全职人力半年以上到 P2。
   资源紧张时的正确压缩方式：砍 IDE 功能档位（先 VS Code 单平台），
   **不砍编译器诊断质量**——诊断是底线，补全是加分。

## 决策记录汇总

| 决策点 | 结论 | 推翻/依据 |
|---|---|---|
| 组件运行时 | **不复用** Blazor 运行时 | 模板编译器成为自有资产后，运行时 diff 是中间商 |
| 更新模型 | 编译器生成保留元素树 + 组件级失效重算（普通 CLR 状态），无运行时 diff/读取追踪 | 编译期已知动态位置；`Build()` 只跑一次，失效只重算绑定与块 |
| 语法层 | 自定义 `.ui` DSL，不用 Razor | HTML 兜底、`@("xxx")`、附加属性无提示等语法债 |
| 基础设施 | 复用 Roslyn（解析/语义/增量生成/Workspace） | 避免自造类型系统与 IDE 智能 |
| IDE 优先级 | VS Code + Rider 第一优先；VS 本体不进 MVP | 两者原生吃 LSP + TextMate，一份 server 两壳接线 |
| 生成器形态 | Roslyn 增量源生成器，生成代码不入库 | 消灭漂移，诊断白嫖编译管线 |
| **渲染后端** | **Vello 单栈**（vello_gpu 主力 / vello_cpu 兜底与 headless），**不引入 SkiaSharp / tgfx** | 与合成器 display list/damage 模型天然对口；immediate-mode 场景即帧语义；**底层 wgpu 提供跨端统一 GPU 栈（含 WebGPU/WebGL 浏览器端），WebGPU 视口控件与 2D 共用同一 GPU 栈——跨端渲染正确性只需在一个抽象上兑现一次，此为 GPU 视口一等能力下最硬的理由**；C# 侧有 VelloSharp 可借 FFI 层；代价接受 pre-1.0 功能缺口由 M1 spike 清单把关 |
| **WebGPU 视口定位** | **只提供 WebGPU 画布控件**（布局/裁剪/DPI/输入 + 离屏纹理交给 Vello 合成）；应用自带渲染器；**不做 3D 引擎** | 3D 引擎（场景图/材质/相机/动画/拾取）超出 UI 框架职责；控件级暴露保持渲染契约简单，display list 不出现 3D 命令段。G1 的 readback 混排已验证语义，G7-0 验证同 device 共享纹理 |
| **控件元数据事实源** | **从 C# 符号派生**（`StyledProperty`/`RoutedEvent`/`[Ui*]` 特性）；目录不手写；编译期不执行用户程序集 | 手写目录与控件元数据双写必然漂移；第三方控件无法进入模板；详见 [14](./14-control-template-contract.md) |
| **控件/模板契约** | 控件属性/事件默认暴露 + `[UiIgnore]`；组件参数 opt-in；内容模型显式声明；组件 v1 无参数 diff（参数是普通 CLR 属性，推送 + 相等保护 + 子 `Invalidate()`；双向用 `{P}Changed`） | 与 04 的"无参数 diff"一致；`StyledProperty` 注册已是公开声明，避免第二份元数据；决策 D1–D21 见 14.14 |
| **契约可实现性修订（v2，2026-10-29）** | 事件字段必须声明在元素/组件类型上（`InputEvents` 7 个事件迁入 `Element`）；必填参数用 `[UiParameter(Required=true)]` 而非 C# `required`；默认值/`Invalidate` 划为运行期事实、不进编译期目录；目录缓存交给增量管道的值比较 | 逐项实测：静态宿主类使模板事件只剩 1 个（能力对等破）；`required` + `new C()` 编译失败 CS9035；`Register(...)` 实参编译期不可读；生成器不应持可变状态。详见 14.15 |
| **状态与反应式契约（v4，2026-10-29）** | 普通 CLR 状态 + 组件级失效重算：`Build()` 保留元素树并首评，`Invalidate()` → `Render()` 重算动态表达式，`@if`/`@foreach` 显式 `Refresh()`；`@bind-P` 双向、组件 `{P}Changed`；EUI045；S1–S5 分阶段 | 状态/集合类型不受限、无运行时读取追踪；v3 的 `[UiState]`/`UiWatch`/依赖分析整体废止（design/15 停止）。详见 [spec/03](../spec/03-reactivity-and-binding.md) |
| 渲染后端候选评估：tgfx | **评估后不采用**（腾讯 C++，BSD-3，功能面/SVG/PDF/滤镜齐、公司级维护） | 否决因：无官方 C API 与 .NET 绑定，绑定层须全自建（C++ 消费子集 50–80 入口起的持续税）；无软件光栅化后端（headless 只能 SwiftShader/GPU CI）；文本 shaping 未证实。其 Metal 后端实为完整实现（README 标注偏保守），不足以单独翻盘 |
| 渲染后端候选评估：SkiaSharp | **评估后不采用**（成熟度最高、纯托管绑定现成） | 与"从头做渲染自有权"的目标冲突；二进制体积与 Skia 生态耦合；若未来 Vello spike 失败可作最后兜底，当前不进架构 |
