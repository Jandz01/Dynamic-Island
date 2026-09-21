using System;
using System.IO;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.WriteAllText(logFile, args.ExceptionObject.ToString());
            }
            catch { }
        };
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.WriteAllText(logFile, args.Exception.ToString());
            }
            catch { }
        };
        base.OnStartup(e);
    }
}
