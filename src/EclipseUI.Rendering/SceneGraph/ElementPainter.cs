using System.Runtime.CompilerServices;
using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>
/// 元素树 → <see cref="DisplayList"/> 的场景图遍历（G2-10）。
///
/// 约定：
/// <list type="bullet">
/// <item>元素的 <c>FinalRect</c> 位于父元素局部坐标系；遍历时累加绝对偏移，指令直接用根坐标。</item>
/// <item>块协调器（<c>@if</c> / <c>@foreach</c>）通过 <see cref="Element.IsVisualTransparent"/> 跳过自身背景。</item>
/// <item>文本走 <see cref="ITextPainter"/>，布局与绘制共用同一 shaping 结果。</item>
/// <item>根元素由宿主 arrange 到整个可视区；根自身不画卡片，只画主题 surface。</item>
/// </list>
///
/// G2 retained（D93）：每个元素的**自绘命令**（类型绘制 + 卡片 + 焦点环）按
/// (RenderVersion, 绝对位置, 尺寸, 焦点态) 缓存——树未变的元素直接复用命令段，
/// 不再逐条重新构造（帧间分配趋零）；失效契约与 <c>DamageTracker</c> 一致：
/// 「一切影响像素的变化都递增 <see cref="Element.RenderVersion"/>」。
/// clip 作用域与滚动条每帧重新生成（位置可能变、且成本可忽略），不属于段。
/// 金标准：retained 构建与全量构建渲染结果逐像素一致（<c>RetainedPaintTests</c> 锁定）。
/// <see cref="BuildFrame"/> 额外产出元素级脏区，宿主据此跳过空闲帧的上屏。
/// </summary>
public sealed class ElementPainter
{
    /// <summary>单元素自绘命令段；缓存后视为只读。</summary>
    private sealed record Segment(long Version, Point Position, Size Size, PseudoStates States, List<DrawCommand> Commands);

    private ElementTheme _theme;
    private readonly ITextPainter? _text;
    private readonly DamageTracker _damageTracker = new();
    private readonly ConditionalWeakTable<Element, Segment> _segments = new();

    private DisplayList _list = null!;
    private DisplayList _paintList = null!;
    private Element? _focused;
    private int _reusedSegments;
    private int _repaintedSegments;

    public ElementPainter(ElementTheme? theme = null, ITextPainter? textPainter = null)
    {
        _theme = theme ?? new ElementTheme();
        _text = textPainter;
    }

    public ElementTheme Theme => _theme;

    /// <summary>
    /// 替换主题（S-A 亮暗切换）：段缓存里的命令持有旧主题的颜色快照，
    /// 必须整段丢弃；脏区历史同步重置，下一帧按整帧重绘上屏。
    /// </summary>
    public void ReplaceTheme(ElementTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (ReferenceEquals(theme, _theme)) return;
        _theme = theme;
        ResetSegments();
        ResetDamage();
    }

    /// <summary>上一次构建的段复用统计（复用数 / 重画数）。</summary>
    public (int Reused, int Repainted) LastBuildStats { get; private set; }

    /// <summary>丢弃全部段缓存：下一次构建为全量重画（窗口遮挡恢复、测试对照）。</summary>
    public void ResetSegments() => _segments.Clear();

    /// <summary>
    /// 构建一帧（不做脏区跟踪）。会清空脏区历史：下一次 <see cref="BuildFrame"/> 按整帧处理。
    /// 坐标使用设备无关单位；<paramref name="scale"/> 为 DPI 缩放，
    /// 由根变换一次性放大到设备像素。返回的 list 尺寸为逻辑尺寸。
    /// </summary>
    public DisplayList Build(Element root, Size size, double scale = 1, Element? focused = null)
    {
        _damageTracker.Reset();
        return BuildCore(root, size, scale, focused, tracker: null);
    }

    /// <summary>
    /// 构建一帧并返回相对上一帧的脏区。首帧 / 尺寸 / DPI 变化为整帧；
    /// 脏区为空时宿主可跳过上屏（画面保持不变）。
    /// </summary>
    public PaintedFrame BuildFrame(Element root, Size size, double scale = 1, Element? focused = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var damage = _damageTracker.Begin(size, scale, focused);
        var list = BuildCore(root, size, scale, focused, _damageTracker);
        _damageTracker.End();
        return new PaintedFrame(list, damage);
    }

    /// <summary>丢弃脏区历史，下一帧强制整帧重绘（窗口遮挡恢复等场景）。</summary>
    public void ResetDamage() => _damageTracker.Reset();

