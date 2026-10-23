using System.Windows;
using System.IO;

namespace LocaleStack.App;
public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        var maintainer = e.Args.Contains("--maintainer") || string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "LocaleStack.Maintainer", StringComparison.OrdinalIgnoreCase);
        if (maintainer) StartupUri = new Uri("MaintainerWindow.xaml", UriKind.Relative);
        instanceMutex = new Mutex(false, maintainer ? "LocaleStack.Maintainer" : "LocaleStack.Desktop", out var created);
        if (!created)
        {
            MessageBox.Show("译栈已经在运行。请在已打开的窗口中继续操作。", "译栈", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, exception) =>
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaleStack");
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "errors.log"), DateTimeOffset.Now + " " + exception.Exception + Environment.NewLine); }
            catch (IOException) { }
            if (e.Args.Contains("--screenshot")) { exception.Handled = true; Shutdown(1); return; }
            MessageBox.Show("译栈遇到了问题：\n" + exception.Exception.Message, "译栈", MessageBoxButton.OK, MessageBoxImage.Error);
            exception.Handled = true;
        };
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
}
