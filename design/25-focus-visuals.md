# 25 · 焦点状态与视觉提示

> 状态：**已实施**。2026-11-14。
> 本文记录当前状态、输入、控件与绘制契约。验证命令和结果见[验证报告](./reports/focus-2026-11-14.md)。

## 1. 目标与已解决的问题

目标是让鼠标操作保持简洁，让键盘操作始终能定位当前控件，并允许控件或主题提供合适的提示形式。

实施前的问题：

1. 原 FocusManager 只记录 IsFocused，未区分鼠标、键盘和程序调用。
2. ElementPainter 看到 Focused 状态便画统一外圈，默认宽度 2。
3. TextBox 自绘又按 IsFocused 改变自身边框颜色，出现边框高亮加外圈的重复效果。
4. DatePicker 等复合控件的真实焦点在内部编辑器，外壳没有统一的 focus-within 状态。
5. 绘制器的 focused 参数还能独立制造焦点外观，与运行时状态形成两个入口。

方案分开三个职责：FocusManager 维护状态与来源；输入路由更新交互方式；控件/主题消费状态决定外观。首版利用现有属性、状态位和绘制扩展完成，不依赖 design/23 整体样式重构，也不引入新的 UI 所有权树。

## 2. 用户可见行为

### 2.1 默认控件表现

| 控件 | 鼠标获得焦点 | 键盘获得焦点 |
|---|---|---|
| Button、ToggleButton | 保留 hover/pressed 等反馈，不额外画外圈 | 显示一层清晰的焦点圈 |
| CheckBox、RadioButton、ToggleSwitch | 显示控件自身的交互状态 | 显示焦点圈；选中/勾选状态另行保留 |
| Slider | 正常轨道/滑块反馈 | 显示可辨认的焦点提示，默认由框架画圈 |
| TextBox、PasswordBox、NumericUpDown | 自身边框高亮、光标/选区正常显示 | 使用同一套自身焦点边框，取消额外外圈 |
| DatePicker、ComboBox | 外壳根据 focus-within 显示自身焦点边框 | 由外壳提供焦点提示，内部按钮/编辑器不重复叠圈 |
| ListBox、TreeView、DataGrid | 选择状态与鼠标反馈照常显示 | 提示当前导航位置；选择色不代替焦点提示 |
| TabControl、Menu | 显示当前页/悬停等状态 | 提示当前页签头或菜单项，不圈住整个内容区 |
| 普通布局容器 | 无新增提示 | 除非显式成为交互控件，否则不参与焦点与描边 |

列表、树和表格需要把“选中”与“当前导航项”区分开。实现时通过控件的焦点视觉目标/几何提供器，把提示落在当前行、节点、单元格或页签头；逻辑焦点仍可由现有容器管理。没有当前导航项时，才回落到容器边界。

### 2.2 同一控件内切换输入方式

- 鼠标点击按钮：按钮可以持有焦点，但不出现外圈。
- 随后按 Space、Enter 或导航键：即使焦点元素没有变化，也启用键盘提示。
- 再次鼠标按下：普通按钮的外圈消失，逻辑焦点无需离开。
- 鼠标仅移动或滚轮滚动：不隐藏键盘提示。
- 点击输入框：仍显示自身边框与光标，不能把 focus-visible 简化为“鼠标一律无提示”。
- 程序把焦点移到新控件：默认沿用当前输入方式；新弹出的对话框可显式要求显示提示。

## 3. 状态与 API

### 3.1 三种只读状态

| 状态 | 含义 | 样式伪类 |
|---|---|---|
| IsFocused | 当前元素直接接收键盘输入 | `:focus` |
| IsFocusWithin | 当前元素或实际内容树中的后代拥有焦点 | `:focus-within` |
| IsFocusVisible | 当前元素拥有焦点，且按交互策略需要清晰的焦点提示 | `:focus-visible` |

约束：

