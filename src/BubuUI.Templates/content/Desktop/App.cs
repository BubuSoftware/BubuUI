using BubuUI;

namespace BubuApp;

public sealed class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        MainWindow = new MainWindow();
    }
}
