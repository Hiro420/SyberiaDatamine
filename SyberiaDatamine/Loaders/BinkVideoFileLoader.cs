using SyberiaDatamine.Core;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SyberiaDatamine.Loaders;

public sealed class BinkVideoFileLoader : IAssetLoader
{
	public string Id => "bink";
	public AssetKind Kind => AssetKind.Video;
	public string[] Extensions => new[] { ".bik", ".bk2", ".bik2" };

	public bool CanHandle(string filePath)
	{
		try
		{
			if (!File.Exists(filePath))
			{
				var missingExt = Path.GetExtension(filePath);
				return Array.Exists(Extensions, e => e.Equals(missingExt, StringComparison.OrdinalIgnoreCase));
			}

			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var header = new byte[Math.Min(64, (int)fs.Length)];
			fs.ReadExactly(header);
			return CanHandle(header, filePath);
		}
		catch
		{
			var ext = Path.GetExtension(filePath);
			return Array.Exists(Extensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
		}
	}

	public bool CanHandle(byte[] data, string fileName)
	{
		var ext = Path.GetExtension(fileName);
		if (Array.Exists(Extensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
			return true;

		if (data.Length < 4)
			return false;

		bool isBik = data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'K';
		bool isKb2 = data[0] == (byte)'K' && data[1] == (byte)'B' && data[2] == (byte)'2';
		return isBik || isKb2;
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{
		try
		{
			var header = ReadPrefixBytes(filePath, 128 * 1024);
			return LoadFromData(header, Path.GetFileName(filePath));
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to load Bink: {ex.Message}" };
		}
	}

	private static byte[] ReadPrefixBytes(string filePath, int maxBytes)
	{
		if (!File.Exists(filePath))
			return Array.Empty<byte>();

		using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		int len = (int)Math.Min(fs.Length, maxBytes);
		var buffer = new byte[len];
		int read = fs.Read(buffer, 0, len);
		if (read == len) return buffer;
		return buffer.Take(read).ToArray();
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		try
		{
			var sb = new StringBuilder();
			sb.AppendLine("Bink Video");

			if (data.Length >= 56 && data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'K')
			{
				var header = ParseClassicHeader(data);
				sb.AppendLine($"Signature: BIK{(char)header.VersionByte}");
				if (header.Width > 0 && header.Height > 0)
					sb.AppendLine($"Resolution: {header.Width}x{header.Height}");
				if (header.Fps > 0)
					sb.AppendLine($"FPS: {header.Fps:0.###}");
				sb.AppendLine($"Frames: {header.FrameCount}");
				if (header.AudioFlag != 0)
				{
					sb.AppendLine($"Audio: yes ({header.AudioChannels} ch, {header.AudioSampleRate} Hz)");
				}
				else
				{
					sb.AppendLine("Audio: no");
				}
			}
			else
			{
				var sig = data.Length >= 3 ? $"{(char)data[0]}{(char)data[1]}{(char)data[2]}" : "(short)";
				sb.AppendLine($"Signature: {sig}");
			}

			return new AssetPreviewData
			{
				MetadataText = sb.ToString().TrimEnd(),
				RawPreviewBytes = data.Length <= 64 * 1024 ? data : data.Take(64 * 1024).ToArray()
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData
			{
				ErrorMessage = $"Failed to parse Bink: {ex.Message}",
				RawPreviewBytes = data.Length <= 64 * 1024 ? data : data.Take(64 * 1024).ToArray()
			};
		}
	}

	private static ClassicBikHeader ParseClassicHeader(byte[] data)
	{

		byte version = data[3];

		uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
		uint width = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4));
		uint height = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24, 4));

		uint fpsRaw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28, 4));
		double fps = fpsRaw;
		if (fpsRaw > 1000)
		{
			fps = fpsRaw / 1000.0;
		}

		uint audioFlag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40, 4));
		ushort audioChannels = 0;
		ushort audioRate = 0;
		if (audioFlag != 0 && data.Length >= 50)
		{
			audioChannels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(44, 2));
			audioRate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(48, 2));
		}

		return new ClassicBikHeader
		{
			VersionByte = version,
			FrameCount = frameCount,
			Width = (int)width,
			Height = (int)height,
			Fps = fps,
			AudioFlag = audioFlag,
			AudioChannels = audioChannels,
			AudioSampleRate = audioRate
		};
	}

	private struct ClassicBikHeader
	{
		public byte VersionByte;
		public uint FrameCount;
		public int Width;
		public int Height;
		public double Fps;
		public uint AudioFlag;
		public ushort AudioChannels;
		public ushort AudioSampleRate;
	}
}
