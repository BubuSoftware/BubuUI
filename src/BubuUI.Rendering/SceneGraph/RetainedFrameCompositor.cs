namespace BubuUI.Rendering;

/// <summary>
/// CPU 侧 retained / 局部重绘原型（B2）。持有上一帧 <see cref="RenderTarget"/>，
/// 接受 <see cref="FrameDamage"/>，只把脏区从「整帧重绘的临时缓冲」合成进保留帧；
/// 脏区外保持上一帧像素，脏区内与整帧重绘结果一致。
///
/// 前提与限制（务必阅读，主代理统一验收时请重点检查）：
///  - <see cref="IRenderBackend.Render"/> 当前总是整帧渲染，没有 scissor / 局部渲染 API。
///    因此 scratch 仍是整帧重绘，本类只在「合成层」采纳脏区来模拟 retained 上屏，
///    **并未减少光栅化工作量**；要真正少画，需要后端提供脏区裁剪能力（见返回报告）。
///  - 调用方提供的 <see cref="FrameDamage"/> 必须覆盖所有因 display list 变化而改变的像素
///    （含抗锯齿 / 描边 / 阴影外溢）。若变化溢出脏区，合成结果将与整帧重绘不等价——
///    这是 damage 估计的固有前提，不是合成器能修正的。
///  - 首帧、<c>IsFull</c>、<see cref="Reset"/> 后一律整帧重绘；空 damage 直接返回保留帧。
///  - 纯 CPU，不依赖 GPU / 窗口；backend 生命周期归调用方（本类不 Dispose 它）。
/// </summary>
public sealed class RetainedFrameCompositor
{
    private readonly IRenderBackend _backend;
    private readonly RenderTarget _front;
    private readonly RenderTarget _scratch;
    private bool _hasFrame;

    public RetainedFrameCompositor(IRenderBackend backend, int width, int height)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _front = new RenderTarget(width, height);
        _scratch = new RenderTarget(width, height);
    }

    public int Width => _front.Width;

    public int Height => _front.Height;

    public bool HasFrame => _hasFrame;

    /// <summary>当前保留帧（逐帧复用同一实例；需要留存请用 <see cref="SnapshotPixels"/>）。</summary>
    public RenderTarget Frame => _front;

    /// <summary>当前帧像素拷贝，便于跨帧断言。</summary>
    public byte[] SnapshotPixels() => (byte[])_front.Pixels.Clone();

    /// <summary>放弃历史，下一帧按整帧重绘。</summary>
    public void Reset() => _hasFrame = false;

    /// <summary>
    /// 渲染一帧并返回保留帧。首帧 / 整帧脏区直接渲染到 front；
    /// 局部脏区先整帧渲染到 scratch，再把脏区像素拷进 front（脏区外保持上一帧）。
    /// </summary>
    public RenderTarget Composite(DisplayList list, FrameDamage damage)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(damage);

        if (!_hasFrame || damage.IsFull)
        {
            _backend.Render(list, _front);
            _hasFrame = true;
            return _front;
        }

        // 空闲帧：无脏区，跳过整帧渲染，直接复用上一帧。
        if (damage.IsEmpty)
            return _front;

        _backend.Render(list, _scratch);
        foreach (var rect in damage.Rects)
            CopyRegion(_scratch, _front, rect);

        return _front;
    }

    /// <summary>
    /// 把 src 的脏区像素拷到 dst。脏区是 double 逻辑像素，向外取整覆盖抗锯齿边缘；
    /// 不做额外外扩——调用方应提供已包含 AA / 描边外溢的脏区（见类注释）。
    /// </summary>
    private void CopyRegion(RenderTarget src, RenderTarget dst, Rect rect)
    {
        int x0 = Math.Max(0, (int)Math.Floor(rect.Left));
        int y0 = Math.Max(0, (int)Math.Floor(rect.Top));
        int x1 = Math.Min(Width, (int)Math.Ceiling(rect.Right));
        int y1 = Math.Min(Height, (int)Math.Ceiling(rect.Bottom));
        if (x1 <= x0 || y1 <= y0)
            return;

        int byteCount = (x1 - x0) * 4;
        for (int y = y0; y < y1; y++)
        {
            int offset = y * src.Stride + x0 * 4;
            Buffer.BlockCopy(src.Pixels, offset, dst.Pixels, offset, byteCount);
        }
    }
}