- IsFocusVisible 必须蕴含 IsFocused；IsFocused 必须蕴含 IsFocusWithin。
- 非可聚焦容器可以有 IsFocusWithin，但不因此获得焦点。
- IsFocusVisible 表示需要提示，不承诺必须绘制“框架外圈”。TextBox 即使鼠标聚焦也可为 true，由自身边框满足提示需求。
- 三种状态由 FocusManager 维护，均不提供模板赋值或双向绑定入口。
- `:focus` 保持原义，不改成“仅键盘焦点”；现有 Focus(...) 样式继续响应任何来源的直接焦点。

新增 PseudoStates.FocusVisible、FocusWithin 平台位；同步移动自定义状态分配起点，预留平台名。编译器、运行时和样式输出一起更新，不允许手写或持久化状态掩码数字。

### 3.2 焦点来源与输入方式

公开入口：

```csharp
enum FocusOrigin { Auto, Pointer, Keyboard, Programmatic, Restore }
enum FocusVisibility { Auto, Show }

bool FocusManager.Focus(Element? target,
    FocusOrigin origin = FocusOrigin.Auto,
    FocusVisibility visibility = FocusVisibility.Auto);

bool Element.RequestFocus(
    FocusOrigin origin = FocusOrigin.Auto,
    FocusVisibility visibility = FocusVisibility.Auto);
```

FocusManager 额外保存当前根的输入方式 Keyboard/Pointer，以及最后一次焦点转移来源。二者不同：Programmatic、Restore 是转移来源，不是第三、第四种用户输入方式。

- Auto 在同步输入分发期间继承该次事件的来源；在分发外视为 Programmatic。
- Programmatic 默认沿用当前输入方式；新根的初始方式设为 Keyboard，使初始化焦点有明确提示。
- Show 是某次请求的显式提示要求，保持到下一次明确输入方式变化或焦点转移。
- 首版不提供按请求 Hide；替换提示形式通过下一节的渲染模式处理。
- 根级 AlwaysShowFocusVisuals 覆盖输入方式推断，供应用/平台的始终显示偏好使用；不是进程静态开关。

Focus(target) 和 RequestFocus() 的现有调用保持可用。GotFocus 的事件参数扩展来源及修饰键，保留 RoutedEventArgs 处理器形态。

### 3.3 可配置的是外观职责

Element.FocusVisualMode 是可样式化的 StyledProperty：

| 值 | 行为 |
|---|---|
| Auto | 使用控件声明的默认方式 |
| Outline | IsFocusVisible 时由框架绘制外圈 |
| Control | 由控件自身提供焦点外观，框架不额外叠圈 |
| None | 不提供框架提示，仅供已有替代提示的定制场景 |

默认 Element 使用 Outline；文本编辑器和输入型复合控件使用 Control。默认选择通过可覆盖的控件能力提供，不能在构造函数中写 local 值，否则 class 样式无法覆盖。

模式改变不修改 IsFocused、IsFocusWithin 或 IsFocusVisible。AlwaysShowFocusVisuals 强制“需要提示”，不强制把 Control 模式改回外圈。Control/None 的作者仍需保证键盘使用时能看清焦点位置。

文本输入能力由控件声明，例如虚拟只读 IsTextInputClient；TextBox 及其派生类提供该能力，包括仍支持键盘选择的只读编辑器。输入系统不根据具体类型名硬编码判断。

## 4. 输入与状态更新规则

### 4.1 输入方式何时变化

| 输入/操作 | 更新方式 | 处理时机 |
|---|---|---|
| PointerPress | Pointer | 关闭外部浮层和选择目标之前 |
| KeyPress 的有效非修饰键 | Keyboard | Escape 处理、Preview 路由和控件处理之前 |
| PointerMove / Wheel | 不改变 | 不因手移鼠标或滚动打断键盘提示 |
| KeyRelease / 单纯修饰键 | 不改变 | 不产生额外提示切换 |
| TextInput / IME 组合事件 | 不单独改变 | 虚拟键盘输入不能误判为物理键盘导航 |
| 方向键、Tab、快捷键调用 RequestFocus | Keyboard | 通过分发上下文继承，或显式传入 |

