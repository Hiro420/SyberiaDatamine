using SharpCompress.Archives;
using SharpCompress.Common;
using System.IO;
using System.Net.Http;

namespace SyberiaDatamine;

internal static class FfmpegBootstrapper
{
	private static readonly Uri Ffmpeg7zUrl = new(
		"https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2024-04-30-12-51/ffmpeg-n7.0-21-gfb8f0ea7b3-win64-gpl-shared-7.0.zip");

	private static readonly string AppFfmpegDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"SyberiaDatamine",
		"ffmpeg");

	private static readonly string CacheRoot = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"SyberiaDatamine",
		"deps");

	private static readonly string LogPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"SyberiaDatamine",
		"logs",
		"ffmpeg_bootstrap.log");

	private static readonly object StartLock = new();
	private static Task? _ensureTask;
	private static string? _resolvedFfmpegDir;

	public static event Action<FfmpegProgress>? ProgressChanged;

	public static Task EnsureStartedAsync(CancellationToken ct = default)
	{
		lock (StartLock)
		{
			if (_ensureTask == null || _ensureTask.IsCanceled || _ensureTask.IsFaulted)
				_ensureTask = EnsureFfmpegPresentAsync(ct);
			return _ensureTask;
		}
	}

	public static bool IsReady
		=> LooksLikeFfmpegDllFolder(_resolvedFfmpegDir ?? string.Empty) || LooksLikeFfmpegDllFolder(AppFfmpegDir);

	public static string? ResolvedFfmpegDir
	{
		get
		{
			if (LooksLikeFfmpegDllFolder(_resolvedFfmpegDir ?? string.Empty))
				return _resolvedFfmpegDir;
			return LooksLikeFfmpegDllFolder(AppFfmpegDir) ? AppFfmpegDir : null;
		}
	}

	private static void Report(string stage, double? progress = null, string? detail = null)
	{
		ProgressChanged?.Invoke(new FfmpegProgress(stage, progress, detail));
		TryLog($"{DateTime.Now:O} | {stage} | {progress:0.000} | {detail}");
	}

	private static void TryLog(string line)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
			File.AppendAllText(LogPath, line + Environment.NewLine);
		}
		catch
		{

		}
	}

	private static bool LooksLikeFfmpegDllFolder(string dir)
	{
		if (!Directory.Exists(dir)) return false;

		if (File.Exists(Path.Combine(dir, "ffmpeg.exe")))
			return true;

		var files = Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.Where(f => f != null)
			.ToArray();

		bool hasCodec = files.Any(f => f!.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase));
		bool hasFormat = files.Any(f => f!.StartsWith("avformat-", StringComparison.OrdinalIgnoreCase));
		bool hasUtil = files.Any(f => f!.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase));

		return hasCodec && hasFormat && hasUtil;
	}

	private static async Task EnsureFfmpegPresentAsync(CancellationToken ct)
	{
		var env = Environment.GetEnvironmentVariable("SYBERIA_FFMPEG_DIR");
		if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env) && LooksLikeFfmpegDllFolder(env))
		{
			_resolvedFfmpegDir = env;
			Report("FFmpeg found via SYBERIA_FFMPEG_DIR", 1, env);
			TrySetFfmeDirectory(env);
			return;
		}

		if (LooksLikeFfmpegDllFolder(AppFfmpegDir))
		{
			_resolvedFfmpegDir = AppFfmpegDir;
			Report("FFmpeg already present", 1, AppFfmpegDir);
			TrySetFfmeDirectory(AppFfmpegDir);
			return;
		}

		Directory.CreateDirectory(CacheRoot);
		Directory.CreateDirectory(AppFfmpegDir);

		using var mutex = new Mutex(false, @"Local\SyberiaDatamine_FFmpegInstall");
		Report("Waiting for FFmpeg installer lock", 0);

		while (true)
		{
			ct.ThrowIfCancellationRequested();
			if (mutex.WaitOne(TimeSpan.FromMilliseconds(250)))
				break;
		}

		try
		{
			if (LooksLikeFfmpegDllFolder(AppFfmpegDir))
			{
				_resolvedFfmpegDir = AppFfmpegDir;
				Report("FFmpeg installed by another instance", 1, AppFfmpegDir);
				TrySetFfmeDirectory(AppFfmpegDir);
				return;
			}

			var archivePath = Path.Combine(CacheRoot, "ffmpeg-n7.0-21-gfb8f0ea7b3-win64-gpl-shared-7.0.zip");
			var extractRoot = Path.Combine(CacheRoot, "ffmpeg_extract");

			if (!File.Exists(archivePath) || new FileInfo(archivePath).Length < 10 * 1024 * 1024)
			{
				Report("Downloading FFmpeg", 0);
				await DownloadAsync(Ffmpeg7zUrl, archivePath, p => Report("Downloading FFmpeg", p), ct).ConfigureAwait(false);
			}
			else
			{
				Report("FFmpeg archive already downloaded", 0.2, archivePath);
			}

			ct.ThrowIfCancellationRequested();

			if (Directory.Exists(extractRoot))
				Directory.Delete(extractRoot, recursive: true);
			Directory.CreateDirectory(extractRoot);

			Report("Extracting FFmpeg", 0);
			await Task.Run(() => Extract7z(archivePath, extractRoot, ct), ct).ConfigureAwait(false);

			ct.ThrowIfCancellationRequested();

			var top = Directory.EnumerateDirectories(extractRoot).FirstOrDefault();
			if (top == null) throw new InvalidOperationException("FFmpeg extraction produced no top-level folder.");

			var binDir = Path.Combine(top, "bin");
			if (!Directory.Exists(binDir))
				throw new DirectoryNotFoundException("FFmpeg bin directory not found after extraction.");

			Report("Installing FFmpeg", 0.95);
			CopyIfNewer(binDir, AppFfmpegDir, "*.dll");
			CopyIfNewer(binDir, AppFfmpegDir, "ffmpeg.exe");
			CopyIfNewer(binDir, AppFfmpegDir, "ffprobe.exe");

			if (!LooksLikeFfmpegDllFolder(AppFfmpegDir))
				throw new InvalidOperationException("FFmpeg install completed but ffmpeg.exe was not found.");

			_resolvedFfmpegDir = AppFfmpegDir;
			Report("FFmpeg ready", 1, AppFfmpegDir);
			TrySetFfmeDirectory(AppFfmpegDir);
		}
		catch (Exception ex)
		{
			Report("FFmpeg install failed", null, ex.Message);
			TryLog(ex.ToString());
			throw;
		}
		finally
		{
			try { mutex.ReleaseMutex(); } catch { }
		}
	}

	private static void TrySetFfmeDirectory(string dir)
	{

	}

	private static async Task DownloadAsync(Uri url, string destPath, Action<double> progress, CancellationToken ct)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
		using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();

		var total = resp.Content.Headers.ContentLength;
		await using var input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
		await using var output = File.Create(destPath);

		var buffer = new byte[128 * 1024];
		long readTotal = 0;
		int read;
		while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
		{
			await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
			readTotal += read;

			if (total.HasValue && total.Value > 0)
				progress(Math.Clamp((double)readTotal / total.Value, 0, 1));
		}
	}

	private static void Extract7z(string archivePath, string extractRoot, CancellationToken ct)
	{
		using var archive = ArchiveFactory.OpenArchive(archivePath);
		int count = archive.Entries.Count(e => !e.IsDirectory);
		int idx = 0;

		foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
		{
			ct.ThrowIfCancellationRequested();
			idx++;

			var p = count > 0 ? (double)idx / count : 0;
			Report("Extracting FFmpeg", p, entry.Key);

			entry.WriteToDirectory(extractRoot, new ExtractionOptions
			{
				ExtractFullPath = true,
				Overwrite = true
			});
		}
	}

	private static void CopyIfNewer(string srcDir, string dstDir, string pattern)
	{
		Directory.CreateDirectory(dstDir);

		foreach (var src in Directory.EnumerateFiles(srcDir, pattern, SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(src);
			if (string.IsNullOrWhiteSpace(name)) continue;

			var dst = Path.Combine(dstDir, name);

			if (!File.Exists(dst))
			{
				File.Copy(src, dst);
				continue;
			}

			var srcTime = File.GetLastWriteTimeUtc(src);
			var dstTime = File.GetLastWriteTimeUtc(dst);
			if (srcTime > dstTime)
				File.Copy(src, dst, overwrite: true);
		}
	}
}

internal readonly record struct FfmpegProgress(string Stage, double? Progress, string? Detail);
