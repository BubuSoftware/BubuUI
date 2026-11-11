using EclipseUI;

namespace Gallery;

/// <summary>
/// 最小自定义控件：用于示例与 IDE 夹具（design/06 §6.9.1 的未 build 控件场景）。
/// 无内容，量出 0×0；不参与布局计算，也不会在示例里抢占空间。
/// </summary>
public class MyControl : Element
{
    protected override void ArrangeCore(Rect final)
    {
    }

    protected override Size MeasureCore(Size available) => new(0, 0);
}
