namespace BubuUI;

/// <summary>
/// Applies window property changes to the platform window after its native surface is created.
/// The runtime depends on this contract without knowing a concrete platform implementation.
/// </summary>
public interface IWindowLiveUpdater
{
    void SetTitle(string title);
    void SetClientSize(double width, double height);
}
