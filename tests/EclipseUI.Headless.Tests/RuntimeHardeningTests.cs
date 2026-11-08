using System;
using System.Runtime.CompilerServices;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// S4/S5 运行时硬化回归：
///  A1 —— 属性本地值改用 ValueBox&lt;T&gt; 内联存储，重复赋值不再把值类型装箱；
///  A2 —— Component.Dispose 清空自身失效队列，释放对组件的强引用；
///  并锁定 GetValue/SetValue 的既有语义（相等短路、脏标记、通知、继承、绑定对象身份）。
/// </summary>
public class RuntimeHardeningTests
{
    // StyledPropertyRegistry 以 (OwnerType, Name) 去重。用专用宿主类型 + 唯一属性名，
    // 避免与生产属性或其它测试类的注册冲突。
    private sealed class ProbeOwner { }

    private const int WarmupIterations = 64;
    private const int SmallIterations = 1_000;
    private const int LargeIterations = 8_000;
    // 一次性抖动预算（JIT 分层 / GC 记账 / 委托缓存等常数级分配）。
    // 若每次赋值装箱，SmallIterations=1000 次 double/int/bool 装箱约 24_000 字节，远超此预算。
    private const long ConstantAllocationBudget = 8_192;

    // ---- A1：去装箱 ----    [Fact]
    public void RepeatedSetValue_DoesNotBoxPerAssignment_ForValueTypes()
    {
        AssertNoPerAssignmentBoxing(
            StyledProperty<double>.Register<ProbeOwner>("Hardening.Boxing.Double", 0d),
            i => i * 0.5);
        AssertNoPerAssignmentBoxing(
            StyledProperty<int>.Register<ProbeOwner>("Hardening.Boxing.Int", 0),
            i => i);
        AssertNoPerAssignmentBoxing(
            StyledProperty<bool>.Register<ProbeOwner>("Hardening.Boxing.Bool", false),
            i => (i & 1) == 0);
    }

    private static void AssertNoPerAssignmentBoxing<T>(StyledProperty<T> property, Func<int, T> makeValue)
    {
        var element = new StackPanel();

        // 预热：完成泛型 JIT、EqualityComparer<T>.Default 单例初始化、以及首次 new ValueBox<T>。
        // 这些都是一次性分配，不落在下面的测量窗口里。
        for (var i = 0; i < WarmupIterations; i++)
            element.SetValue(property, makeValue(i));

        // 测量窗口内每次写入「不同」的值，强制命中「已有 ValueBox<T>、改写 .Value」这条路径——
        // 旧实现正是在这里把 value 重新装箱进 Dictionary<StyledProperty, object?>。
        // 两段窗口用不相交的值域，保证窗口切换处也不会因相等而短路。
        var small = MeasureAssignments(element, property, makeValue, SmallIterations, WarmupIterations);
        var large = MeasureAssignments(element, property, makeValue, LargeIterations, WarmupIterations + SmallIterations);

        // 稳健断言：不硬断言「严格 0 分配」（不同运行时可能有一次性抖动），而是断言
        // 「分配量不随迭代次数线性增长」。若每次赋值仍装箱，large（8 倍迭代）会约为 small
        // 的 8 倍；修复后两者都是常数级（理想为 0）。
        Assert.True(small <= ConstantAllocationBudget,
            $"{typeof(T).Name}: 小窗口分配 {small} 字节，疑似每次赋值发生装箱（预算 {ConstantAllocationBudget}）。");
        Assert.True(large <= small + ConstantAllocationBudget,
            $"{typeof(T).Name}: 分配量随迭代次数增长（small={small}, large={large}），疑似每次赋值仍发生装箱。");
    }

    private static long MeasureAssignments<T>(
        StackPanel element, StyledProperty<T> property, Func<int, T> makeValue, int iterations, int offset)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            element.SetValue(property, makeValue(offset + i));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ---- A1：语义不变 ----

