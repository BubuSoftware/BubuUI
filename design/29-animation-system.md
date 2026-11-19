# 29 · 动画系统

> 状态：属性过渡、`.eustyle` 描述、Fluent 开关动画及三平台帧接入已实施。关键帧、物理动画、通用变换和独立合成线程仍是后续工作。本文记录现有能力及其边界。

## 使用 `.eustyle`

在样式基础规则中声明过渡，状态规则只负责设置目标值：

```scss
.card {
    Background: #202020;

    @transition Background {
        Duration: 150ms;
        Easing: CubicOut;
    }

    &:hover { Background: #404040; }
}
```

将该样式表按项目已有方式编译并安装，并给元素设置 `Class="card"`。鼠标进入和离开都会触发过渡；中途反向时从当前显示值继续。该语法也可用于 `@theme` 的基础规则。

单行简写等价于上述块：

```scss
.card {
    Background: #202020;
    transition: Background 150ms CubicOut;
    &:hover { Background: #404040; }
}
```

`Duration` 必需，接受非负有限数值及 `ms` 或 `s` 单位；`0ms` 直接显示目标。块形式省略 `Easing` 时默认为 `Linear`，可选 `Linear`、`CubicOut`；简写须提供缓动名。可以为不同属性分别声明过渡。过渡声明只能写在基础规则，不能写在 `:hover` 等状态变体内；重复声明按样式级联及后者优先处理。

声明必须解析到允许动画的视觉属性，编译器检查未知属性、类型、时长和曲线。不能直接过渡 `Checked`、布局属性或其他没有开放动画的属性。当前可用的视觉属性包括背景和圆角；不同样式通道可能绑定到不同属性：传统 class 样式背景使用 `Visual.BackgroundProperty` 或主题色槽，控件主题使用相应控件的 `StyledProperty`。

class 样式背景两端均为静态颜色时可过渡；两端均为 `theme(dark, light)` 时，分别插值深色和浅色分支。例如：

```scss
.card {
    Background: theme(#202020, #eeeeee);
    transition: Background 150ms CubicOut;
    &:hover { Background: theme(#404040, #cccccc); }
}
```

现有绘制优先级是主题资源色高于静态颜色。静态色与 `theme()` 分属不同属性槽，不支持两种槽之间的平滑过渡；同一 class 混用会产生编译诊断，跨 class 时也应避免混用。只改变系统亮暗模式时，直接选择 `ThemeColor` 对应分支，不自动在两分支之间做动画。`resource()` 与这些槽之间的过渡也不在当前通用保证范围内。

## C# 入口

以下代码假设 `card` 是已挂接到宿主的 `Border`。配置不自动播放；下次属性基础值改变时启动：

```csharp
using EclipseUI;
using EclipseUI.Elements;

Transitions.Set(card, Border.BackgroundProperty,
    new TransitionSpec(TimeSpan.FromMilliseconds(150), Easings.CubicOut));

card.Background = new UiColor(70, 130, 220);

// 按需要移除配置，取消当前过渡并显示基础值。
Transitions.Remove(card, Border.BackgroundProperty);
```

框架直接提供 `double` 和 `UiColor` 插值，并可通过 `TransitionInterpolators.Register<T>` 注册其他类型的插值器。运行时要求属性的 `PropertyOptions` 包含 `Animatable = true`、失效级别为 `Render` 且不继承。供 `.eustyle` 编译器识别的属性字段还需标注 `[UiAnimatable]`；自定义类型也必须提供可用的插值器。不可插值的端点（如非有限 `double` 或为空的颜色槽）直接切换到目标。

Fluent `ToggleSwitch` 的 `Checked` 立即变化；视觉部件的数值进度控制圆点位置，配置的过渡从当前进度重定向，快速反向不会跳回原起点。该进度是部件视觉状态，不改变业务属性的含义。

## 值与级联

显示值优先读取活动动画覆盖，再读取基础值。基础值仍按现有 `local > template > class > rule > controlTheme > skin > inherited > theme/default` 解析。`GetValue` 返回当前显示值，`GetBaseValue` 返回基础目标；动画帧不写入 local 层。双向绑定读取基础值并忽略逐帧通知，因此不会把中间值写回源。

同一元素和属性同一时刻最多一个活动过渡。目标在动画中改变时，从当前显示值重新开始一个完整时长的过渡；完成或取消时移除覆盖，显示最新基础值。动画本身不保证反向时速度连续。配置优先级为 C# 本地配置高于 class，高于 rule，再高于 control theme；同一来源内后应用的声明胜出。

class、规则和主题重算按批提交，避免先清理旧样式时的临时默认值触发动画。首次模板和主题应用使用初值，不播放从默认值进入的过渡。动画显示值变化仍通过属性通知刷新模板，可能带来模板更新成本。继承属性暂不支持过渡。

## 帧调度和生命周期

Windows、macOS、Linux 宿主用单调时钟在 UI 线程推进动画，再布局和绘制。当前视觉树根维护活动过渡元素集合；新过渡按活动集合推进并过滤指定子树，原有 `IFrameAnimation` 则继续遍历可见视觉树，兼容光标、不确定进度条和滚动条重复行为。空 damage 时可以跳过 Present，但宿主仍有消息循环，活动集合并不等于完整的按需唤醒或空闲休眠机制。

不可见元素及关闭的 Popup 的活动过渡暂停；隐藏时基础目标变化则取消旧过渡并直接保存最新目标。窗口没有可绘制尺寸期间，新过渡暂停，恢复时不追赶最小化时长。元素摘除或销毁时取消活动过渡、清除覆盖和旧根的登记；重新挂接不会继续旧实例。借入模板内容仍遵循它本来的生命周期。

`FrameAnimations.SetEnabled(root, false)` 禁用该元素子树的过渡并结束现有实例；重新启用只作用于后续变化。`FrameAnimations.SetPaused(root, true)` 暂停子树的新过渡；恢复用 `false`。当前不会自动读取系统减少动态效果偏好，应用需显式设置。

无头测试可以精确推进时间：

```csharp
using var host = new HeadlessRoot(card);
Transitions.Set(card, Border.BackgroundProperty,
    new TransitionSpec(TimeSpan.FromMilliseconds(200), Easings.Linear));
card.Background = new UiColor(200, 0, 0);
host.AdvanceAnimations(TimeSpan.FromMilliseconds(100));
```

`HeadlessRoot.AdvanceAnimations` 调用同一 `FrameAnimations.Advance`，会同时推进新过渡和旧 `IFrameAnimation`；不要在同一逻辑帧再次调用后者。它不自动排空 UI 续体或执行布局。时间参数不得为负，`UpdateLayout` 和构帧仍由调用方控制。

## 范围与验证

首期不提供关键帧、Spring、延迟、循环、公开完成事件、布局属性动画、`.ui` 内动画标签或合成线程动画；也没有通用元素透明度/变换动画。`.eustyle` 的过渡块与简写已实现，其他动画语言仍需单独设计。

回归覆盖插值、中途反向、双向绑定、样式事务、首帧和主题切换、隐藏/解绑、多宿主隔离、重入和旧逐帧接口兼容。具体用例见 `tests/EclipseUI.Headless.Tests/TransitionTests.cs`、`AnimationHostClockTests.cs`、`AnimationAuditTests.cs` 和编译器的 `LooklessTemplateTests.cs`；平台宿主测试覆盖各自的帧流程。原生系统动态偏好、跨资源槽过渡和合成线程性能仍未实现或验证。
