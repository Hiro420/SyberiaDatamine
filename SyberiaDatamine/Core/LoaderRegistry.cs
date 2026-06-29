using System.IO;

namespace SyberiaDatamine.Core;

public static class LoaderRegistry
{
	private static readonly List<IAssetLoader> _loaders = new();

	public static void Register(IAssetLoader loader) => _loaders.Add(loader);

	public static (AssetKind kind, string loaderId) Classify(string filePath)
	{
		foreach (var loader in _loaders)
		{
			try
			{
				if (loader.CanHandle(filePath))
					return (loader.Kind, loader.Id);
			}
			catch { }
		}
		return (AssetKind.Unknown, "");
	}

	public static (AssetKind kind, string loaderId) Classify(byte[] data, string fileName)
	{
		foreach (var loader in _loaders)
		{
			try
			{
				if (loader.CanHandle(data, fileName))
					return (loader.Kind, loader.Id);
			}
			catch { }
		}
		return (AssetKind.Unknown, "");
	}

	public static AssetPreviewData LoadAsset(string filePath)
	{
		foreach (var loader in _loaders)
		{
			try
			{
				if (loader.CanHandle(filePath))
					return loader.LoadFromFile(filePath);
			}
			catch { }
		}
		return Fallback(filePath);
	}

	public static AssetPreviewData LoadAssetFromData(byte[] data, string fileName)
	{
		foreach (var loader in _loaders)
		{
			try
			{
				if (loader.CanHandle(data, fileName))
					return loader.LoadFromData(data, fileName);
			}
			catch { }
		}
		return FallbackData(data, fileName);
	}

	private static AssetPreviewData Fallback(string filePath)
	{
		try
		{
			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			int cap = (int)Math.Min(fs.Length, 512);
			var buf = new byte[cap];
			fs.Read(buf, 0, cap);
			return new AssetPreviewData
			{
				MetadataText = $"Unknown asset  ({new FileInfo(filePath).Length:N0} B)",
				RawPreviewBytes = buf,
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = ex.Message };
		}
	}

	private static AssetPreviewData FallbackData(byte[] data, string fileName)
	{
		int cap = Math.Min(data.Length, 512);
		var buf = new byte[cap];
		Array.Copy(data, buf, cap);
		return new AssetPreviewData
		{
			MetadataText = $"Unknown asset — {fileName}  ({data.Length:N0} B)",
			RawPreviewBytes = buf,
		};
	}
}
