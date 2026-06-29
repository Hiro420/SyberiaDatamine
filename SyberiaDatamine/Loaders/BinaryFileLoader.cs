using SyberiaDatamine.Core;
using System.IO;

namespace SyberiaDatamine.Loaders;

public sealed class BinaryFileLoader : IAssetLoader
{
	public string Id => "binary";
	public AssetKind Kind => AssetKind.Binary;
	public string[] Extensions => Array.Empty<string>();

	public bool CanHandle(string filePath) => true;
	public bool CanHandle(byte[] data, string fileName) => true;

	public AssetPreviewData LoadFromFile(string filePath)
	{
		try
		{
			if (!File.Exists(filePath))
				return new AssetPreviewData { ErrorMessage = $"File not found: {filePath}" };

			var fileInfo = new FileInfo(filePath);
			var prefix = ReadPrefixBytes(filePath, 64 * 1024);
			return new AssetPreviewData
			{
				MetadataText = $"Binary file\nName: {fileInfo.Name}\nSize: {fileInfo.Length} bytes",
				RawPreviewBytes = prefix
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to read file: {ex.Message}" };
		}
	}

	private static byte[] ReadPrefixBytes(string filePath, int maxBytes)
	{
		using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		int len = (int)Math.Min(fs.Length, maxBytes);
		var buffer = new byte[len];
		int read = fs.Read(buffer, 0, len);
		if (read == len) return buffer;
		return buffer.Take(read).ToArray();
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		return new AssetPreviewData
		{
			MetadataText = $"Binary file\nName: {fileName}\nSize: {data.Length} bytes",
			RawPreviewBytes = data.Length <= 64 * 1024 ? data : data.Take(64 * 1024).ToArray()
		};
	}
}
