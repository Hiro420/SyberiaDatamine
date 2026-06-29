using System.Collections.Concurrent;

namespace SyberiaDatamine;

internal sealed class StaExecutor : IDisposable
{
	private readonly BlockingCollection<Action> _queue = new();
	private readonly Thread _thread;

	public StaExecutor(string name)
	{
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = name
		};
		_thread.SetApartmentState(ApartmentState.STA);
		_thread.Start();
	}

	public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
	{
		var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

		void Work()
		{
			try
			{
				if (ct.IsCancellationRequested)
				{
					tcs.TrySetCanceled(ct);
					return;
				}
				tcs.TrySetResult(func());
			}
			catch (Exception ex)
			{
				tcs.TrySetException(ex);
			}
		}

		_queue.Add(Work, ct);
		return tcs.Task;
	}

	private void Run()
	{
		foreach (var action in _queue.GetConsumingEnumerable())
			action();
	}

	public void Dispose()
	{
		_queue.CompleteAdding();
		try { _thread.Join(500); } catch { }
		_queue.Dispose();
	}
}
