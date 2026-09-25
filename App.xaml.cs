using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinDirectoryStats;

public partial class App : Application
{
    private const string MutexName = @"Local\WinDirectoryStats_Singleton_4C0452CF";
    private const string WindowTitle = "WinDirectoryStats";
    private const int SwRestore = 9;
    private static MainWindow? _mainWindow;
    private static Mutex? _mutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public App()
    {
        UnhandledException += (_, eventArgs) => LogException(eventArgs.Exception, "App.UnhandledException");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                LogException(exception, "AppDomain.UnhandledException");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            LogException(eventArgs.Exception, "TaskScheduler.UnobservedTaskException");
            eventArgs.SetObserved();
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            RestoreExistingInstance();
            Current.Exit();
            return;
        }

        _mainWindow = new MainWindow();
        _mainWindow.Activate();

        string? launchPath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(path => Directory.Exists(path));
        if (launchPath is not null)
        {
            _mainWindow.OpenPath(launchPath);
        }
    }

    private static void RestoreExistingInstance()
    {
        IntPtr window = FindWindow(null, WindowTitle);
        if (window == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
    }

    internal static void LogException(Exception exception, string context)
    {
        try
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinDirectoryStats");
            Directory.CreateDirectory(logDirectory);
            string entry = $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDirectory, "crash.log"), entry);
        }
        catch
        {
            // Logging must never cause a second application failure.
        }
    }
}
