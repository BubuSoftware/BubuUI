using System.Collections.Generic;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;

namespace Gallery;

/// <summary>
/// 画廊主场景（平台无关，2026-11-09 完全重构）：<see cref="ScrollViewer"/> 作根——内容超出
/// 窗口即可滚动（窗口根的有限高度使视口生效，见 ScrollViewer 文档），整页分区陈列框架
/// 当前全部能力：
/// <list type="bullet">
/// <item>反应式组件（模板编译管线）：@bind / @if / @foreach / @switch / OnClick</item>
/// <item>控件：Button（主题/class/inline 三层样式）、CheckBox、TextBlock(TextRun)</item>
/// <item>布局：StackPanel / WrapPanel / Grid（Auto·固定·星号轨道 + 附加属性）/ ListBoxItem 卡片</item>
/// <item>滚动：ScrollViewer（滚轮 + 滚动条）、VirtualizingStackPanel（1000 项视口实例化）</item>
/// <item>样式：S-A 主题令牌切换（宿主接线）、S-B Class 样式 + Visual 覆盖（local &gt; class &gt; theme）</item>
/// </list>
/// 根组件 <see cref="GalleryRoot"/> 只承担所有权与失效队列（G2 口径）。
/// </summary>
public static class GalleryScene
{
    /// <summary>构建场景。返回根滚动视图与各可交互部件，供宿主/验证工具驱动。</summary>
    public sealed record Scene(
        ScrollViewer RootViewer,
        Counter Counter,
        TaskList Tasks,
        Button AddButton,
        Button ThemeButton);

    public static Scene Build()
    {
        var counter = new Counter();
        counter.Mount();

        var taskList = new TaskList();
        // 先填数据再 Mount：Build 末尾的 Refresh 会读当前 tasks（spec/03 §2.3），省一次渲染。
        taskList.tasks = new List<TaskItem>
        {
            new TaskItem { Id = 1, Name = "Design review" },
            new TaskItem { Id = 2, Name = "Damage tracking v0" },
            new TaskItem { Id = 3, Name = "Golden image test" },
        };
        taskList.Mount();

        // 控件陈列区（模板编译管线）：Grid/WrapPanel/ScrollViewer/虚拟化 + @switch 演示。
        var showcase = new Showcase();
        showcase.Mount();
        showcase.virtualList.ItemFactory =
            i => new Button { Label = $"virtual item {i + 1}" };   // 1000 行只实例化视口可见

        var addTaskButton = new Button { Label = "+ Add task", Classes = ["primary"] };
        var nextId = 4;
        addTaskButton.AddHandler(Button.ClickEvent, () =>
        {
            var tasks = new List<TaskItem>(taskList.tasks)
            {
                new TaskItem { Id = nextId, Name = $"Task {nextId}" },
            };
            nextId++;
            taskList.tasks = tasks;
            taskList.Invalidate();
        });

        // S-A 主题令牌化：切换按钮本体在树里，Click 由宿主接线（SetTheme 换 token 表 → 整帧重绘）。
        var themeButton = new Button { Label = "Toggle theme", Classes = ["tool"] };

        // ---- 页面组装：根 ScrollViewer + 分区卡片 ----
        var page = new StackPanel { Gap = 16, Padding = 24 };

        // 头部：标题区 + 主题切换。
        var header = new StackPanel { Gap = 4 };
        header.Children.Add(Text("EclipseUI Gallery", 28));
        header.Children.Add(Text("Reactive UI → scene graph → Vello. Scroll to browse every feature; Tab / Space to focus, Esc to clear focus.", 13));
        var headerRow = new WrapPanel { Gap = 8 };
        headerRow.Children.Add(themeButton);
        header.Children.Add(headerRow);
        page.Children.Add(header);

        // ① 反应式组件（@bind + @if + OnClick）：checkbox 必须是树中第一个 CheckBox
        //    （GalleryCheck selfcheck 依赖——其余复选框都排在其后）。
        page.Children.Add(Section("1. Reactivity - template components: bind / if", counter.Root));

        // ② 任务列表（@foreach + ListBoxItem 卡片 + RoutedEvent 加任务）。
        var tasksSection = new StackPanel { Gap = 8 };
        tasksSection.Children.Add(addTaskButton);
        tasksSection.Children.Add(taskList.Root);
        page.Children.Add(Section("2. TaskList - foreach + routed events", tasksSection));

        // ③ 控件与布局陈列（模板 Showcase.ui：文本分级 / CheckBox / Grid / WrapPanel /
        //    ScrollViewer / 虚拟化 / @switch）。
        page.Children.Add(Section("3. Controls & Layout - template compile pipeline", showcase.Root));

        // ④ 样式系统：有效值分层 local > class > theme 的按钮四连 + Visual 覆盖卡片。
        page.Children.Add(Section("4. Styles - theme tokens + class layers + registered states", BuildStyleSection()));

        // ⑤ 自定义样式属性（design/19 E6）：[UiStyleProperty] 标注字段并集进目录——
        //    类通道落值 + &:hover 变体翻自定义属性 + 值读回（内联 Style= 读回在第 3 分区）。
        page.Children.Add(Section(
            "5. Custom style properties - [UiStyleProperty] catalog union (design/19)",
            BuildCustomPropertySection()));

        page.Children.Add(Text("EclipseUI - retained segment caching (D93) - scroll to browse the whole gallery", 12));

        // 根滚动器显式 Auto：极窄窗口下保留按需横滚兜底（方向开关默认 Disabled——
        // Avalonia 对齐，其余 ScrollView 溢出裁剪不出条，design/05 §布局契约）。
        var rootViewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        rootViewer.Children.Add(page);
        return new Scene(rootViewer, counter, taskList, addTaskButton, themeButton);
    }

