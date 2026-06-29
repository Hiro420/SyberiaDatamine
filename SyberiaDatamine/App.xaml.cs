using System.Windows;
namespace SyberiaDatamine;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		CrashLog.Init();

		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
			CrashLog.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);

		DispatcherUnhandledException += (_, args) =>
		{
			CrashLog.Write("DispatcherUnhandledException", args.Exception);
			args.Handled = true;
		};

		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			CrashLog.Write("UnobservedTaskException", args.Exception);
			args.SetObserved();
		};

	}
}
