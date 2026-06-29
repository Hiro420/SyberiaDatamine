using SyberiaDatamine.Core;
using SyberiaDatamine.Parsers;
using System.IO;

namespace SyberiaDatamine.Loaders;

public class VxbgFileLoader : IAssetLoader
{
	public string Id => "vxbg";
	public AssetKind Kind => AssetKind.Archive;

	public string[] Extensions => new[] { ".syb", ".sl", ".vxbg" };

	public bool CanHandle(string filePath)
	{
		if (!File.Exists(filePath))
			return false;

		try
		{
			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var header = new byte[4];
			if (fs.Read(header, 0, 4) == 4)
			{
				return header[0] == 0x56 && header[1] == 0x58 &&
					   header[2] == 0x42 && header[3] == 0x47;
			}
		}
		catch { }
		return false;
	}

	public bool CanHandle(byte[] data, string fileName)
	{
		if (data.Length < 4)
			return false;
		return data[0] == 0x56 && data[1] == 0x58 && data[2] == 0x42 && data[3] == 0x47;
	}

	private static AssetPreviewData LoadMetadata(IArchiveSource src)
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"VXBG Archive — {src.ArchiveName}  ({src.Entries.Count} files)\n");
		foreach (var e in src.Entries)
			sb.AppendLine($"{e.Name,-40} {FormatSize(e.FileSize)}");
		return new AssetPreviewData { MetadataText = sb.ToString() };
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{
		try
		{
			using var src = new VxbgArchiveSource(filePath);
			return LoadMetadata(src);
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = ex.Message };
		}
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{

		try
		{
			var tmp = Path.GetTempFileName();
			File.WriteAllBytes(tmp, data);
			try
			{
				using var src = new VxbgArchiveSource(tmp);
				return LoadMetadata(src);
			}
			finally { File.Delete(tmp); }
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = ex.Message };
		}
	}

	private static string FormatSize(long bytes)
	{
		string[] sizes = { "B", "KB", "MB", "GB" };
		double len = bytes;
		int order = 0;
		while (len >= 1024 && order < sizes.Length - 1)
		{
			order++;
			len /= 1024;
		}
		return $"{len:0.##} {sizes[order]}";
	}
}
