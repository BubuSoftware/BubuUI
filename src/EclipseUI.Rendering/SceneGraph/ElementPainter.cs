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
/// (RenderVersion, 绝对位置, 尺寸, 状态, 焦点视觉描述符) 缓存——树未变的元素直接复用命令段，
/// 不再逐条重新构造（帧间分配趋零）；失效契约与 <c>DamageTracker</c> 一致：
/// 状态/样式递增 <see cref="Element.RenderVersion"/>；导航项与裁剪几何进入同一描述符。
/// 自绘段先于子内容输出，焦点段在子内容与自身裁剪结束后输出。
/// clip 作用域与滚动条每帧重新生成（位置可能变、且成本可忽略），不属于段。
/// 金标准：retained 构建与全量构建渲染结果逐像素一致（<c>RetainedPaintTests</c> 锁定）。
/// <see cref="BuildFrame"/> 额外产出元素级脏区，宿主据此跳过空闲帧的上屏。
/// </summary>
public sealed class ElementPainter
{
    /// <summary>单元素自绘命令段；缓存后视为只读。</summary>
    private sealed record Segment(long Version, Point Position, Size Size, PseudoStates States,
        FocusVisualDescriptor Focus, List<DrawCommand> Commands, List<DrawCommand> FocusCommands);

    private ElementTheme _theme;
    private readonly ITextPainter? _text;
    private readonly DamageTracker _damageTracker = new();
    private readonly ConditionalWeakTable<Element, Segment> _segments = new();

    private DisplayList _list = null!;
    private DisplayList _paintList = null!;
    private readonly List<(Elements.Popup Popup, Point Offset)> _popups = new();
    private FocusVisualDescriptor _focus;
    private double _aaMargin;
    private int _reusedSegments;
    private int _repaintedSegments;

    public ElementPainter(ElementTheme? theme = null, ITextPainter? textPainter = null)
    {
        _theme = theme ?? new ElementTheme();
        _text = textPainter;
    }

    public ElementTheme Theme => _theme;

    /// <summary>Apply scoped font and control metrics before layout, including newly attached children.</summary>
    public void Prepare(Element root) => root.SetThemeDefaults(_theme as IElementThemeDefaults);

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
    public DisplayList Build(Element root, Size size, double scale = 1)
    {
        _damageTracker.Reset();
        return BuildCore(root, size, scale, tracker: null);
    }

    /// <summary>
    /// 构建一帧并返回相对上一帧的脏区。首帧 / 尺寸 / DPI 变化为整帧；
    /// 脏区为空时宿主可跳过上屏（画面保持不变）。
    /// </summary>
    public PaintedFrame BuildFrame(Element root, Size size, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(root);
        var damage = _damageTracker.Begin(size, scale);
        var list = BuildCore(root, size, scale, _damageTracker);
        _damageTracker.End();
        return new PaintedFrame(list, damage);
    }

    /// <summary>丢弃脏区历史，下一帧强制整帧重绘（窗口遮挡恢复等场景）。</summary>
    public void ResetDamage() => _damageTracker.Reset();

    private DisplayList BuildCore(Element root, Size size, double scale, DamageTracker? tracker)
    {
        ArgumentNullException.ThrowIfNull(root);

        var list = new DisplayList(size);
        _list = list;
        _paintList = list;
        _focus = FocusVisualDescriptor.Resolve(root, _theme);
        _aaMargin = scale > 0 ? Math.Max(1, 1 / scale) : 1;
        _popups.Clear();
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
        for (var i = 0; i < _popups.Count; i++)
        {
            var entry = _popups[i];
            PaintElement(entry.Popup, entry.Offset, false, tracker, overlay: true);
        }

        if (clipSurface) list.Add(new PopClip());
        if (scaled) list.Add(new PopTransform());

        LastBuildStats = (_reusedSegments, _repaintedSegments);
        return list;
    }

