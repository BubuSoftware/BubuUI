namespace EclipseUI.Elements;

/// <summary>
/// Grid 附加属性载体（design/14 §14.5.5；G4 首批切片）。模板写 <c>Grid.Row="1"</c> 时
/// 生成代码直调 <see cref="SetRow"/>；行/列值挂在任意元素上（含组件实例）。
/// 本切片只提供附加属性的写入与读取；Grid 布局本身（行列定义、测量/排列、星号分配）
/// 在 G4 的 Grid 布局切片落地，届时本类扩展为 Panel 子类并保留这些静态成员。
/// </summary>
public static class Grid
{
    public static readonly AttachedProperty<int> RowProperty = new("Row", typeof(Grid));

    public static readonly AttachedProperty<int> ColumnProperty = new("Column", typeof(Grid));

    public static int GetRow(Element? element) => RowProperty.GetValue(element);

    public static void SetRow(Element element, int value) => RowProperty.SetValue(element, value);

    public static int GetColumn(Element? element) => ColumnProperty.GetValue(element);

    public static void SetColumn(Element element, int value) => ColumnProperty.SetValue(element, value);
}
