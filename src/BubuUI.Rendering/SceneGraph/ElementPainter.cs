using System.Runtime.CompilerServices;
using BubuUI.Elements;

namespace BubuUI.Rendering;

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

    /// <summary>Paints a popup in its own surface while preserving its real owner tree and focus manager.</summary>
    public PaintedFrame BuildPopupFrame(Popup popup, Rect bounds, double scale = 1, bool transparent = true)
    {
        ArgumentNullException.ThrowIfNull(popup);
        var damage = _damageTracker.Begin(bounds.Size, scale);
        var list = BuildCore(popup, bounds.Size, scale, _damageTracker, bounds, transparent);
        _damageTracker.End();
        return new(list, damage);
    }

    /// <summary>Includes drawing outside the popup body, while respecting content viewports and separate nested popups.</summary>
    public static Rect GetPopupVisualBounds(Popup popup)
    {
        var bounds = popup.WindowBounds;
        if ((popup.TemplateRoot ?? popup.Content) is { } body) Visit(body, null);
        return bounds;

        void Visit(Element element, Rect? inheritedClip)
        {
            if (!element.IsEffectivelyVisible || element is Popup) return;
            var zero = element.PointFromRoot(Point.Zero);
            var origin = new Point(-zero.X, -zero.Y);
            var local = element.GetDrawingClip() ?? new Rect(Point.Zero, element.FinalRect.Size);
            var drawing = ToRoot(local, origin);
            if (inheritedClip is { } clip) drawing = Intersect(drawing, clip);
            if (!drawing.Size.IsEmpty) bounds = FrameDamage.Union(bounds, drawing);
            var childClip = inheritedClip;
            if (element.GetVisualChildrenClip() is { } contentClip)
            {
                var region = ToRoot(contentClip, origin);
                childClip = childClip is { } previous ? Intersect(previous, region) : region;
            }
            foreach (var child in element.VisualChildren) Visit(child, childClip);
        }

        static Rect Intersect(Rect a, Rect b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y),
            Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)),
            Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y)));
    }

    /// <summary>丢弃脏区历史，下一帧强制整帧重绘（窗口遮挡恢复等场景）。</summary>
    public void ResetDamage() => _damageTracker.Reset();

    private DisplayList BuildCore(Element root, Size size, double scale, DamageTracker? tracker, Rect? popupBounds = null, bool transparentPopup = true)
    {
        ArgumentNullException.ThrowIfNull(root);

        var list = new DisplayList(size);
        _list = list;
        _paintList = list;
        _focus = FocusVisualDescriptor.Resolve(root, _theme);
        if (popupBounds is { } focusBounds && _focus.Owner is not null)
        {
            Rect Translate(Rect r) => new(r.X - focusBounds.X, r.Y - focusBounds.Y, r.Width, r.Height);
            _focus = _focus with { Bounds = Translate(_focus.Bounds), Clip = _focus.Clip is { } clip ? Translate(clip) : null };
        }
        _aaMargin = scale > 0 ? Math.Max(1, 1 / scale) : 1;
        _popups.Clear();
        _reusedSegments = 0;
        _repaintedSegments = 0;

        var scaled = scale > 0 && Math.Abs(scale - 1) > 1e-6;
        if (scaled)
            list.Add(new PushTransform(Transform.Scale(scale)));

        if (popupBounds is null || !transparentPopup)
            list.Add(new FillRect(new Rect(0, 0, size.Width, size.Height), _theme.Surface));

        var clipSurface = size.Width > 0 && size.Height > 0;
        if (clipSurface)
            list.Add(new PushClip(new Rect(0, 0, size.Width, size.Height)));

        var origin = Point.Zero;
        if (popupBounds is { } bounds)
        {
            var parentOrigin = root.Parent?.PointFromRoot(Point.Zero) ?? Point.Zero;
            origin = new(-parentOrigin.X - bounds.X, -parentOrigin.Y - bounds.Y);
        }
        PaintElement(root, origin, isRoot: popupBounds is null, tracker, overlay: popupBounds is not null);
        if (_popups.Count != 0)
        {
            var modalRoot = root;
            while (modalRoot.Parent is not null) modalRoot = modalRoot.Parent;
            var modal = Elements.Popup.TopModal(modalRoot);
            var paintedPopups = new HashSet<Elements.Popup>();
            while (true)
            {
                var pending = _popups.Where(entry => !paintedPopups.Contains(entry.Popup)
                        && (modal is null || modal.IsInModalScope(entry.Popup) || entry.Popup.IsInModalScope(modal)))
                    .OrderBy(entry => entry.Popup.OpenOrder).ToArray();
                if (pending.Length == 0) break;
                var entry = pending[0];
                paintedPopups.Add(entry.Popup);
                PaintElement(entry.Popup, entry.Offset, false, tracker, overlay: true);
            }
        }

        if (clipSurface) list.Add(new PopClip());
        if (scaled) list.Add(new PopTransform());

        LastBuildStats = (_reusedSegments, _repaintedSegments);
        return list;
    }

    private void PaintElement(Element element, Point offset, bool isRoot, DamageTracker? tracker, bool overlay = false)
    {
        if (!element.IsEffectivelyVisible) return;
        if (element is Elements.Popup popup && !overlay)
        {
            if (!popup.IsWindowHosted) _popups.Add((popup, offset));
            return;
        }
        var final = element.FinalRect;
        var rect = new Rect(offset.X + final.X, offset.Y + final.Y, final.Width, final.Height);
        var selfBounds = SelfBounds(element, rect);
        var drawingClip = element.GetDrawingClip();

        var states = element.PseudoStates;
        var focus = ReferenceEquals(element, _focus.Owner) ? _focus : default;
        if (element.IsFocusWithin && ReferenceEquals(element.FocusVisualTarget, element) && element.EffectiveFocusVisualMode == FocusVisualMode.Control)
            focus = new(element, FocusManager.GetFor(element)?.Focused, rect, Control: true);
        var visualBounds = rect;
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
        // Popup template chrome (including its shadow) can draw outside the body.
        // Its content presenter/viewports keep content clipped, and hit testing uses the body.
        var contentClip = element is Popup popupOwner && (popupOwner.IsWindowHosted || popupOwner.TemplateRoot is not null)
            ? null : element.GetVisualChildrenClip();
        if (contentClip is { } childClip) _list.Add(new PushClip(ToRoot(childClip, rect.Location)));

        var children = element.VisualChildren;
        for (var i = 0; i < children.Count; i++)
            PaintElement(children[i], rect.Location, isRoot: false, tracker);

        if (contentClip is not null) _list.Add(new PopClip());

        _list.AddRange(segment.FocusCommands);
    }

    /// <summary>元素的自身绘制（写入当前段缓冲；子树由 <see cref="PaintElement"/> 递归处理）。</summary>
    private void PaintSelf(Element element, Rect rect, bool isRoot)
    {
        if (element is TextBlock block) PaintTextBlock(block, rect);
        var context = new ElementDrawingContext(_paintList, rect.Location, _text,
            ResolveForeground(element), ResolveBackground(element), element);
        element.Render(context);
        context.Complete();
    }

    private Color? PeekForeground(Element element) => Visual.GetForegroundResource(element)?.Resolve(_theme) ?? Visual.GetForeground(element);
    private Color ResolveForeground(Element element) => PeekForeground(element) ?? ToColor(element.Foreground);
    private Color ResolveBackground(Element element) => Visual.GetBackgroundResource(element)?.Resolve(_theme) ?? Visual.GetBackground(element) ?? ToColor(element.Background);
    private static Color ToColor(UiColor color) => new(color.R, color.G, color.B, color.A);

    private void PaintTextBlock(TextBlock block, Rect rect)
    {
        // S-B/E5：TextBlock 也可携带 class/local 背景（注册位状态芯片等——四层值盒有值即画，
        // 主题资源最先解析）；null = 维持文字控件透明惯例，不加背景命令。
        // 圆角读 class/local 覆盖（.spinner 的 CornerRadius），未设置回落按钮半径令牌。
        var chipBackground = Visual.GetBackgroundResource(block)?.Resolve(_theme) ?? Visual.GetBackground(block);
        if (chipBackground is { } chipColor)
            _paintList.Add(new FillRoundedRect(rect, Visual.GetCornerRadius(block) ?? block.CornerRadius, chipColor));

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
                    var color = explicitForeground ?? ToColor(block.Foreground);
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
            var color = explicitForeground ?? ToColor(block.Foreground);
            var origin = new Point(rect.X + run.FinalRect.X, rect.Y + run.FinalRect.Y);
            _text?.Paint(_paintList, text, fontSize, origin, color);
        }
    }

    private void PaintFocusRing(Element element, Rect rect, FocusVisualDescriptor focus)
    {
        if (focus.Control)
        {
            var context = new ElementDrawingContext(_paintList, rect.Location, _text,
                ResolveForeground(element), ResolveBackground(element), element);
            element.RenderFocusVisual(context); context.Complete(); return;
        }
        var ring = Inflate(focus.Bounds, focus.Offset);
        if (ring.Width <= 0 || ring.Height <= 0) return;
        if (focus.Clip is { } clip) _paintList.Add(new PushClip(clip));
        _paintList.Add(new StrokePath(
            PathGeometry.RoundedRectangle(ring, Math.Max(0, focus.Radius + focus.Offset)), focus.Color, focus.Width));
        if (_theme.FocusRingInner.A > 0)
        {
            var inset = focus.Width / 2 + .5;
            _paintList.Add(new StrokePath(PathGeometry.RoundedRectangle(Inset(ring, inset), Math.Max(0, focus.Radius + focus.Offset - inset)), _theme.FocusRingInner, 1));
        }
        if (focus.Clip is not null) _paintList.Add(new PopClip());
    }

    private static Rect Inset(Rect rect, double amount) => new(
        rect.X + amount,
        rect.Y + amount,
        Math.Max(0, rect.Width - amount * 2),
        Math.Max(0, rect.Height - amount * 2));

    private static Rect ToRoot(Rect rect, Point offset) => new(rect.X + offset.X, rect.Y + offset.Y, rect.Width, rect.Height);
    private static Rect SelfBounds(Element element, Rect rect) => rect;

    private static Rect Inflate(Rect rect, double amount) => new(
        rect.X - amount,
        rect.Y - amount,
        rect.Width + amount * 2,
        rect.Height + amount * 2);
}

/// <summary>一帧的绘制结果：display list + 相对上一帧的脏区。</summary>
public readonly record struct PaintedFrame(DisplayList List, FrameDamage Damage);
