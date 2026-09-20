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
            File.WriteAllText(@"D:\Dynamic Island\crash.log", args.ExceptionObject.ToString());
        };
        DispatcherUnhandledException += (s, args) =>
        {
            File.WriteAllText(@"D:\Dynamic Island\crash.log", args.Exception.ToString());
        };
        base.OnStartup(e);
    }
}
