using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using QwenLocalChat.Core;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace QwenLocalChat_WinUI;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppCloseCoordinator _closeCoordinator = new();
    private readonly AppWindow _appWindow;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        _appWindow.SetIcon("Assets/AppIcon.ico");
        _appWindow.Closing += AppWindow_Closing;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32((int)(960 * scale), (int)(740 * scale)));

        RootFrame.Navigate(typeof(MainPage));
    }

    public void SetEndpointSubtitle(string host, int port)
        => AppTitleBar.Subtitle = $"LOCAL CORE  /  {host}:{port}";

    public WindowId AppWindowId => _appWindow.Id;

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closeCoordinator.ShouldCancelClose) return;
        args.Cancel = true;
        if (!_closeCoordinator.TryBeginCleanup()) return;

        try
        {
            if (RootFrame.Content is not MainPage page)
            {
                FinishClose();
                return;
            }

            var decision = await page.RequestCloseDecisionAsync();
            if (decision == AppCloseDecision.Cancel)
            {
                _closeCoordinator.AbortCleanup();
                return;
            }

            await page.ShutdownAsync(stopOwnedModel: decision == AppCloseDecision.ExitStopModel);
            FinishClose();
        }
        catch
        {
            _closeCoordinator.AbortCleanup();
            throw;
        }
    }

    private void FinishClose()
    {
        _closeCoordinator.ApproveClose();
        _appWindow.Closing -= AppWindow_Closing;
        Close();
    }
}
