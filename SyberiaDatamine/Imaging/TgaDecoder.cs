using System.IO;

namespace SyberiaDatamine.Imaging;

public static class TgaDecoder
{
	public sealed class ImageRgba32
	{
		public int Width { get; }
		public int Height { get; }
		public byte[] Rgba { get; }

		public ImageRgba32(int width, int height, byte[] rgba)
		{
			Width = width;
			Height = height;
			Rgba = rgba;
		}
	}

	private readonly struct TgaHeader
	{
		public readonly byte IdLength;
		public readonly byte ColorMapType;
		public readonly byte ImageType;
		public readonly ushort ColorMapFirstEntryIndex;
		public readonly ushort ColorMapLength;
		public readonly byte ColorMapEntrySize;
		public readonly ushort XOrigin;
		public readonly ushort YOrigin;
		public readonly ushort Width;
		public readonly ushort Height;
		public readonly byte PixelDepth;
		public readonly byte ImageDescriptor;

		public TgaHeader(BinaryReader br)
		{
			IdLength = br.ReadByte();
			ColorMapType = br.ReadByte();
			ImageType = br.ReadByte();
			ColorMapFirstEntryIndex = br.ReadUInt16();
			ColorMapLength = br.ReadUInt16();
			ColorMapEntrySize = br.ReadByte();
			XOrigin = br.ReadUInt16();
			YOrigin = br.ReadUInt16();
			Width = br.ReadUInt16();
			Height = br.ReadUInt16();
			PixelDepth = br.ReadByte();
			ImageDescriptor = br.ReadByte();
		}
	}

	public static ImageRgba32 Decode(Stream stream)
	{
		using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

		var h = new TgaHeader(br);

		if (h.Width == 0 || h.Height == 0)
			throw new InvalidDataException("Invalid TGA dimensions.");
		if (h.ColorMapType != 0)
			throw new NotSupportedException("Color-mapped TGAs are not supported.");
		if (h.IdLength > 0)
			br.ReadBytes(h.IdLength);

		int width = h.Width;
		int height = h.Height;
		bool originTop = (h.ImageDescriptor & 0x20) != 0;
		var rgba = new byte[width * height * 4];

		bool isColorMapped = h.ImageType == 1 || h.ImageType == 9;

		if (isColorMapped)
		{
			if (h.ColorMapType != 1)
				throw new InvalidDataException("Color-mapped TGA is missing a color map.");
		}
		else
		{
			if (h.ColorMapType != 0)
				throw new NotSupportedException("Unexpected color map for non-color-mapped TGA.");
		}

		if (h.IdLength > 0)
			br.ReadBytes(h.IdLength);

		Rgba[]? colorMap = null;
		if (isColorMapped)
		{
			colorMap = ReadColorMap(
				br,
				h.ColorMapFirstEntryIndex,
				h.ColorMapLength,
				h.ColorMapEntrySize
			);
		}

		switch (h.ImageType)
		{
			case 1: DecodeUncompressedColorMapped(br, width, height, h.PixelDepth, originTop, colorMap!, rgba); break;
			case 2: DecodeUncompressedTruecolor(br, width, height, h.PixelDepth, originTop, rgba); break;
			case 3: DecodeUncompressedGrayscale(br, width, height, h.PixelDepth, originTop, rgba); break;
			case 9: DecodeRleColorMapped(br, width, height, h.PixelDepth, originTop, colorMap!, rgba); break;
			case 10: DecodeRleTruecolor(br, width, height, h.PixelDepth, originTop, rgba); break;
			case 11: DecodeRleGrayscale(br, width, height, h.PixelDepth, originTop, rgba); break;
			default: throw new NotSupportedException($"Unsupported TGA image type: {h.ImageType}");
		}

		return new ImageRgba32(width, height, rgba);
	}

	private static void DecodeUncompressedTruecolor(BinaryReader br, int w, int h, byte bpp, bool top, byte[] dst)
	{
		if (bpp != 24 && bpp != 32)
			throw new NotSupportedException($"Unsupported truecolor bpp: {bpp}");

		for (int i = 0; i < w * h; i++)
		{
			byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte();
			byte a = (bpp == 32) ? br.ReadByte() : (byte)255;
			WriteColor(dst, w, h, i, top, r, g, b, a);
		}
	}