来源上下文只覆盖同步分发，不跨 await 保留；异步恢复在执行时沿用当前方式，确有需要时显式传 Origin/Show。每个根独立维护，两个窗口互不污染。

WindowHost 当前会在 InputRouter.KeyPress 之前处理 Escape，因此平台入口也必须先通知 Keyboard 方式；Headless 直接调用 KeyPress 时执行同一规则。方式未变时更新应短路。

### 4.2 IsFocusVisible 的默认计算

在元素仍有效、可见、启用且直接拥有焦点的前提下：

```text
IsFocusVisible =
    AlwaysShowFocusVisuals
    或本次请求要求 Show
    或元素声明为文本输入客户端
    或当前输入方式为 Keyboard
```

FocusManager.Focus(同一元素) 不能在重新计算提示状态之前直接返回。同一元素的提示变化只重绘/重算样式，不重复发送 GotFocus/LostFocus，也不影响光标、文本选区或绑定状态。

### 4.3 状态提交与实际树

一次转移先验证目标未释放、有效可见/启用且属于本根，然后提交 Focused 引用、旧/新元素状态与祖先 focus-within 状态，最后发出 LostFocus/GotFocus。

同一元素的多个状态应批量应用，避免先出现 `focus=true, focus-visible=false` 的中间样式。重入焦点请求排队，在本次提交结束后处理；设置有界处理上限，防止事件互相聚焦形成死循环。

FocusManager 缓存当前焦点的实际祖先路径，更新旧/新路径差异：

- 同祖先内切换子控件，公共祖先的 focus-within 保持 true，避免外壳闪烁。
- 摘除/释放后仍能清掉原祖先的状态，不能在 Parent 已为空后才尝试反推旧路径。
- 同根内移动焦点元素时重新核对祖先路径；跨根移动清除原根焦点，新根显式接收。
- FocusWithin 依据实际父子关系，插槽、普通控件、Popup 内容使用相同规则；祖先路径缓存不形成第二棵树。

首期保持 WindowHost 失焦时的 Reset 行为：清除焦点、提示和祖先状态，不在这里顺带实现多焦点作用域或窗口激活恢复。

### 4.4 浮层关闭和焦点恢复

Popup 保存原焦点目标，关闭后验证目标仍在原根且有效，再请求 Restore。恢复沿用关闭时的输入方式：

- Escape 关闭：先切成 Keyboard，恢复后的按钮显示提示。
- 点击外部关闭：先切成 Pointer，普通按钮恢复焦点后不突然显示外圈。
- 程序关闭：沿用当前方式；需要强制提示时，可通过 Focus(target, Restore, Show) 显式请求。
- 外部点击接着聚焦其他控件时，在同一输入批次完成，不绘制短暂的中间外圈。
- 原目标已失效时清除恢复记录，不聚焦到树外对象。

不要只保存打开时的 IsFocusVisible 并原样还原；用户可能在弹窗中切换输入方式。恢复记录在关闭/释放后释放对旧目标的引用。

## 5. 控件与绘制分工

### 5.1 一个提示出口

Outline 焦点源每帧解析一次内部 FocusVisualDescriptor，包含：实际绘制目标、局部范围、圆角、模式、颜色、宽度和偏移。实际绘制、retained 缓存及 damage 都消费同一个结果。Control 模式按外壳的 focus-within 生成自身装饰段，因此下拉列表持有焦点时，输入外壳仍可保持边框提示。

默认目标是自身。复合控件可以把视觉目标指向本根内的祖先外壳，列表/页签等也可以提供本控件范围内的当前项几何；不得指向无关窗口、任意旁支或已释放元素。改变视觉目标不转移逻辑焦点，也不把父子两个节点都当作焦点源绘制。

对于 Control 模式：