    [Fact]
    public void SetValue_ReusesValueBox_AndPreservesChangeSemantics()
    {
        var property = StyledProperty<double>.Register<ProbeOwner>("Hardening.Semantics", 0d);
        var element = new StackPanel();

        var changeCount = 0;
        element.PropertyChanged += (_, changed) =>
        {
            // 绑定依赖 StyledProperty 对象身份：事件里必须是同一个实例。
            if (ReferenceEquals(changed, property)) changeCount++;
        };

        Assert.Equal(property.DefaultValue, element.GetValue(property)); // 默认回退

        var version0 = element.RenderVersion;
        element.SetValue(property, 1.5);
        Assert.Equal(1.5, element.GetValue(property));          // GetValue 返回新值
        Assert.Equal(1, changeCount);                           // 值变化 → 通知
        Assert.True(element.RenderVersion > version0);          // 脏标记递增

        var version1 = element.RenderVersion;
        element.SetValue(property, 1.5);                        // 相同值 → 相等短路
        Assert.Equal(1.5, element.GetValue(property));
        Assert.Equal(1, changeCount);                           // 通知不触发
        Assert.Equal(version1, element.RenderVersion);          // 脏标记不变

        var version2 = element.RenderVersion;
        element.SetValue(property, 2.5);                        // 再次变化
        Assert.Equal(2.5, element.GetValue(property));
        Assert.Equal(2, changeCount);
        Assert.True(element.RenderVersion > version2);
    }

    [Fact]
    public void TwoWayBinding_ValueType_StillSyncsAfterStorageChange()
    {
        var property = StyledProperty<double>.Register<ProbeOwner>("Hardening.Binding", 0d);
        var element = new StackPanel();
        var source = 7d;
        var invalidations = 0;

        using var binding = UiBinding.TwoWay(
            element,
            property,
            () => element.GetValue(property),
            v => element.SetValue(property, v),
            () => source,
            v => source = v,
            () => invalidations++);

        Assert.Equal(7d, element.GetValue(property)); // 构造时源 → 目标推送

        element.SetValue(property, 9d);               // 目标变化 → 回写源（依赖 ReferenceEquals 命中）
        Assert.Equal(9d, source);
        Assert.Equal(1, invalidations);

        element.SetValue(property, 9d);               // 相等短路：不回写
        Assert.Equal(1, invalidations);
    }

    // ---- A1：继承语义不变 ----

    [Fact]
    public void Inherits_ResolutionAndBroadcastStillWork()
    {
        var parent = new TextBlock();
        var child = new TextRun("x");
        parent.AddRun(child);

        // 子级无本地值：沿父链解析（初始为默认值）
        Assert.Equal(TextBlock.FontSizeProperty.DefaultValue, child.GetValue(TextBlock.FontSizeProperty));

        var childNotifications = 0;
        child.PropertyChanged += (_, changed) =>
        {
            if (ReferenceEquals(changed, TextBlock.FontSizeProperty)) childNotifications++;
        };

        parent.FontSize = 20d;
        Assert.Equal(20d, parent.FontSize);
        Assert.Equal(20d, child.GetValue(TextBlock.FontSizeProperty)); // 父链解析到新值
        Assert.Equal(1, childNotifications);                           // 继承广播到达子级

        // 相等短路：父再设相同值，子级不收到广播、也不标脏
        var version = child.RenderVersion;
        parent.FontSize = 20d;
        Assert.Equal(1, childNotifications);
        Assert.Equal(version, child.RenderVersion);

        // 再次变更继续广播
        parent.FontSize = 24d;
        Assert.Equal(24d, child.GetValue(TextBlock.FontSizeProperty));
        Assert.Equal(2, childNotifications);
    }

    [Fact]
    public void Inherits_LocalValueStillOverridesParent()
    {
        var parent = new TextBlock();
        var child = new TextRun("x");
        parent.AddRun(child);

        parent.FontSize = 20d;
        Assert.Equal(20d, child.GetValue(TextBlock.FontSizeProperty));

        child.SetValue(TextBlock.FontSizeProperty, 30d);
        Assert.Equal(30d, child.GetValue(TextBlock.FontSizeProperty)); // 本地值胜出
        Assert.Equal(20d, parent.FontSize);                            // 父不受影响

        parent.FontSize = 24d;
        Assert.Equal(30d, child.GetValue(TextBlock.FontSizeProperty)); // 仍用本地值（local → 父链 → 默认）
    }

    // ---- A2：失效队列清理 / 泄漏回归 ----

    private sealed class ProbeComponent : Component
    {
        public int RenderCount { get; private set; }
        protected override Element Build() => new StackPanel();
        protected override void Render() => RenderCount++;
    }

    private sealed class LeakHost : Component
    {
        // 可清空：避免 Dispose 后 host 仍通过字段强引用子组件（否则 GC 断言失效）。
        public Component? Child;

        protected override Element Build()
        {
            var panel = new StackPanel();
            var child = Child;
            if (child is not null)
            {
                Track(child);                       // adopt + 随 host 释放
                child.Mount();                      // 使其 Invalidate 生效
                panel.Children.Add(child.Root);     // 视觉树只持有 Element，不持有 Component
            }
            return panel;
        }
    }

