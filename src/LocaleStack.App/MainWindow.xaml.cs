using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LocaleStack.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    public MainWindow()
    {
        InitializeComponent();
        model = new(); DataContext = model;
        SourceInitialized += (_, _) => { var value = 1; _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref value, sizeof(int)); };
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => model.Dispose();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        await model.StartAsync(args.Contains("--offline") || screenshotIndex >= 0);
        var pageIndex = Array.IndexOf(args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < args.Length)
        {
            switch (args[pageIndex + 1])
            {
                case "tasks": model.TasksCommand.Execute(null); break;
                case "help": model.HelpCommand.Execute(null); break;
                case "settings": model.SettingsCommand.Execute(null); break;
            }
        }
        if (args.Contains("--compact")) { Width = MinWidth; Height = MinHeight; }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(args[screenshotIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(path)) encoder.Save(stream);
            if (!args.Contains("--stay-open")) Application.Current.Shutdown();
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!model.IsBusy && !model.MaintainerBusy) return;
        e.Cancel = true;
        MessageBox.Show("操作尚未结束。可先点击取消，待安装完成或回滚后再关闭译栈。", "译栈", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
