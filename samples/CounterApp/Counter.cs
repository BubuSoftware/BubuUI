using EclipseUI;

namespace Samples;

public partial class Counter
{
    public readonly Signal<int> count = new(0);
    public readonly Signal<bool> showButton = new(false);
}