    [Fact]
    public void Dispose_ClearsPendingQueue_ReleasesQueuedComponent()
    {
        var (root, weakChild) = CreateDisposedHostWithPendingChild();

        ForceGc();

        // root 被 KeepAlive 保持存活，因此 child 只有在「失效队列被清空」后才可能回收；
        // 若未清空，root._pendingRenders/_pendingSet 会一直强引用 child（且已 Dispose 的
        // root 的 ProcessInvalidations 直接返回 false，队列永不被排空）→ 泄漏。
        Assert.False(weakChild.IsAlive,
            "Dispose 未清空失效队列：已释放的子组件仍被根组件的待渲染队列强引用（泄漏回归）。");

        GC.KeepAlive(root); // 必须保活 host：否则未修复时整个对象图被回收，断言会假阴性
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LeakHost Root, WeakReference WeakChild) CreateDisposedHostWithPendingChild()
    {
        var child = new ProbeComponent();
        var root = new LeakHost { Child = child };
        root.Mount();                 // Track(child)、Mount(child)、把 child.Root 挂进视觉树

        child.Invalidate();           // 登记到 root 的失效队列（root 是 RootComponent），形成强引用
        var weak = new WeakReference(child);

        root.Child = null;            // 去掉 host 字段强引用
        root.Dispose();               // 级联 Dispose(child) + A2 清空 root 的 _pendingRenders/_pendingSet
        child = null;                 // 去掉局部强引用
        return (root, weak);
    }

    /// <summary>
    /// 稳健回退断言（框架结构决定 WeakReference 场景只覆盖「释放根」这一种）：
    /// 保守版 A2 只清空组件「自身」队列；单独释放子组件时，它仍暂存在存活的 root 队列里，
    /// 直到下一轮 ProcessInvalidations 排空。因此这里直接锁定对外契约：Dispose 幂等、
    /// 之后 Invalidate 不抛且 no-op、ProcessInvalidations 跳过已释放组件。
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent_AndInvalidationStopsProcessingComponent()
    {
        var root = new LeakHost { Child = new ProbeComponent() };
        root.Mount();
        var child = (ProbeComponent)root.Child!;

        child.Invalidate();          // 入队到 root 的失效队列
        child.Dispose();             // 只释放子组件；root 仍存活
        child.Dispose();             // 幂等：不抛
        child.Invalidate();          // Dispose 后 no-op：不抛、不入队

        Assert.False(root.ProcessInvalidations()); // 已释放组件被跳过（_disposed 守卫），不处理
        Assert.Equal(0, child.RenderCount);
    }

    private static void ForceGc()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // ---- S5：预算门槛扩展（D94：附加属性 / 布局热路径 / retained 帧构建）----

    /// <summary>附加属性重复赋值：弱表 ValueBox 盒复用，分配不得随迭代次数增长。</summary>
    [Fact]
    public void AttachedProperty_RepeatedSet_DoesNotAllocatePerAssignment()
    {
        var element = new FixedBox(10, 10);

        for (var i = 0; i < WarmupIterations; i++)
            Grid.SetRow(element, i);            // 首次 Add 节点/盒属于一次性分配

        var small = MeasureAttachedSets(element, SmallIterations, WarmupIterations);
        var large = MeasureAttachedSets(element, LargeIterations, WarmupIterations + SmallIterations);

        Assert.True(small <= ConstantAllocationBudget,
            $"附加属性：小窗口分配 {small} 字节，疑似每次赋值重分配值盒（预算 {ConstantAllocationBudget}）。");
        Assert.True(large <= small + ConstantAllocationBudget,
            $"附加属性：分配量随迭代次数增长（small={small}, large={large}），疑似未复用 ValueBox。");
    }

