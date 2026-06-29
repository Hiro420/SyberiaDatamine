using SyberiaDatamine.Core;

namespace SyberiaDatamine.Loaders;

public static class AssetLoaderRegistry
{
	private static readonly List<IAssetLoader> _loaders = new();
	private static bool _initialized = false;

	public static void Initialize()
	{
		if (_initialized)
			return;

		_loaders.Clear();

		Register(new VxbgFileLoader());
		Register(new BinkVideoFileLoader());

		Register(new NemoFileLoader());
		Register(new SyjFileLoader());
		Register(new TgaFileLoader());
		Register(new ImageFileLoader());

		Register(new WavAudioFileLoader());
		Register(new Mp3AudioFileLoader());

		Register(new BinaryFileLoader());

		_initialized = true;
	}

	public static void Register(IAssetLoader loader)
	{
		if (!_loaders.Contains(loader))
		{
			_loaders.Add(loader);
		}
	}

	public static IAssetLoader? GetLoader(string filePath)
	{
		Initialize();
		return _loaders.FirstOrDefault(l => l.CanHandle(filePath));
	}

	public static IAssetLoader? GetLoader(byte[] data, string fileName)
	{
		Initialize();
		return _loaders.FirstOrDefault(l => l.CanHandle(data, fileName));
	}

	public static (AssetKind Kind, string LoaderId) Classify(string filePath)
	{
		var loader = GetLoader(filePath);
		return loader == null
			? (AssetKind.Unknown, "")
			: (loader.Kind, loader.Id);
	}

	public static (AssetKind Kind, string LoaderId) Classify(byte[] data, string fileName)
	{
		var loader = GetLoader(data, fileName);
		return loader == null
			? (AssetKind.Unknown, "")
			: (loader.Kind, loader.Id);
	}

	public static AssetPreviewData LoadAsset(string filePath)
	{
		var loader = GetLoader(filePath);
		if (loader == null)
		{
			return new AssetPreviewData
			{
				ErrorMessage = $"No loader found for file type: {System.IO.Path.GetExtension(filePath)}"
			};
		}

		return loader.LoadFromFile(filePath);
	}

	public static AssetPreviewData LoadAssetFromData(byte[] data, string fileName)
	{
		var loader = GetLoader(data, fileName);
		if (loader == null)
		{
			return new AssetPreviewData
			{
				ErrorMessage = $"No loader found for file type: {System.IO.Path.GetExtension(fileName)}"
			};
		}

		return loader.LoadFromData(data, fileName);
	}

	public static bool CanLoadFile(string filePath)
	{
		return GetLoader(filePath) != null;
	}

	public static bool CanLoadData(byte[] data, string fileName)
	{
		return GetLoader(data, fileName) != null;
	}
}
