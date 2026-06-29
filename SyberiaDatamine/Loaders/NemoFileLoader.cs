using SyberiaDatamine.Core;
using SyberiaDatamine.Parsers;
using System.IO;
using System.Text;

namespace SyberiaDatamine.Loaders;

public class NemoFileLoader : IAssetLoader
{
	public string Id => "nemo";
	public AssetKind Kind => AssetKind.NemoFile;
	public string[] Extensions => new[] { ".nmo", ".cmo" };

	public bool CanHandle(string filePath)
	{
		var ext = Path.GetExtension(filePath);
		if (!ext.Equals(".nmo", StringComparison.OrdinalIgnoreCase) &&
			!ext.Equals(".cmo", StringComparison.OrdinalIgnoreCase))
			return false;

		if (!File.Exists(filePath))
			return false;

		try
		{
			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var buf = new byte[4];
			return fs.Read(buf, 0, 4) == 4 && CheckMagic(buf);
		}
		catch { return false; }
	}

	public bool CanHandle(byte[] data, string fileName)
	{
		var ext = Path.GetExtension(fileName);
		if (!ext.Equals(".nmo", StringComparison.OrdinalIgnoreCase) &&
			!ext.Equals(".cmo", StringComparison.OrdinalIgnoreCase))
			return false;

		return data.Length >= 4 && CheckMagic(data);
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{

		var info = CkFileParser.TryParseHeaderOnly(filePath);

		if (info == null)
			return new AssetPreviewData { ErrorMessage = "Failed to parse NMO/CMO file." };

		if (info.ParseError != null)
			return new AssetPreviewData
			{
				ErrorMessage = $"Parse error: {info.ParseError}",
				NemoInfo = info,
			};

		var sb = new StringBuilder();
		sb.AppendLine($"Format    CKFile (NMO/CMO)");
		sb.AppendLine($"Version   {info.FileVersion}  |  {info.WriteModeStr}");
		sb.AppendLine($"Build     Virtools {info.ProductBuildStr}");
		sb.AppendLine($"CK Date   {info.CkVersionStr}");
		sb.AppendLine($"Objects   {info.ObjectCount}");
		sb.AppendLine($"Managers  {info.ManagerCount}");
		sb.AppendLine($"MaxID     {info.MaxIDSaved}");
		sb.AppendLine($"DataSize  {info.DataPackSize:N0} B packed → {info.DataUnPackSize:N0} B");

		return new AssetPreviewData
		{
			MetadataText = sb.ToString(),
			NemoInfo = info,

		};
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		var info = CkFileParser.TryParse(data);

		if (info == null)
			return new AssetPreviewData { ErrorMessage = "Failed to parse NMO/CMO file." };

		if (info.ParseError != null)
			return new AssetPreviewData
			{
				ErrorMessage = $"Parse error: {info.ParseError}",
				NemoInfo = info,
			};

		var sb = new StringBuilder();
		sb.AppendLine($"Format    CKFile (NMO/CMO)");
		sb.AppendLine($"Version   {info.FileVersion}  |  {info.WriteModeStr}");
		sb.AppendLine($"Build     Virtools {info.ProductBuildStr}");
		sb.AppendLine($"CK Date   {info.CkVersionStr}");
		sb.AppendLine($"Objects   {info.ObjectCount}");
		sb.AppendLine($"Managers  {info.ManagerCount}");
		sb.AppendLine($"MaxID     {info.MaxIDSaved}");
		sb.AppendLine($"DataSize  {info.DataPackSize:N0} B packed → {info.DataUnPackSize:N0} B");

		return new AssetPreviewData
		{
			MetadataText = sb.ToString(),
			NemoInfo = info,
			RawPreviewBytes = data.Length > 512 ? data[..512] : data,
		};
	}

	private static bool CheckMagic(byte[] buf) =>
		buf[0] == 'N' && buf[1] == 'e' && buf[2] == 'm' && buf[3] == 'o';
}
