using System.Threading;
using System.Windows;

namespace LimitsWidget.Windows;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool compositionSmoke = e.Args.Contains("--backdrop-smoke");
        bool smoke = compositionSmoke || e.Args.Contains("--smoke-test");
        instance = new Mutex(true, "Local\\CodexLimitsWidget.User." + Environment.UserName, out bool created);
        if (!created && !smoke) { Shutdown(); return; }
        var window = new MainWindow(smoke, compositionSmoke); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