    /// <summary>
    /// 样式系统演示区（代码构建——Class/Visual 覆盖尚不支持模板语法 EUI031）。
    /// inline 按钮保留静态色：与主题资源按钮并列，正好展示「静态覆盖 vs 主题跟随」两种语义。
    /// </summary>
    private static StackPanel BuildStyleSection()
    {
        // 按钮五连：主题默认 → class primary（主题资源）→ class tool（主题资源）→
        // inline Visual 静态覆盖 → :disabled（E3：class 变体 + 有效值禁用，点击/焦点被路由吞掉）。
        var row = new WrapPanel { Gap = 8 };
        row.Children.Add(new Button { Label = "theme default" });
        row.Children.Add(new Button { Label = "class: primary", Classes = ["primary"] });
        row.Children.Add(new Button { Label = "class: tool", Classes = ["tool"] });
        var inline = new Button { Label = "inline: static" };
        Visual.SetBackground(inline, new Color(196, 84, 152));
        Visual.SetCornerRadius(inline, 18);
        row.Children.Add(inline);
        row.Children.Add(new Button { Label = ":disabled", Classes = ["primary"], IsEnabled = false });

        // Visual 覆盖卡片：StackPanel 背景/圆角 + 文本前景覆盖（painter 回落主题当未设置）。
        var card = new StackPanel { Gap = 6, Padding = 14 };
        Visual.SetBackgroundResource(card, new ThemeColor(
            Dark: new Color(38, 84, 124),
            Light: new Color(208, 232, 244)));
        Visual.SetCornerRadius(card, 14);
        var cardTitle = Text("StackPanel + ThemeColor resource (follows theme)", 14);
        Visual.SetForegroundResource(cardTitle, new ThemeColor(
            Dark: new Color(233, 238, 248),
            Light: new Color(30, 34, 44)));
        card.Children.Add(cardTitle);
        var fgText = Text("TextBlock + ThemeColor foreground override (local beats muted-by-size rule)", 15);
        Visual.SetForegroundResource(fgText, new ThemeColor(
            Dark: new Color(126, 220, 180),
            Light: new Color(18, 118, 88)));
        card.Children.Add(fgText);

        // E5 registered pseudo-state（design/17 §3.2）：'spinning' 由 app.eustyle 的 @states
        // 声明 + StyleBootstrap 启动期注册；宿主用返回的掩码（掩码即凭证）翻位——
        // :spinning 变体命中即变体色，翻回即基础色（O(1) 位测试，段键含注册位）。
        var spinState = PseudoStatesRegistry.Register("spinning");
        var chips = new WrapPanel { Gap = 8 };
        var idleChip = new TextBlock { FontSize = 13 };
        idleChip.AddRun(new TextRun("spinner: idle"));
        idleChip.Classes = ["spinner"];
        var busyChip = new TextBlock { FontSize = 13 };
        busyChip.AddRun(new TextRun("spinner: spinning"));
        busyChip.Classes = ["spinner"];
        busyChip.SetRegisteredState(spinState, true);
        chips.Children.Add(idleChip);
        chips.Children.Add(busyChip);

        var section = new StackPanel { Gap = 10 };
        section.Children.Add(row);
        section.Children.Add(card);
        section.Children.Add(chips);
        // 自定义样式属性（design/19 E6）的类通道演示在第 5 分区（可见 + 值读回）。
        return section;
    }