    private void PaintElement(Element element, Point offset, bool isRoot, DamageTracker? tracker, bool overlay = false)
    {
        if (!element.IsEffectivelyVisible) return;
        if (element is Elements.Popup popup && !overlay) { _popups.Add((popup, offset)); return; }
        var final = element.FinalRect;
        var rect = new Rect(offset.X + final.X, offset.Y + final.Y, final.Width, final.Height);
        var selfBounds = SelfBounds(element, rect);
        var drawingClip = element.GetDrawingClip();

        var states = element.PseudoStates;
        var focus = ReferenceEquals(element, _focus.Owner) ? _focus : default;
        if (element.IsFocusWithin && ReferenceEquals(element.FocusVisualTarget, element) && element.EffectiveFocusVisualMode == FocusVisualMode.Control)
            focus = new(element, FocusManager.GetFor(element)?.Focused, rect, Control: true);
        var visualBounds = element is Elements.Popup ? Inflate(selfBounds, _theme.PopupShadowSize * 2 + _aaMargin) : rect;
        if (drawingClip is { } drawingBounds) visualBounds = FrameDamage.Union(visualBounds, ToRoot(drawingBounds, rect.Location));
        if (focus.Owner is not null && !focus.Control)
            visualBounds = FrameDamage.Union(visualBounds, Inflate(focus.Bounds, Math.Max(0, focus.Offset + focus.Width / 2) + _aaMargin));
        tracker?.Visit(element, visualBounds, focus);

        // retained 段：自绘命令（类型绘制 + 卡片 + 焦点环）按失效键复用。
        if (_segments.TryGetValue(element, out var segment) &&
            segment.Version == element.RenderVersion &&
            segment.Position == selfBounds.Location &&
            segment.Size == selfBounds.Size &&
            segment.States == states && segment.Focus == focus)
        {
            _list.AddRange(segment.Commands);
            _reusedSegments++;
        }
        else
        {
            var buffer = new DisplayList();
            _paintList = buffer;
            PaintSelf(element, rect, isRoot);
            // Bound both built-in drawing and the custom Render path. Cache the balanced
            // scope with the commands; empty layout/coordination nodes need no extra clip.
            if (buffer.Commands.Count > 0 && drawingClip is { } selfClip)
            {
                buffer.Commands.Insert(0, new PushClip(ToRoot(selfClip, rect.Location)));
                buffer.Add(new PopClip());
            }
            var decoration = new DisplayList();
            _paintList = decoration;
            if (focus.Owner is not null) PaintFocusRing(element, rect, focus);
            _paintList = _list;
            _segments.Remove(element);
            segment = new Segment(element.RenderVersion, selfBounds.Location, selfBounds.Size, states, focus, buffer.Commands, decoration.Commands);
            _segments.Add(element, segment);
            _list.AddRange(buffer.Commands);
            _repaintedSegments++;
        }

        // Content regions come from the control, shared with hit testing and focus geometry.
        var contentClip = element.GetContentClip();
        if (contentClip is { } childClip) _list.Add(new PushClip(ToRoot(childClip, rect.Location)));

        // G2 的可视子节点全部来自 Panel（TextBlock 的 run 也是 Panel.Children 的一部分）。
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
                PaintElement(child, rect.Location, isRoot: false, tracker);
        }

        if (contentClip is not null) _list.Add(new PopClip());

