using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Pano;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\Pano.TekKopya", out bool created);
        if (!created)
        {
            // Already running: ask the existing instance to show itself, then quit.
            try { EventWaitHandle.OpenExisting(@"Local\Pano.Goster").Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Pano.Goster");

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        var w = new MainWindow();
        MainWindow = w;
        w.Init(startHidden: e.Args.Contains("--hidden"));
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => w.ShowPopup(fromTray: true)), null, -1, false);
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(ClipStore.Dir);
            File.AppendAllText(Path.Combine(ClipStore.Dir, "hata.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
