using SyberiaDatamine.Core;
using System.IO;
using System.Windows.Media.Imaging;

namespace SyberiaDatamine.Loaders;

public class SyjFileLoader : IAssetLoader
{
	public string Id => "syj";
	public AssetKind Kind => AssetKind.Texture2D;
	private static readonly byte[] JpgHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

	public string[] Extensions => new[] { ".syj" };

	public bool CanHandle(string filePath)
	{
		return Path.GetExtension(filePath).Equals(".syj", StringComparison.OrdinalIgnoreCase);
	}

	public bool CanHandle(byte[] data, string fileName)
	{
		return Path.GetExtension(fileName).Equals(".syj", StringComparison.OrdinalIgnoreCase);
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{
		try
		{
			if (!File.Exists(filePath))
				return new AssetPreviewData { ErrorMessage = $"File not found: {filePath}" };

			var data = File.ReadAllBytes(filePath);
			return LoadFromData(data, Path.GetFileName(filePath));
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to load SYJ: {ex.Message}" };
		}
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		try
		{
			var stream = new MemoryStream();
			stream.Write(JpgHeader, 0, JpgHeader.Length);
			stream.Write(data, 0, data.Length);
			stream.Seek(0, SeekOrigin.Begin);

			var bitmap = new BitmapImage();
			bitmap.BeginInit();
			bitmap.StreamSource = stream;
			bitmap.CacheOption = BitmapCacheOption.OnLoad;
			bitmap.EndInit();
			bitmap.Freeze();

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
			return new AssetPreviewData { ErrorMessage = $"Failed to load SYJ texture: {ex.Message}" };
		}
	}
}