        // 滚动条画在内容之上（裁剪作用域外也不越界：位置按视口矩形内缩计算）。
        if (element is ScrollViewer viewer)
            PaintScrollBar(viewer, rect);
        _list.AddRange(segment.FocusCommands);
    }

    /// <summary>元素的自身绘制（写入当前段缓冲；子树由 <see cref="PaintElement"/> 递归处理）。</summary>
    private void PaintSelf(Element element, Rect rect, bool isRoot)
    {
        if (element.UsesDefaultDrawing) switch (element)
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

        if (element.UsesDefaultDrawing && !isRoot && !element.IsVisualTransparent)
        {
            switch (element)
            {
                case ListBoxItem item:
                    PaintCard(element, rect, item.IsSelected ? _theme.SelectionBackground : ItemBackground(element), _theme.ItemBorder, _theme.ItemRadius);
                    if (item.IsSelected && _theme.TransparentPanels)
                        _paintList.Add(new FillRoundedRect(new(rect.X + 3, rect.Y + 6, 3, Math.Max(0, rect.Height - 12)), 1.5, element.IsEffectiveEnabled ? _theme.Accent : _theme.DisabledForeground));
                    break;
                case Elements.Popup:
                    PaintPopup(element, rect);
                    break;
                case ScrollViewer:
                    PaintCard(element, rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
                case StackPanel:
                    if (_theme.TransparentPanels && Visual.GetBackground(element) is null && Visual.GetBackgroundResource(element) is null) break;
                    PaintCard(element, rect, _theme.PanelBackground, _theme.PanelBorder, _theme.PanelRadius);
                    break;
            }
        }

        var context = new ElementDrawingContext(_paintList, rect.Location, _theme, _text,
            ResolveForeground(element, _theme.Foreground), ResolveBackground(element, ControlBackground(element)), element);
        element.Render(context);
        context.Complete();
    }

    /// <summary>叠加式滚动条：可滚动方向在视口内缘画拇指，命中/拖动由 ScrollViewer 处理。
    /// 出条判据走方向开关（design/05 §布局契约）：Auto 按需、Visible 恒显、
    /// Hidden/Disabled 不出条。</summary>
    private void PaintScrollBar(ScrollViewer viewer, Rect rect)
    {
        var clipped = false;
        for (var i = 0; i < 2; i++)
        {
            if (!viewer.TryGetScrollBarGeometry(i == 1, out var bar)) continue;
            if (!clipped) { _list.Add(new PushClip(rect)); clipped = true; }
            var thumb = bar.Thumb;
            _paintList.Add(new FillRoundedRect(
                new Rect(rect.X + thumb.X, rect.Y + thumb.Y, thumb.Width, thumb.Height),
                3, _theme.ScrollBarThumb));
        }
        if (clipped) _list.Add(new PopClip());
    }

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

    private Color ItemBackground(Element element) => element.IsPressed ? _theme.ItemPressedBackground : element.IsHovered ? _theme.ItemHoverBackground : _theme.ItemBackground;
    private Color ControlBackground(Element element) => element switch
    {
        MenuItem or TreeViewItem => ItemBackground(element),
        Calendar or Elements.Popup => _theme.PopupBackground,
        _ => element.IsPressed ? _theme.ControlPressedBackground : element.IsHovered ? _theme.ControlHoverBackground : _theme.ControlBackground,
    };

    private void PaintPopup(Element element, Rect rect)
    {
        rect = SelfBounds(element, rect);
        var size = _theme.PopupShadowSize;
        if (size > 0)
        {
            for (var i = 3; i >= 1; i--)
            {
                var spread = size * i / 3;
                var shadow = new Color(_theme.PopupShadow.R, _theme.PopupShadow.G, _theme.PopupShadow.B, (byte)(_theme.PopupShadow.A / 3));
                _paintList.Add(new FillRoundedRect(new(rect.X - spread, rect.Y + size - spread, rect.Width + spread * 2, rect.Height + spread * 2), _theme.PopupRadius + spread, shadow));
            }
        }
        PaintCard(element, rect, _theme.PopupBackground, _theme.PopupBorder, _theme.PopupRadius);
    }

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

        // Wrap 多行（design/05 §布局契约）：段是 run 文本的切片，按行布局定位——
        // 跨行 run 的 FinalRect 只落在首段，这里必须走段序列而不是 run.FinalRect。
        if (block.TextLines.Count > 0)
        {
            foreach (var line in block.TextLines)
            {
                foreach (var segment in line.Segments)
                {
                    if (string.IsNullOrEmpty(segment.Text)) continue;
                    var color = explicitForeground
                                ?? (segment.FontSize < _theme.MutedFontSizeBelow
                                    ? _theme.MutedForeground
                                    : _theme.Foreground);
                    var origin = new Point(rect.X + segment.X, rect.Y + line.Y);
                    _text?.Paint(_paintList, segment.Text, segment.FontSize, origin, color);
                }
            }
            return;
        }

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
        var background = ResolveBackground(button, button.IsPressed ? _theme.ButtonPressedBackground : button.IsHovered ? _theme.ButtonHoverBackground : _theme.ButtonBackground);
        var radius = Visual.GetCornerRadius(button) ?? _theme.ButtonRadius;
        _paintList.Add(new FillRoundedRect(rect, radius, background));
        var border = button.IsEffectiveEnabled || _theme.ButtonBorder.A == 0 ? _theme.ButtonBorder : _theme.DisabledBorder;
        if (border.A > 0 && _theme.BorderWidth > 0)
            _paintList.Add(new StrokePath(PathGeometry.RoundedRectangle(Inset(rect, _theme.BorderWidth / 2), radius), border, _theme.BorderWidth));

        if (button.Content is not null) return;
        var label = button.Label;
        if (string.IsNullOrEmpty(label) || _text is null) return;

        var fontSize = button.GetValue(TextBlock.FontSizeProperty);
        var metrics = _text.Measure(label, fontSize);
        var labelBounds = button.GetLabelBounds();
        var origin = new Point(
            rect.X + labelBounds.X + (labelBounds.Width - metrics.Width) / 2,
            rect.Y + labelBounds.Y + (labelBounds.Height - metrics.Height) / 2);
        // 必须写段缓冲 _paintList：写 _list 会先于背景 AddRange（背景后画盖掉文字），
        // 且文字命令不进段、复用帧彻底丢失（D97 回归测试锁定）。
        _paintList.Add(new PushClip(ToRoot(labelBounds, rect.Location)));
        _text.Paint(_paintList, label, fontSize, origin, ResolveForeground(button, _theme.ButtonForeground));
        _paintList.Add(new PopClip());
    }

    private void PaintCheckBox(CheckBox checkBox, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var fontSize = checkBox.GetValue(TextBlock.FontSizeProperty);
        var boxSize = fontSize * 1.1;
        var box = new Rect(rect.X, rect.Y + (rect.Height - boxSize) / 2, boxSize, boxSize);

        if (checkBox.IsIndeterminate)
        {
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius, ResolveBackground(checkBox, AccentBackground(checkBox))));
            _paintList.Add(new FillRect(new Rect(box.X + boxSize * .2, box.Y + boxSize * .43, boxSize * .6, boxSize * .14), checkBox.IsEffectiveEnabled ? _theme.OnAccent : _theme.DisabledForeground));
        }
        else if (checkBox.Checked)
        {
            var checkedBg = ResolveBackground(checkBox,
                AccentBackground(checkBox));
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius, checkedBg));

            var strokeWidth = Math.Max(1.5, boxSize * 0.11);
            var check = new PathGeometryBuilder()
                .MoveTo(box.X + boxSize * 0.24, box.Y + boxSize * 0.52)
                .LineTo(box.X + boxSize * 0.43, box.Y + boxSize * 0.71)
                .LineTo(box.X + boxSize * 0.77, box.Y + boxSize * 0.31)
                .Build();
            _paintList.Add(new StrokePath(check, checkBox.IsEffectiveEnabled ? _theme.OnAccent : _theme.DisabledForeground, strokeWidth));
        }
        else
        {
            _paintList.Add(new FillRoundedRect(box, _theme.CheckBoxRadius,
                ResolveBackground(checkBox, ControlBackground(checkBox))));
            _paintList.Add(new StrokePath(
                PathGeometry.RoundedRectangle(Inset(box, _theme.BorderWidth / 2), _theme.CheckBoxRadius),
                checkBox.IsEffectiveEnabled ? checkBox.IsHovered ? _theme.ControlHoverBorder : _theme.ControlBorder : _theme.DisabledBorder,
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

    private Color AccentBackground(Element element) => element.IsPressed ? _theme.AccentPressed : element.IsHovered ? _theme.AccentHover : _theme.Accent;

    private void PaintFocusRing(Element element, Rect rect, FocusVisualDescriptor focus)
    {
        if (focus.Control)
        {
            var context = new ElementDrawingContext(_paintList, rect.Location, _theme, _text,
                ResolveForeground(element, _theme.Foreground), ResolveBackground(element, ControlBackground(element)), element);
            element.RenderFocusVisual(context); context.Complete(); return;
        }
        var ring = Inflate(focus.Bounds, focus.Offset);
        if (ring.Width <= 0 || ring.Height <= 0) return;
        if (focus.Clip is { } clip) _paintList.Add(new PushClip(clip));
        _paintList.Add(new StrokePath(
            PathGeometry.RoundedRectangle(ring, Math.Max(0, focus.Radius + focus.Offset)), focus.Color, focus.Width));
        if (focus.Clip is not null) _paintList.Add(new PopClip());
    }

    private static Rect Inset(Rect rect, double amount) => new(
        rect.X + amount,
        rect.Y + amount,
        Math.Max(0, rect.Width - amount * 2),
        Math.Max(0, rect.Height - amount * 2));

    private static Rect ToRoot(Rect rect, Point offset) => new(rect.X + offset.X, rect.Y + offset.Y, rect.Width, rect.Height);
    private static Rect SelfBounds(Element element, Rect rect) => element is Elements.Popup { Content: { } body }
        ? ToRoot(body.FinalRect, rect.Location) : rect;

    private static Rect Inflate(Rect rect, double amount) => new(
        rect.X - amount,
        rect.Y - amount,
        rect.Width + amount * 2,
        rect.Height + amount * 2);
}

/// <summary>一帧的绘制结果：display list + 相对上一帧的脏区。</summary>
public readonly record struct PaintedFrame(DisplayList List, FrameDamage Damage);
