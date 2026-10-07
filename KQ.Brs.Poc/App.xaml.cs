using KQ.Brs.Poc.Services;
using Microsoft.UI.Xaml;

namespace KQ.Brs.Poc;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            CrashLog(ex);
            throw;
        }
        UnhandledException += (_, e) =>
        {
            // Keep the demo alive; surface the problem instead of crashing.
            CrashLog(e.Exception);
            e.Handled = true;
            AppServices.State?.ShowToast("Unexpected error", e.Exception.Message, isError: true);
        };
    }

    public static MainWindow? Window { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Window = new MainWindow();
            Window.Activate();
        }
        catch (Exception ex)
        {
            CrashLog(ex);
            throw;
        }
    }

    /// <summary>Appends to %LocalAppData%\KQ.Brs.Poc\errors.log (startup XAML errors are otherwise silent).</summary>
    public static void CrashLog(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            File.AppendAllText(Path.Combine(SettingsStore.DataFolder, "errors.log"), $"{DateTime.Now:O}\n{ex}\n\n");
        }
        catch { }
    }
}