    /// <summary>
    /// design/19 E6 自定义样式属性演示区（可见）：<c>BadgeStyles</c> 的 <c>[UiStyleProperty]</c>
    /// 字段并集进目录后，.eustyle 类通道与模板内联 <c>Style=</c> 都能写（后者在第 3 分区
    /// Showcase 内读回）。自定义属性没有 painter 读点——值是存储层事实，所以把元素收到的值
    /// 读回文案；类通道按钮 hover 时 <c>.badge &amp;:hover</c> 把 Glow 从 6 翻到 12，
    /// PropertyChanged 即时刷新文案，变体落在 class 层肉眼可见。
    /// </summary>
    private static StackPanel BuildCustomPropertySection()
    {
        var section = new StackPanel { Gap = 8 };
        // 文案行长对齐画廊既有口径（~90 字符内）：TextBlock 不换行，纵向 StackPanel 取最宽
        // 子项作整页宽度——超长行会把整页撑宽、所有分区横向溢出（2026-11-12 实测教训）。
        section.Children.Add(Text(
            "App-assembly [UiStyleProperty] fields join the style catalog - no framework changes.", 13));

        // 类通道：.badge 规则（app.eustyle）发射 s.Set(field, value)。先订阅再挂类——
        // 初次应用与后续变体翻转走同一条 SetValue → PropertyChanged 路径。
        var classDemo = new Button();
        void RefreshLabel() => classDemo.Label =
            $"class .badge: Glow={classDemo.GetValue(BadgeStyles.GlowProperty)} · " +
            $"Tone={classDemo.GetValue(BadgeStyles.ToneProperty)} · " +
            $"BadgeLabel=\"{classDemo.GetValue(BadgeStyles.BadgeLabelProperty)}\"";
        classDemo.PropertyChanged += (_, property) =>
        {
            if (ReferenceEquals(property, BadgeStyles.GlowProperty)) RefreshLabel();
        };
        classDemo.Classes = ["badge"];   // Provider 已在宿主装配期接线（UseStyles 先于场景构建）
        RefreshLabel();                   // 初次应用的事件可能早于 Tone/BadgeLabel 落值，补一次全量读
        section.Children.Add(classDemo);

        section.Children.Add(Text(
            "Number / enum keyword / string - all three forms declared in BadgeStyles.cs; " +
            "hover flips Glow 6 → 12 via the .badge &:hover variant.", 12));
        section.Children.Add(Text(
            "Inline Style= in Showcase (section 3) writes the same fields locally; its caption reads the values back.", 12));
        return section;
    }

    /// <summary>
    /// 分区卡片：背景/圆角/标题前景走 <see cref="ThemeColor"/> 主题资源（S-C）——
    /// 亮暗切换时 ReplaceTheme 丢段重建，卡片与标题随新主题自动换色。
    /// </summary>
    private static StackPanel Section(string title, Element content)
    {
        var card = new StackPanel { Gap = 10, Padding = 16 };
        Visual.SetBackgroundResource(card, new ThemeColor(
            Dark: new Color(31, 37, 50),
            Light: new Color(255, 255, 255)));
        Visual.SetCornerRadius(card, 12);
        var heading = Text(title, 18);
        Visual.SetForegroundResource(heading, new ThemeColor(
            Dark: new Color(233, 238, 248),
            Light: new Color(30, 34, 44)));
        card.Children.Add(heading);
        card.Children.Add(content);
        return card;
    }

    private static TextBlock Text(string text, double fontSize)
    {
        // Wrap：窄窗口下说明文案随视口收窄换行（TextBlock 换行契约，design/05）；
        // 宽窗口下宽度富余，视觉与单行完全一致。
        var block = new TextBlock { FontSize = fontSize, TextWrapping = TextWrapping.Wrap };
        block.AddRun(new TextRun(text));
        return block;
    }
}

/// <summary>
/// G2 场景是手工元素树；根组件只承担所有权与失效队列，供 WindowHost 每帧处理。
/// </summary>
public sealed class GalleryRoot : Component
{
    private readonly Counter _counter;
    private readonly TaskList _tasks;
    private readonly Element _tree;

    public GalleryRoot(Counter counter, TaskList tasks, Element tree)
    {
        _counter = counter;
        _tasks = tasks;
        _tree = tree;
    }

    protected override Element Build()
    {
        Track(_counter);
        Track(_tasks);
        return _tree;
    }
}