    private DisplayList BuildCore(Element root, Size size, double scale, Element? focused, DamageTracker? tracker)
    {
        ArgumentNullException.ThrowIfNull(root);

        var list = new DisplayList(size);
        _list = list;
        _paintList = list;
        _focused = focused;
        _reusedSegments = 0;
        _repaintedSegments = 0;

        var scaled = scale > 0 && Math.Abs(scale - 1) > 1e-6;
        if (scaled)
            list.Add(new PushTransform(Transform.Scale(scale)));

        list.Add(new FillRect(new Rect(0, 0, size.Width, size.Height), _theme.Surface));

        var clipSurface = size.Width > 0 && size.Height > 0;
        if (clipSurface)
            list.Add(new PushClip(new Rect(0, 0, size.Width, size.Height)));

        PaintElement(root, Point.Zero, isRoot: true, tracker);

        if (clipSurface) list.Add(new PopClip());
        if (scaled) list.Add(new PopTransform());

        LastBuildStats = (_reusedSegments, _repaintedSegments);
        return list;
    }

    private void PaintElement(Element element, Point offset, bool isRoot, DamageTracker? tracker)
    {
        var final = element.FinalRect;
        var rect = new Rect(offset.X + final.X, offset.Y + final.Y, final.Width, final.Height);

        // 伪类状态（D102）：hover/pressed/focused 读元素自身（FocusManager/InputRouter 维护
        // IsFocused）；Build 的 focused 参数是 headless 手工传焦点的兼容入口。两来源并集，
        // 段键与焦点环绘制同源——否则 retained 复用键与全量重画判定会分叉。
        var states = element.PseudoStates;
        if (ReferenceEquals(element, _focused) && element.IsFocusable)
            states |= PseudoStates.Focused;
        var focused = (states & PseudoStates.Focused) != 0;
        // E3：焦点环宽度可被 class/inline 覆盖（Visual.FocusRingWidth），null 回落主题令牌。
        var ringWidth = Visual.GetFocusRingWidth(element) ?? _theme.FocusRingWidth;
        // 焦点环 stroke 居中于 ring（rect 外扩 ringWidth）路径：外缘达 1.5×ringWidth，
        // AA 半覆盖再 +1——脏区必须覆盖全部环像素（D102 抓出的既有覆盖不足：旧式 1× 膨胀下
        // 环外缘 ~1px 带留在脏区外，GPU retained 合成与整帧参照逐像素比对时暴露）。
        var visualBounds = focused ? Inflate(rect, ringWidth * 1.5 + 1) : rect;
        tracker?.Visit(element, visualBounds);

        // retained 段：自绘命令（类型绘制 + 卡片 + 焦点环）按失效键复用。
        if (_segments.TryGetValue(element, out var segment) &&
            segment.Version == element.RenderVersion &&
            segment.Position == rect.Location &&
            segment.Size == rect.Size &&
            segment.States == states)
        {
            _list.AddRange(segment.Commands);
            _reusedSegments++;
        }
        else
        {
            var buffer = new DisplayList();
            _paintList = buffer;
            PaintSelf(element, rect, focused, isRoot);
            _paintList = _list;
            _segments.Remove(element);
            _segments.Add(element, new Segment(element.RenderVersion, rect.Location, rect.Size, states, buffer.Commands));
            _list.AddRange(buffer.Commands);
            _repaintedSegments++;
        }

        // ScrollViewer 无论 ClipToBounds 与否都必须裁剪：内容按 -Offset 平移，不裁就画出视口外。
        var clip = (element is Panel { ClipToBounds: true } || element is ScrollViewer)
                   && rect.Width > 0 && rect.Height > 0;
        if (clip) _list.Add(new PushClip(rect));

        // G2 的可视子节点全部来自 Panel（TextBlock 的 run 也是 Panel.Children 的一部分）。
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
                PaintElement(child, rect.Location, isRoot: false, tracker);
        }

        if (clip) _list.Add(new PopClip());

