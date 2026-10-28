using EclipseUI;

namespace Samples;

public partial class Prefs
{
    public readonly Signal<bool> showExtra = new(false);
    public readonly Signal<bool> extra = new(false);
}