	private static void DecodeUncompressedGrayscale(BinaryReader br, int w, int h, byte bpp, bool top, byte[] dst)
	{
		if (bpp != 8 && bpp != 16)
			throw new NotSupportedException($"Unsupported grayscale bpp: {bpp}");

		for (int i = 0; i < w * h; i++)
		{
			byte intensity = br.ReadByte();
			byte alpha = (bpp == 16) ? br.ReadByte() : (byte)255;
			WriteGray(dst, w, h, i, top, intensity, alpha);
		}
	}

	private static void DecodeRleTruecolor(BinaryReader br, int w, int h, byte bpp, bool top, byte[] dst)
	{
		if (bpp != 24 && bpp != 32)
			throw new NotSupportedException($"Unsupported truecolor bpp: {bpp}");

		int total = w * h, idx = 0;
		while (idx < total)
		{
			byte hdr = br.ReadByte();
			int run = (hdr & 0x7F) + 1;
			if ((hdr & 0x80) != 0)
			{
				byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte();
				byte a = (bpp == 32) ? br.ReadByte() : (byte)255;
				for (int j = 0; j < run && idx < total; j++, idx++)
					WriteColor(dst, w, h, idx, top, r, g, b, a);
			}
			else
			{
				for (int j = 0; j < run && idx < total; j++, idx++)
				{
					byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte();
					byte a = (bpp == 32) ? br.ReadByte() : (byte)255;
					WriteColor(dst, w, h, idx, top, r, g, b, a);
				}
			}
		}
	}

	private static void DecodeRleGrayscale(BinaryReader br, int w, int h, byte bpp, bool top, byte[] dst)
	{
		if (bpp != 8 && bpp != 16)
			throw new NotSupportedException($"Unsupported grayscale bpp: {bpp}");

		int total = w * h, idx = 0;
		while (idx < total)
		{
			byte hdr = br.ReadByte();
			int run = (hdr & 0x7F) + 1;
			if ((hdr & 0x80) != 0)
			{
				byte intensity = br.ReadByte();
				byte alpha = (bpp == 16) ? br.ReadByte() : (byte)255;
				for (int j = 0; j < run && idx < total; j++, idx++)
					WriteGray(dst, w, h, idx, top, intensity, alpha);
			}
			else
			{
				for (int j = 0; j < run && idx < total; j++, idx++)
				{
					byte intensity = br.ReadByte();
					byte alpha = (bpp == 16) ? br.ReadByte() : (byte)255;
					WriteGray(dst, w, h, idx, top, intensity, alpha);
				}
			}
		}
	}

	private static Rgba[] ReadColorMap(
		BinaryReader br,
		ushort firstEntryIndex,
		ushort colorMapLength,
		byte entrySizeBits)
	{
		if (colorMapLength == 0)
			throw new InvalidDataException("Color map length is zero.");

		int totalEntries = firstEntryIndex + colorMapLength;
		var colorMap = new Rgba[totalEntries];

		for (int i = 0; i < colorMapLength; i++)
		{
			int dstIndex = firstEntryIndex + i;
			colorMap[dstIndex] = ReadColorMapEntry(br, entrySizeBits);
		}

		return colorMap;
	}

	private static Rgba ReadColorMapEntry(BinaryReader br, byte entrySizeBits)
	{
		switch (entrySizeBits)
		{
			case 15:
			case 16:
				{
					ushort v = br.ReadUInt16();

					byte b5 = (byte)(v & 0x1F);
					byte g5 = (byte)((v >> 5) & 0x1F);
					byte r5 = (byte)((v >> 10) & 0x1F);

					byte r = Expand5To8(r5);
					byte g = Expand5To8(g5);
					byte b = Expand5To8(b5);

					byte a = entrySizeBits == 16
						? (((v & 0x8000) != 0) ? (byte)255 : (byte)0)
						: (byte)255;

					return new Rgba(r, g, b, a);
				}

			case 24:
				{
					byte b = br.ReadByte();
					byte g = br.ReadByte();
					byte r = br.ReadByte();
					return new Rgba(r, g, b, 255);
				}

			case 32:
				{
					byte b = br.ReadByte();
					byte g = br.ReadByte();
					byte r = br.ReadByte();
					byte a = br.ReadByte();
					return new Rgba(r, g, b, a);
				}

			default:
				throw new NotSupportedException($"Unsupported color map entry size: {entrySizeBits}");
		}
	}