        // 滚动条画在内容之上（裁剪作用域外也不越界：位置按视口矩形内缩计算）。
        if (element is ScrollViewer viewer)
            PaintScrollBar(viewer, rect);
    }

    /// <summary>元素的自身绘制（写入当前段缓冲；子树由 <see cref="PaintElement"/> 递归处理）。</summary>
    private void PaintSelf(Element element, Rect rect, bool focused, bool isRoot)
    {
        switch (element)
        {
            case TextBlock block:
                PaintTextBlock(block, rect);
                break;
            case Button button:
                PaintButton(button, rect);
                break;
            case CheckBox checkBox:
                PaintCheckBox(checkBox, rect);
                break;
        }

        if (!isRoot && !element.IsVisualTransparent)
        {
            switch (element)
            {
                case ListBoxItem:
                    PaintCard(element, rect, _theme.ItemBackground, _theme.ItemBorder, _theme.ItemRadius);
                    break;
                case ScrollViewer:
                    PaintCard(element, rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
                case StackPanel:
                    PaintCard(element, rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
            }
        }

        if (focused)
            PaintFocusRing(element, rect);
    }

    /// <summary>叠加式滚动条：可滚动方向在视口内缘画拇指（v1 只显示，不做命中拖拽）。</summary>
    private void PaintScrollBar(ScrollViewer viewer, Rect rect)
    {
        if (viewer.ScrollableHeight > 0)
        {
            var trackHeight = rect.Height - ScrollBarInset * 2;
            if (trackHeight > ScrollBarThickness * 2)
            {
                var thumbHeight = Math.Max(ScrollBarThickness,
                    trackHeight * viewer.ViewportHeight / Math.Max(1, viewer.ExtentHeight));
                var maxScroll = Math.Max(1, viewer.ScrollableHeight);
                var offset = Math.Clamp(viewer.VerticalOffset, 0, viewer.ScrollableHeight);
                var thumbY = rect.Y + ScrollBarInset
                             + (trackHeight - thumbHeight) * (offset / maxScroll);
                _paintList.Add(new FillRoundedRect(
                    new Rect(rect.Right - ScrollBarInset - ScrollBarThickness, thumbY, ScrollBarThickness, thumbHeight),
                    ScrollBarThickness / 2,
                    _theme.ScrollBarThumb));
            }
        }

        if (viewer.ScrollableWidth > 0)
        {
            var trackWidth = rect.Width - ScrollBarInset * 2;
            if (trackWidth > ScrollBarThickness * 2)
            {
                var thumbWidth = Math.Max(ScrollBarThickness,
                    trackWidth * viewer.ViewportWidth / Math.Max(1, viewer.ExtentWidth));
                var maxScroll = Math.Max(1, viewer.ScrollableWidth);
                var offset = Math.Clamp(viewer.HorizontalOffset, 0, viewer.ScrollableWidth);
                var thumbX = rect.X + ScrollBarInset
                             + (trackWidth - thumbWidth) * (offset / maxScroll);
                _paintList.Add(new FillRoundedRect(
                    new Rect(thumbX, rect.Bottom - ScrollBarInset - ScrollBarThickness, thumbWidth, ScrollBarThickness),
                    ScrollBarThickness / 2,
                    _theme.ScrollBarThumb));
            }
        }
    }

    private const double ScrollBarInset = 4;
    private const double ScrollBarThickness = 6;

    // ---- S-C 有效色解析：主题资源 > 静态覆盖 > 主题令牌（解析发生在段构建期，
    // 换主题 ReplaceTheme 丢段重建即随新表刷新）----

    /// <summary>前景覆盖探测（可空）：null = 未设置，调用方回落自身规则（如字号次色）。
    /// E3：有效禁用且无显式覆盖时返回置灰令牌——class 变体（.Disabled 写入 class 层）优先于令牌。</summary>
    private Color? PeekForeground(Element element)
    {
        var foreground = Visual.GetForegroundResource(element)?.Resolve(_theme) ?? Visual.GetForeground(element);
        if (foreground is not null) return foreground;
        return element.IsEffectiveEnabled ? null : (Color?)_theme.DisabledForeground;
    }

    private Color ResolveForeground(Element element, Color fallback) => PeekForeground(element) ?? fallback;

    private Color ResolveBackground(Element element, Color fallback)
    {
        var background = Visual.GetBackgroundResource(element)?.Resolve(_theme) ?? Visual.GetBackground(element);
        if (background is not null) return background.Value;
        return element.IsEffectiveEnabled ? fallback : _theme.DisabledBackground;
    }

    private void PaintTextBlock(TextBlock block, Rect rect)
    {
        // S-B/E5：TextBlock 也可携带 class/local 背景（注册位状态芯片等——四层值盒有值即画，
        // 主题资源最先解析）；null = 维持文字控件透明惯例，不加背景命令。
        // 圆角读 class/local 覆盖（.spinner 的 CornerRadius），未设置回落按钮半径令牌。
        var chipBackground = Visual.GetBackgroundResource(block)?.Resolve(_theme) ?? Visual.GetBackground(block);
        if (chipBackground is { } chipColor)
            _paintList.Add(new FillRoundedRect(rect, Visual.GetCornerRadius(block) ?? _theme.ButtonRadius, chipColor));

        // S-B：显式前景覆盖优先于字号次色规则；S-C：主题资源最先解析。
        var explicitForeground = PeekForeground(block);
        foreach (var run in block.Runs)
        {
            var text = run.Text;
            if (string.IsNullOrEmpty(text)) continue;

            var fontSize = run.GetValue(TextBlock.FontSizeProperty);
            var color = explicitForeground
                        ?? (fontSize < _theme.MutedFontSizeBelow ? _theme.MutedForeground : _theme.Foreground);
            var origin = new Point(rect.X + run.FinalRect.X, rect.Y + run.FinalRect.Y);
            _text?.Paint(_paintList, text, fontSize, origin, color);
        }
    }

    private void PaintButton(Button button, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // S-B：元素级视觉覆盖（class/inline）优先，null 回落主题令牌；
        // 覆盖色完全接管按下态换色（v1 口径）。S-C：主题资源最先解析。
        var background = ResolveBackground(button, button.IsPressed ? _theme.AccentPressed : _theme.Accent);
        _paintList.Add(new FillRoundedRect(rect, _theme.ButtonRadius, background));

        var label = button.Label;
        if (string.IsNullOrEmpty(label) || _text is null) return;

        var fontSize = button.GetValue(TextBlock.FontSizeProperty);
        var metrics = _text.Measure(label, fontSize);
        var origin = new Point(
            rect.X + (rect.Width - metrics.Width) / 2,
            rect.Y + (rect.Height - metrics.Height) / 2);
        // 必须写段缓冲 _paintList：写 _list 会先于背景 AddRange（背景后画盖掉文字），
        // 且文字命令不进段、复用帧彻底丢失（D97 回归测试锁定）。
        _text.Paint(_paintList, label, fontSize, origin, ResolveForeground(button, _theme.OnAccent));
    }

    private void PaintCheckBox(CheckBox checkBox, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var fontSize = checkBox.GetValue(TextBlock.FontSizeProperty);
        var boxSize = fontSize * 1.1;
        var box = new Rect(rect.X, rect.Y + (rect.Height - boxSize) / 2, boxSize, boxSize);

        if (checkBox.Checked)
        {
            var checkedBg = ResolveBackground(checkBox,
                checkBox.IsPressed ? _theme.AccentPressed : _theme.Accent);
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius, checkedBg));

            var strokeWidth = Math.Max(1.5, boxSize * 0.11);
            var check = new PathGeometryBuilder()
                .MoveTo(box.X + boxSize * 0.24, box.Y + boxSize * 0.52)
                .LineTo(box.X + boxSize * 0.43, box.Y + boxSize * 0.71)
                .LineTo(box.X + boxSize * 0.77, box.Y + boxSize * 0.31)
                .Build();
            _paintList.Add(new StrokePath(check, _theme.OnAccent, strokeWidth));
        }
        else
        {
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius,
                checkBox.IsPressed ? _theme.AccentPressed : _theme.ControlBackground));
            _paintList.Add(new StrokePath(
                PathGeometry.RoundedRectangle(Inset(box, _theme.BorderWidth / 2), _theme.CheckBoxRadius),
                _theme.ControlBorder,
                _theme.BorderWidth));
        }

        var label = checkBox.Label;
        if (string.IsNullOrEmpty(label) || _text is null) return;

        var metrics = _text.Measure(label, fontSize);
        var origin = new Point(
            rect.X + boxSize + 8,
            rect.Y + (rect.Height - metrics.Height) / 2);
        // 同 PaintButton：标签必须写段缓冲（D97）；前景覆盖优先（S-B）。
        _text.Paint(_paintList, label, fontSize, origin, ResolveForeground(checkBox, _theme.Foreground));
    }

    private void PaintCard(Element element, Rect rect, Color background, Color border, double radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // S-B：卡片三件套均可被元素级覆盖替换；S-C：主题资源最先解析。
        var bg = ResolveBackground(element, background);
        var corner = Visual.GetCornerRadius(element) ?? radius;
        _paintList.Add(new FillRoundedRect(rect, corner, bg));

        if (_theme.BorderWidth <= 0 || border.A == 0) return;
        _paintList.Add(new StrokePath(
            PathGeometry.RoundedRectangle(Inset(rect, _theme.BorderWidth / 2), corner),
            border,
            _theme.BorderWidth));
    }

    private void PaintFocusRing(Element element, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var radius = element switch
        {
            Button => _theme.ButtonRadius,
            CheckBox => _theme.CheckBoxRadius,
            ListBoxItem => _theme.ItemRadius,
            _ => _theme.PanelRadius,
        };

        var ringWidth = Visual.GetFocusRingWidth(element) ?? _theme.FocusRingWidth;
        var ring = Inflate(rect, ringWidth);
        _paintList.Add(new StrokePath(
            PathGeometry.RoundedRectangle(ring, radius + ringWidth),
            _theme.FocusRing,
            ringWidth));
    }

    private static Rect Inset(Rect rect, double amount) => new(
        rect.X + amount,
        rect.Y + amount,
        Math.Max(0, rect.Width - amount * 2),
        Math.Max(0, rect.Height - amount * 2));

    private static Rect Inflate(Rect rect, double amount) => new(
        rect.X - amount,
        rect.Y - amount,
        rect.Width + amount * 2,
        rect.Height + amount * 2);
}

/// <summary>一帧的绘制结果：display list + 相对上一帧的脏区。</summary>
public readonly record struct PaintedFrame(DisplayList List, FrameDamage Damage);
