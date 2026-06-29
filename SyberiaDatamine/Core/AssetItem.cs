namespace SyberiaDatamine.Core;

public sealed class AssetItem
{
	public string Name { get; set; } = "";
	public string FullPath { get; set; } = "";
	public string Container { get; set; } = "";
	public AssetKind Kind { get; set; } = AssetKind.Unknown;
	public string LoaderId { get; set; } = "";
	public long FileSize { get; set; }
	public DateTime Modified { get; set; }

	public bool HideInAssetGrid { get; set; }

	public NemoObject? CmoObjectRef { get; set; }

	public string? CmoSourcePath { get; set; }

	public NemoFileInfo? CachedNemoInfo { get; set; }

	public string? LinkedAssetPath { get; set; }

	public string SizeFormatted => FormatSize(FileSize);

	public string TypeName => Kind switch
	{
		AssetKind.Texture2D => "Texture",
		AssetKind.Audio => "Audio",
		AssetKind.Video => "Video",
		AssetKind.Archive => "Archive",
		AssetKind.NemoFile => "NMO/CMO",
		AssetKind.CmoObject => CmoObjectRef?.GroupName ?? "CMO Object",
		AssetKind.Binary => "Binary",
		_ => "Unknown",
	};

	private static string FormatSize(long bytes)
	{
		if (bytes <= 0) return "—";
		string[] units = { "B", "KB", "MB", "GB" };
		double len = bytes;
		int i = 0;
		while (len >= 1024 && i < units.Length - 1) { len /= 1024; i++; }
		return $"{len:0.##} {units[i]}";
	}
}