	private static void DecodeUncompressedColorMapped(
		BinaryReader br,
		int w,
		int h,
		byte indexBpp,
		bool top,
		Rgba[] colorMap,
		byte[] dst)
	{
		if (indexBpp != 8 && indexBpp != 16)
			throw new NotSupportedException($"Unsupported color-mapped index bpp: {indexBpp}");

		for (int i = 0; i < w * h; i++)
		{
			int colorIndex = ReadColorMapIndex(br, indexBpp);
			Rgba c = LookupColor(colorMap, colorIndex);
			WriteColor(dst, w, h, i, top, c.R, c.G, c.B, c.A);
		}
	}

	private static void DecodeRleColorMapped(
		BinaryReader br,
		int w,
		int h,
		byte indexBpp,
		bool top,
		Rgba[] colorMap,
		byte[] dst)
	{
		if (indexBpp != 8 && indexBpp != 16)
			throw new NotSupportedException($"Unsupported color-mapped index bpp: {indexBpp}");

		int total = w * h;
		int idx = 0;

		while (idx < total)
		{
			byte hdr = br.ReadByte();
			int run = (hdr & 0x7F) + 1;

			if ((hdr & 0x80) != 0)
			{
				int colorIndex = ReadColorMapIndex(br, indexBpp);
				Rgba c = LookupColor(colorMap, colorIndex);

				for (int j = 0; j < run && idx < total; j++, idx++)
					WriteColor(dst, w, h, idx, top, c.R, c.G, c.B, c.A);
			}
			else
			{
				for (int j = 0; j < run && idx < total; j++, idx++)
				{
					int colorIndex = ReadColorMapIndex(br, indexBpp);
					Rgba c = LookupColor(colorMap, colorIndex);
					WriteColor(dst, w, h, idx, top, c.R, c.G, c.B, c.A);
				}
			}
		}
	}

	private static int ReadColorMapIndex(BinaryReader br, byte indexBpp)
	{
		return indexBpp switch
		{
			8 => br.ReadByte(),
			16 => br.ReadUInt16(),
			_ => throw new NotSupportedException($"Unsupported color-mapped index bpp: {indexBpp}")
		};
	}

	private static Rgba LookupColor(Rgba[] colorMap, int index)
	{
		if ((uint)index >= (uint)colorMap.Length)
			throw new InvalidDataException($"Color map index out of range: {index}");

		return colorMap[index];
	}

	private static byte Expand5To8(byte v)
	{
		return (byte)((v << 3) | (v >> 2));
	}

	private static void WriteColor(byte[] dst, int w, int h, int linear, bool top, byte r, byte g, byte b, byte a)
	{
		int x = linear % w;
		int y = top ? (linear / w) : (h - 1 - linear / w);
		int i = (y * w + x) * 4;
		dst[i] = r; dst[i + 1] = g; dst[i + 2] = b; dst[i + 3] = a;
	}

	private static void WriteGray(byte[] dst, int w, int h, int linear, bool top, byte intensity, byte alpha)
	{
		int x = linear % w;
		int y = top ? (linear / w) : (h - 1 - linear / w);
		int i = (y * w + x) * 4;
		dst[i] = dst[i + 1] = dst[i + 2] = intensity; dst[i + 3] = alpha;
	}

	private readonly struct Rgba
	{
		public readonly byte R, G, B, A;

		public Rgba(byte r, byte g, byte b, byte a)
		{
			R = r;
			G = g;
			B = b;
			A = a;
		}
	}
}
