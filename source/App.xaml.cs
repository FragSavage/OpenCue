using System.Windows;

namespace OpenCue;
public partial class App : Application
{
    Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            int code = SelfTests.Run(e.Args[1]); Shutdown(code); return;
        }
        instanceMutex=new Mutex(true,@"Local\OpenCue.AudioUtility",out bool firstInstance);
        if(!firstInstance){MessageBox.Show("OpenCue is already open. Use its existing window.","OpenCue",MessageBoxButton.OK,MessageBoxImage.Information);instanceMutex.Dispose();instanceMutex=null;Shutdown();return;}
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "OpenCue could not finish that action", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainWindow = new MainWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e){if(instanceMutex is not null){instanceMutex.ReleaseMutex();instanceMutex.Dispose();}base.OnExit(e);}
}
