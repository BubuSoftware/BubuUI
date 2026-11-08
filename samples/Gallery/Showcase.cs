using EclipseUI.Elements;

namespace Gallery;

/// <summary>
/// 控件展示区（G4 布局切片）：Grid 行列布局（Auto/固定/星号轨道 + 附加属性落格）、
/// WrapPanel 流式换行、ScrollViewer 滚动、VirtualizingStackPanel 列表虚拟化。
/// 模板走真实编译管线——Grid.Row/Grid.Column 生成直调静态 setter，Ref 把面板交给 code-behind。
/// </summary>
public partial class Showcase
{
    /// <summary>虚拟列表面板（模板 Ref 注入；工厂由 GalleryScene 在 Mount 后接线）。</summary>
    public VirtualizingStackPanel virtualList = null!;
}
