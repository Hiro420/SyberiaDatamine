using System.Diagnostics;
using System.IO;
using System.Text;

internal static class CrashLog
{
	private static readonly object _lock = new();
	private static string _path = "";

	public static string Path => _path;

	public static void Init()
	{
		var dir = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"SyberiaDatamine", "logs");
		Directory.CreateDirectory(dir);

		_path = System.IO.Path.Combine(dir, $"SyberiaDatamine_{DateTime.Now:yyyyMMdd_HHmmss}.log");
		Write("Logger init");
	}

	public static void Write(string message, Exception? ex = null)
	{
		try
		{
			var sb = new StringBuilder();
			sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ");
			sb.Append(message);

			if (ex != null)
				sb.AppendLine().Append(ex);

			sb.AppendLine()
			  .Append("Thread=").Append(Thread.CurrentThread.ManagedThreadId)
			  .Append("  Is64Bit=").Append(Environment.Is64BitProcess)
			  .Append("  PID=").Append(Process.GetCurrentProcess().Id)
			  .AppendLine()
			  .AppendLine(new string('-', 80));

			lock (_lock)
				File.AppendAllText(_path, sb.ToString());
		}
		catch
		{

		}
	}
}
