using SyberiaDatamine.Core;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SyberiaDatamine.Loaders;

public sealed class WavAudioFileLoader : IAssetLoader
{
	public string Id => "wav";
	public AssetKind Kind => AssetKind.Audio;
	public string[] Extensions => new[] { ".wav" };

	public bool CanHandle(string filePath)
		=> Path.GetExtension(filePath).Equals(".wav", StringComparison.OrdinalIgnoreCase);

	public bool CanHandle(byte[] data, string fileName)
	{
		if (data.Length < 12)
			return false;

		return data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
			&& data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E';
	}

	public AssetPreviewData LoadFromFile(string filePath)
	{
		try
		{
			if (!File.Exists(filePath))
				return new AssetPreviewData { ErrorMessage = $"File not found: {filePath}" };

			var header = ReadPrefixBytes(filePath, 128 * 1024);
			var fileSize = new FileInfo(filePath).Length;
			return LoadFromHeader(header, fileSize, Path.GetFileName(filePath));
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to load WAV: {ex.Message}" };
		}
	}

	private AssetPreviewData LoadFromHeader(byte[] headerBytes, long fileSizeBytes, string fileName)
	{
		try
		{
			var (fmt, dataSize) = ParseWave(headerBytes);

			if (dataSize <= 0)
				dataSize = (int)Math.Max(0, fileSizeBytes - 44);

			var sb = new StringBuilder();
			sb.AppendLine("WAV Audio");
			sb.AppendLine($"Channels: {fmt.Channels}");
			sb.AppendLine($"Sample Rate: {fmt.SampleRate} Hz");
			sb.AppendLine($"Bits/Sample: {fmt.BitsPerSample}");
			sb.AppendLine($"Format: {(fmt.AudioFormat == 1 ? "PCM" : $"0x{fmt.AudioFormat:X}")}");

			if (fmt.ByteRate > 0 && dataSize > 0)
			{
				double seconds = dataSize / (double)fmt.ByteRate;
				sb.AppendLine($"Approx. Duration: {TimeSpan.FromSeconds(seconds):g}");
			}

			return new AssetPreviewData
			{
				MetadataText = sb.ToString().TrimEnd(),
				RawPreviewBytes = headerBytes
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to parse WAV: {ex.Message}", RawPreviewBytes = headerBytes };
		}
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		try
		{
			var (fmt, dataSize) = ParseWave(data);
			var sb = new StringBuilder();
			sb.AppendLine("WAV Audio");
			sb.AppendLine($"Channels: {fmt.Channels}");
			sb.AppendLine($"Sample Rate: {fmt.SampleRate} Hz");
			sb.AppendLine($"Bits/Sample: {fmt.BitsPerSample}");
			sb.AppendLine($"Format: {(fmt.AudioFormat == 1 ? "PCM" : $"0x{fmt.AudioFormat:X}")}");

			if (fmt.ByteRate > 0 && dataSize > 0)
			{
				double seconds = dataSize / (double)fmt.ByteRate;
				sb.AppendLine($"Approx. Duration: {TimeSpan.FromSeconds(seconds):g}");
			}

			return new AssetPreviewData
			{
				MetadataText = sb.ToString().TrimEnd(),
				RawPreviewBytes = data.Length <= 64 * 1024 ? data : data.Take(64 * 1024).ToArray()
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to parse WAV: {ex.Message}", RawPreviewBytes = data };
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

	private static (WaveFmt fmt, int dataChunkSize) ParseWave(byte[] bytes)
	{
		int offset = 12;

		WaveFmt fmt = default;
		bool haveFmt = false;
		int dataSize = 0;

		while (offset + 8 <= bytes.Length)
		{
			uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
			int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
			offset += 8;

			if (chunkSize < 0 || offset + chunkSize > bytes.Length)
				break;

			if (chunkId == 0x20746D66)
			{
				if (chunkSize < 16)
					throw new InvalidDataException("Invalid fmt chunk size.");

				var span = bytes.AsSpan(offset, chunkSize);
				fmt.AudioFormat = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
				fmt.Channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
				fmt.SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
				fmt.ByteRate = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
				fmt.BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12, 2));
				fmt.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2));
				haveFmt = true;
			}
			else if (chunkId == 0x61746164)
			{
				dataSize = chunkSize;
			}

			offset += chunkSize;
			if ((chunkSize & 1) == 1)
				offset++;
		}

		if (!haveFmt)
			throw new InvalidDataException("Missing fmt chunk.");

		return (fmt, dataSize);
	}

	private struct WaveFmt
	{
		public ushort AudioFormat;
		public ushort Channels;
		public uint SampleRate;
		public uint ByteRate;
		public ushort BlockAlign;
		public ushort BitsPerSample;
	}
}
