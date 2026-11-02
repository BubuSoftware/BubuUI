using EclipseUI.Elements;

namespace EclipseUI;

/// <summary>文本内容模型宿主：生成代码通过该接口追加 run，不直接操作 Children。</summary>
public interface ITextContentHost
{
    TextRun AddRun(string text);
}
