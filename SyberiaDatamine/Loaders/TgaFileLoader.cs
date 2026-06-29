using SyberiaDatamine.Core;
using SyberiaDatamine.Imaging;
using System.IO;

namespace SyberiaDatamine.Loaders;

public class TgaFileLoader : IAssetLoader
{
	public string Id => "tga";
	public AssetKind Kind => AssetKind.Texture2D;
	public string[] Extensions => new[] { ".tga" };

	public bool CanHandle(string filePath)
	{
		return Path.GetExtension(filePath).Equals(".tga", StringComparison.OrdinalIgnoreCase);
	}

	public bool CanHandle(byte[] data, string fileName)
	{
		return Path.GetExtension(fileName).Equals(".tga", StringComparison.OrdinalIgnoreCase);
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{
		if (!File.Exists(filePath))
			return new AssetPreviewData { ErrorMessage = $"File not found: {filePath}" };

		string fileName = Path.GetFileName(filePath);
		return LoadFromData(File.ReadAllBytes(filePath), fileName);
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		try
		{
			var bitmap = TgaWpfAdapter.DecodeTgaFromData(data);
			return new AssetPreviewData
			{
				ImageData = bitmap,
				RawPreviewBytes = data,
				MetadataText = $"Texture: {bitmap.PixelWidth}x{bitmap.PixelHeight}\n" +
							  $"DPI: {bitmap.DpiX}x{bitmap.DpiY}\n" +
							  $"Format: {bitmap.Format}"
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to load TGA texture: {ex.Message}" };
		}
	}
}
