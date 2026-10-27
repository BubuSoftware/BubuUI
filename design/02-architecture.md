# 2. 总体架构

核心理念：**模板不是"字符串拼 UI"的外壳，而是编译单元**——编译期完成尽可能多的工作
（类型检查、字面量转换、更新路径分析），运行时只执行生成好的指令。

## 分层架构

```
┌─ 模板层   .ui 文件：词法/语法/语义检查 → 编译为 C# 渲染指令（Roslyn 增量生成器）
├─ 运行时   实例模型：模板实例 = 编译代码 + 状态对象；细粒度响应式更新（无运行时 diff）
├─ 元素系统 Element 树 + StyledProperty 属性引擎（唯一真相源）
├─ 布局     measure/arrange 双遍，文本 shaping 参与测量，虚拟化一等公民
├─ 渲染     Scene Graph → 合成器（damage/layer/动画）→ Vello 光栅化（vello_gpu 主力 / vello_cpu 兜底）
├─ 3D       3D 视口组件 + wgpu 直通协议：同一 device/queue 上与 Vello 2D 同源混排（3D 为一等需求）
├─ 输入     hit test、路由事件、焦点、IME
└─ 平台     窗口/输入/剪贴板后端：Win32、macOS、X11/Wayland（每平台一个宿主）
```

## 单向管线

消灭 Blazonia "UpdateDisplayAsync 同步应用 edits" 的问题：

```
事件/异步完成 → signals 变更 → 定向更新函数改 Element 属性（标脏，纯逻辑可重入）
  → 帧回调（vsync）：measure → arrange → build scene → paint
```

逻辑更新与物理渲染解耦，天然批处理，也为合成器线程（动画在 UI 线程外跑）留了口子。

## 与 Blazor 的根本分岔：编译器生成式更新

三种候选更新模型：

- Blazor 式：重跑整个渲染函数，运行时 diff 序列树；
- Vue 式：virtual DOM diff；
- **编译器生成式（本设计采用）**：模板编译时已知每个绑定点对应哪个元素的哪个属性，
  直接生成定向更新函数；`@if`/`@foreach` 编译为带 Key 的块级协调器，
  列表更新 O(变动数)。

选择依据：模板-first 框架里编译器拥有全部结构信息，把 diff 留给运行时是浪费。

状态原语采用**显式 signals**（读取收集依赖、写入触发定向更新），优于 Vue proxy 魔法：
可预测、可静态分析、AOT 安全。

## 边界声明

- **自持**：`.ui` 语法、编译器、运行时、元素系统、布局、渲染管线、LSP。
- **复用**：仅 Roslyn 作为基础设施。
- **不复用**：Blazor 运行时与 Razor 语法层——一旦模板编译器成为自有资产，
  Blazor 运行时（组件 diff、RenderTree 序列、EventCallback）就是中间商。