    private static long MeasureAttachedSets(FixedBox element, int iterations, int offset)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            Grid.SetRow(element, offset + i);   // 每次都换值，防止相等短路
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// 布局热路径：反复「强制 measure 失效 → UpdateLayout」的分配不得随次数增长。
    /// 这条锁住 Grid 轨道解析缓存、cells/double[] 缓冲复用与 ElementCollection 的
    /// struct enumerator（不装箱）——回退为逐帧 Split/新建数组/接口装箱都会红。
    /// </summary>
    [Fact]
    public void LayoutPass_DoesNotAllocatePerPass()
    {
        var root = CreateLayoutProbeTree();
        var headless = new HeadlessRoot(root) { AvailableSize = new Size(400, 400) };
        headless.UpdateLayout();

        for (var i = 0; i < WarmupIterations; i++)
        {
            root.InvalidateMeasure();
            headless.UpdateLayout();
        }

        var small = MeasureLayoutPasses(root, headless, SmallIterations);
        var large = MeasureLayoutPasses(root, headless, LargeIterations);

        Assert.True(small <= ConstantAllocationBudget,
            $"布局：小窗口分配 {small} 字节，布局热路径存在逐帧分配（预算 {ConstantAllocationBudget}）。");
        Assert.True(large <= small + ConstantAllocationBudget,
            $"布局：分配量随遍数增长（small={small}, large={large}），疑似轨道/缓冲未复用。");
    }

    private static Grid CreateLayoutProbeTree()
    {
        var root = new Grid { RowDefinitions = "Auto,1*", ColumnDefinitions = "120,*" };
        var wrap = new WrapPanel { Gap = 4 };
        for (var i = 0; i < 8; i++)
            wrap.Children.Add(new FixedBox(40, 20));
        root.Children.Add(wrap);
        Grid.SetRow(wrap, 0);
        Grid.SetColumn(wrap, 1);

        var cells = new Grid { RowDefinitions = "*,*,*" };
        for (var i = 0; i < 6; i++)
        {
            var box = new FixedBox(30, 16);
            cells.Children.Add(box);
            Grid.SetRow(box, i % 3);
            Grid.SetColumn(box, i % 2);
        }
        root.Children.Add(cells);
        Grid.SetRow(cells, 1);
        return root;
    }

    private static long MeasureLayoutPasses(Grid root, HeadlessRoot headless, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            root.InvalidateMeasure();
            headless.UpdateLayout();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// retained 帧构建（D93）：静止树第二帧起段全复用，构建分配应保持在常数级
    /// （仅每帧新 DisplayList 及其内部 List 的固定扩容）。段缓存回退为逐元素重画会红。
    /// </summary>
    [Fact]
    public void RetainedBuildFrame_StaticTree_NearConstantAllocation()
    {
        var root = new StackPanel { Gap = 4 };
        for (var i = 0; i < 6; i++)
        {
            var box = new FixedBox(80, 24);
            root.Children.Add(box);
        }
        new HeadlessRoot(root) { AvailableSize = new Size(300, 300) }.UpdateLayout();

        var painter = new EclipseUI.Rendering.ElementPainter(textPainter: new NullTextPainter());
        painter.BuildFrame(root, new Size(300, 300));   // 首帧：全量画 + 段缓存建立
        var (_, repainted) = painter.LastBuildStats;
        Assert.Equal(7, repainted);                     // root + 6 boxes

        // 预热 FrameDamage/字典等一次性结构。
        for (var i = 0; i < WarmupIterations; i++)
            painter.BuildFrame(root, new Size(300, 300));

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 1_000;
        for (var i = 0; i < frames; i++)
            painter.BuildFrame(root, new Size(300, 300));
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)frames;

        // 每帧预算：仅结构性常数（每帧新 DisplayList + FrameDamage 对象与内部 List 扩容，
        // 实测 ~384B）；段复用下与元素数无关。段缓存失效逐元素重画会是每帧数 KB。
        Assert.True(perFrame <= 512,
            $"retained 构建：静止树每帧分配 {perFrame:0.#} 字节，疑似段缓存失效逐元素重画。");
        var (_, repaintedAfter) = painter.LastBuildStats;
        Assert.Equal(0, repaintedAfter);                // 构建结果语义：全部复用
    }

    private sealed class NullTextPainter : EclipseUI.Rendering.ITextPainter
    {
        public EclipseUI.Rendering.TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * 0.4, text.Length * fontSize * 0.5);

        public void Paint(EclipseUI.Rendering.DisplayList list, string text, double fontSize,
            Point topLeft, EclipseUI.Rendering.Color color)
        {
        }
    }

    /// <summary>固定期望尺寸的探针元素（同 WrapPanelTests 等的口径）。</summary>
    private sealed class FixedBox : Panel
    {
        private readonly Size _size;
        public FixedBox(double width, double height) => _size = new Size(width, height);

        // 跨程序集重写 protected internal 成员只能收窄为 protected（C# 规则）。
        protected override Size MeasureCore(Size available) => _size;

        protected override void ArrangeCore(Rect final)
        {
        }
    }
}
