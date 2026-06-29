using SyberiaDatamine.Core;
using System.IO;
using System.Windows.Media.Imaging;

namespace SyberiaDatamine.Loaders;

public class ImageFileLoader : IAssetLoader
{
	public string Id => "image";
	public AssetKind Kind => AssetKind.Texture2D;
	public string[] Extensions => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp" };

	public bool CanHandle(string filePath)
	{
		var ext = Path.GetExtension(filePath).ToLowerInvariant();
		return Array.IndexOf(Extensions, ext) >= 0;
	}

	public bool CanHandle(byte[] data, string fileName)
	{
		var ext = Path.GetExtension(fileName).ToLowerInvariant();
		return Array.IndexOf(Extensions, ext) >= 0;
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{
		try
		{
			var bitmap = new BitmapImage();
			bitmap.BeginInit();
			bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
			bitmap.CacheOption = BitmapCacheOption.OnLoad;
			bitmap.EndInit();
			bitmap.Freeze();

			return new AssetPreviewData
			{
				ImageData = bitmap,
				MetadataText = $"Texture: {bitmap.PixelWidth}x{bitmap.PixelHeight}\n" +
							  $"DPI: {bitmap.DpiX}x{bitmap.DpiY}\n" +
							  $"Format: {bitmap.Format}"
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to load image: {ex.Message}" };
		}
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		try
		{
			var bitmap = new BitmapImage();
			bitmap.BeginInit();
			bitmap.StreamSource = new MemoryStream(data);
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
			return new AssetPreviewData { ErrorMessage = $"Failed to load image: {ex.Message}" };
		}
	}
}
