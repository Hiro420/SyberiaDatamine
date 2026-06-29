using System.Windows.Media.Imaging;

namespace SyberiaDatamine.Core;

public sealed class AssetPreviewData
{

	public BitmapSource? ImageData { get; set; }

	public string MetadataText { get; set; } = "";

	public string? ErrorMessage { get; set; }

	public byte[]? RawPreviewBytes { get; set; }

	public NemoFileInfo? NemoInfo { get; set; }

	public byte[]? GetHexPreview(int maxBytes = 512)
	{
		if (RawPreviewBytes is { Length: > 0 })
			return RawPreviewBytes.Length <= maxBytes
				? RawPreviewBytes
				: RawPreviewBytes.Take(maxBytes).ToArray();

		if (ImageData is null) return null;

		try
		{
			var enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(ImageData));
			using var ms = new System.IO.MemoryStream();
			enc.Save(ms);
			return ms.ToArray().Take(maxBytes).ToArray();
		}
		catch { return null; }
	}
}