- TextBox 保留自身边框高亮、光标与选区，删除框架外圈叠加。
- DatePicker/ComboBox 外壳使用 focus-within，不依赖内部焦点目标是否恰好为某种按钮。
- 第三方控件可以在 Render(IElementDrawingContext) 消费只读状态；需要覆盖子内容的自身提示可重写 RenderFocusVisual(IElementDrawingContext)。该回调只在 Control 模式、focus-within 且由自身负责提示时执行，无需修改 ElementPainter 的类型 switch。
- FocusVisualTarget 默认返回自身；复合控件内部委托给祖先外壳。GetFocusVisualGeometry 返回 FocusVisualGeometry(Target, Bounds, CornerRadius)，Bounds 使用 Target 局部坐标，Target 限于绘制所有者自身或后代。
- AlwaysShowFocusVisuals 通过 FocusManager 配置；Windows 宿主支持 Window / WindowHostOptions 同名选项。

### 5.2 框架外圈

- Outline 只有在 IsFocusVisible 时绘制；宽度为 0 时不生成命令。
- 颜色/宽度/偏移读取样式有效值，回落主题令牌；圆角复用控件装饰几何，避免控件与外圈半径不一致。
- 外圈不参加 Measure/Arrange，不抢命中，不创建一个可聚焦的 UI 子元素。
- 绘制顺序为自身内容 → 子内容 → 焦点装饰；外圈在自身裁剪退出之后、祖先裁剪退出之前输出，不绕过 ScrollViewer 的视口约束。
- Popup 仍按既有顶层叠加顺序绘制，普通控件焦点圈不能盖住后来绘制的浮层。
- 滚动区域可通过控件内缩几何或偏移样式保持提示完整，不能靠取消祖先裁剪解决边缘问题。

Segment 分别保存自身命令 Commands 与 FocusCommands，仍属于同一元素的缓存记录，按上述顺序输出。无需新增可视树或整窗焦点覆盖层。

### 5.3 缓存与脏区

- Focused/FocusVisible/FocusWithin 变化都走受控状态失效；提示变化可以发生在同一焦点对象上，不能只比较 Focused 引用。
- 模式、目标范围、圆角、宽度、偏移变化进入描述符/段键或显式 RenderVersion 失效。
- damage 覆盖旧/新提示的实际描边范围与抗锯齿边缘；从显示变为隐藏也要擦掉旧圈。
- 对矩形外圈，向外扩展至少为 `max(0, offset + width / 2) + AA余量`，不能固定假设 offset 等于 width。
- Control/None 不产生虚假的外圈脏区；主题切换仍按现有重建路径处理。
- 静止帧不遍历整棵树重新判定输入方式；通常只更新当前焦点及其祖先路径。

### 5.4 移除绘制器中的第二焦点来源

生产绘制统一读取元素状态。Build/BuildFrame 的 focused 参数及“强行 OR Focused”的分支已删除，DamageTracker 不再依赖该参数推断真实焦点。

测试、截图通过 HeadlessRoot.Focus.Focus(..., Keyboard) 或 Pointer 建立状态，再绘制真实树；焦点不会因“绘制一次”被临时改变。UI/C# 控件和原生/无头宿主共用同一状态来源。

## 6. 样式与目录

首版增量接入现有样式系统：

- BssVariant、Parser 平台伪类表、Generator 映射及 ClassStyleVariants 同时新增 FocusVisible/FocusWithin。
- `.Focus(...)` 保持当前行为；新增 `.FocusVisible(...)`、`.FocusWithin(...)`，无需应用自行 @states 注册。
- FocusVisualMode 枚举属性通过已有 UiStyleProperty 枚举通道发布；当前 StyleBuilder 的内建扫描只支持颜色/数字，不直接新增一个未适配扫描器的枚举 setter。
- FocusRingWidth 保留；补充 FocusRingColor（含 theme 资源）、FocusRingOffset，并通过同一符号目录进入 Class/inline/IDE。
- 状态只读，不进入可写属性或 @bind 目标；拼写错误按现有 EUI 诊断路径拒绝。

