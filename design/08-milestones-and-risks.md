# 8. 实施里程碑与风险

按风险排序，先做穿风险最高的部分：

| 阶段 | 内容 | 出口标准 |
|---|---|---|
| **M1 地基**（最高风险） | 文本 shaping（HarfBuzz）+ measure/arrange + **Vello 光栅化 spike（含 2D/3D 同源混排）** | 静态文本+盒子布局渲染正确；Vello 功能清单 + FFI 帧开销验证通过；3D 视口与 Vello 同帧混排跑通 |
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
| **2D/3D 同帧混排** | 3D 渲到 texture → Vello image 绘入视口，同一 device/queue 单次 submit 的实测（3D 为一等需求） |
| **wgpu 直通协议切入点** | 直通回调拿到 surface view / encoder 的方式，能否在不破坏 damage/layer 编排的前提下接入 |

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
| 更新模型 | 编译器生成定向更新 + signals，无运行时 diff | 编译器明知谁变谁更新 |
| 语法层 | 自定义 `.ui` DSL，不用 Razor | HTML 兜底、`@("xxx")`、附加属性无提示等语法债 |
| 基础设施 | 复用 Roslyn（解析/语义/增量生成/Workspace） | 避免自造类型系统与 IDE 智能 |
| IDE 优先级 | VS Code + Rider 第一优先；VS 本体不进 MVP | 两者原生吃 LSP + TextMate，一份 server 两壳接线 |
| 生成器形态 | Roslyn 增量源生成器，生成代码不入库 | 消灭漂移，诊断白嫖编译管线 |
| **渲染后端** | **Vello 单栈**（vello_gpu 主力 / vello_cpu 兜底与 headless），**不引入 SkiaSharp / tgfx** | 与合成器 display list/damage 模型天然对口；immediate-mode 场景即帧语义；**底层 wgpu 提供跨端统一 GPU 栈（含 WebGPU/WebGL 浏览器端），2D/3D 同源混排——跨端渲染正确性只需在一个抽象上兑现一次，此为 3D 一等需求下最硬的理由**；C# 侧有 VelloSharp 可借 FFI 层；代价接受 pre-1.0 功能缺口由 M1 spike 清单把关 |
| **3D 定位** | 3D 视口组件 + wgpu 直通协议 + 可选薄 3D 组件层为一等能力；**不自研完整 3D 引擎** | 完整引擎（场景图/材质/动画）超出 UI 框架职责，交给生态（rend3/three-d/glam/glTF）或用户经直通自带；2D/3D 混排默认走 offscreen texture + Vello image，保住 damage/layer 兼容 |
| 渲染后端候选评估：tgfx | **评估后不采用**（腾讯 C++，BSD-3，功能面/SVG/PDF/滤镜齐、公司级维护） | 否决因：无官方 C API 与 .NET 绑定，绑定层须全自建（C++ 消费子集 50–80 入口起的持续税）；无软件光栅化后端（headless 只能 SwiftShader/GPU CI）；文本 shaping 未证实。其 Metal 后端实为完整实现（README 标注偏保守），不足以单独翻盘 |
| 渲染后端候选评估：SkiaSharp | **评估后不采用**（成熟度最高、纯托管绑定现成） | 与"从头做渲染自有权"的目标冲突；二进制体积与 Skia 生态耦合；若未来 Vello spike 失败可作最后兜底，当前不进架构 |
