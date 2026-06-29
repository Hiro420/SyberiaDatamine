using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static SyberiaDatamine.Imaging.TgaDecoder;

namespace SyberiaDatamine.Imaging;

public static class TgaWpfAdapter
{
	public static BitmapSource DecodeTgaFromData(byte[] tgaData)
	{
		if (tgaData == null) throw new ArgumentNullException(nameof(tgaData));
		using var ms = new MemoryStream(tgaData, writable: false);
		return DecodeTgaFromData(ms);
	}

	public static BitmapSource DecodeTgaFromData(Stream tgaStream)
	{
		if (tgaStream == null) throw new ArgumentNullException(nameof(tgaStream));
		if (!tgaStream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(tgaStream));

		ImageRgba32 img = TgaDecoder.Decode(tgaStream);
		int stride = img.Width * 4;
		var bgra = new byte[stride * img.Height];
		var rgba = img.Rgba;

		for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 4)
		{
			bgra[j + 0] = rgba[i + 2];
			bgra[j + 1] = rgba[i + 1];
			bgra[j + 2] = rgba[i + 0];
			bgra[j + 3] = rgba[i + 3];
		}

		var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96,
			PixelFormats.Bgra32, null, bgra, stride);
		if (bmp.CanFreeze) bmp.Freeze();
		return bmp;
	}
}
