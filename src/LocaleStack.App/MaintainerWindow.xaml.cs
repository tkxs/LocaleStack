using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocaleStack.Core;

namespace LocaleStack.App;
public partial class MaintainerWindow : Window
{
    private readonly MaintainerViewModel model;
    private readonly string? previewData;
    public bool IsOperationInProgress => model.IsBusy;
    public MaintainerWindow() : this(null) { }
    public MaintainerWindow(GameEntry? game)
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Contains("--screenshot")) previewData = Path.Combine(Path.GetTempPath(), "LocaleStack-preview-" + Guid.NewGuid().ToString("N"));
        model = new(game, previewData); DataContext = model;
        SourceInitialized += (_, _) => { var value = 1; _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref value, sizeof(int)); };
        Loaded += OnLoaded;
        Closing += (_, e) => { if (model.IsBusy) { e.Cancel = true; MessageBox.Show("请先取消或等待当前操作结束，再关闭工作台。", "译栈", MessageBoxButton.OK, MessageBoxImage.Information); } };
        Closed += (_, _) =>
        {
            model.Dispose();
            if (previewData is not null && Directory.Exists(previewData)) { SafePaths.RejectLinks(previewData); Directory.Delete(previewData, true); }
        };
    }
    private async void ConnectClicked(object sender, RoutedEventArgs e)
    { await model.ConnectAsync(TokenBox.Password, RememberCredential.IsChecked == true); if (model.IsConnected) TokenBox.Clear(); }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--smoke-maintainer")) await model.ExerciseLocalWorkflowAsync();
        if (args.Contains("--compact")) { Width = MinWidth; Height = MinHeight; }
        var index = Array.IndexOf(args, "--screenshot");
        if (index >= 0 && index + 1 < args.Length)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout();
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = File.Create(path)) encoder.Save(output);
            if (!args.Contains("--stay-open")) Application.Current.Shutdown();
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