以下写法已通过真实目录、模板生成和编译测试：

```scss
.action {
    FocusVisualMode: Outline;
    FocusRingWidth: 2;
    FocusRingOffset: 2;
    FocusRingColor: theme(#7ebaff, #3476ff);

    &:focus-visible {
        CornerRadius: 8;
    }
}

.input-shell {
    FocusVisualMode: Control;
    &:focus-within {
        // 外壳边框属性在控件/主题中消费；不再另外配置一层框架外圈。
    }
}
```

design/23 的未来状态目录与统一装饰阶段采用本方案语义；本阶段不提前加入其资源字典、完整选择器或任意装饰模板。

## 7. 实施切片与验收

| 切片 | 主要修改 | 出口 |
|---|---|---|
| F1 状态与来源 | Element、FocusManager、PseudoStates、Registry、InputRouter；WindowHost 输入前置通知 | Pointer/Keyboard/Programmatic/Restore 规则正确，同一元素切换方式生效，根隔离、树清理与重入可验证 |
| F2 样式与元数据 | BssAst/Parser/Generator、StyleRegistry、Visual、元数据/IDE 目录与 golden | 三种伪类可编译、补全，focus 原义保持，模式和外圈参数可覆盖 |
| F3 视觉与控件 | FocusVisualDescriptor、ElementPainter/FrameDamage、TextBox、复合控件、Popup 恢复、集合/页签目标几何 | 默认控件不重复描边，提示在正确位置，内容/裁剪/缓存/脏区一致 |
| F4 迁移与演示 | 迁移 focused 参数调用；Gallery、Headless/Rendering/Compiler/IDE 回归及原生验证壳；同步 spec/设计入口 | 深浅主题、原生与无头行为一致，旧圈无残影、静止帧无新增成本 |

F1–F4 已完成。验收场景：

1. 鼠标点按钮 → 按 Space → 再点按钮：外圈隐藏/显示/隐藏，GotFocus 只因实际转移发生。
2. Tab/Shift+Tab、方向键、Ctrl+Tab、菜单快捷键都显示正确目标；单纯移动鼠标/滚轮不熄灭提示。
3. 鼠标/键盘进入各类编辑器，都有一套清晰的自身焦点边框，光标和选区不受切换影响。
4. 复合控件内部切换 focus-within 不闪烁，列表/树/表格/页签提示当前导航位置而非整个内容区。
5. Escape 和外部点击关闭浮层分别恢复相应方式；嵌套浮层、原目标失效、窗口失焦无悬挂状态。
6. 状态来源与伪类、C# 样式、模板、IDE 候选一致；父禁用、隐藏、摘除、释放和移树全部清理。
7. 相同焦点对象仅改变 FocusVisible 时产生正确 damage；外圈从有到无、宽度/偏移/半径切换无旧像素残留。
8. 静止帧 retained 复用；同祖先内切换只更新必要路径；第三方控件自绘和显式关闭默认圈均有替代提示示例。

## 8. 参考与范围

设计参考：

- [WPF FocusVisualStyle](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle)：区分键盘焦点装饰与控件自己的焦点状态外观。
- [Avalonia Focus](https://docs.avaloniaui.net/docs/input-interaction/focus)：focus、focus-within、focus-visible 与 FocusAdorner 分离，避免自绘提示与默认装饰重复。
- [CSS Selectors：focus-visible](https://www.w3.org/TR/selectors-4/#the-focus-visible-pseudo)：输入方式与文本编辑能力共同决定提示，程序转移继承上下文；其启发式是参考，不声称 BubuUI 实现浏览器全部行为。

本方案不扩展 TabIndex/FocusScope、无障碍树、手柄设备、完整控件模板系统。保留相应接口演进空间，避免把解决焦点描边变成新的样式引擎重构。
