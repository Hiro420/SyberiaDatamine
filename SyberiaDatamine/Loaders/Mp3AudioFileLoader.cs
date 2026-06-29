using SyberiaDatamine.Core;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SyberiaDatamine.Loaders;

public sealed class Mp3AudioFileLoader : IAssetLoader
{
	public string Id => "mp3";
	public AssetKind Kind => AssetKind.Audio;
	public string[] Extensions => new[] { ".mp3" };

	public bool CanHandle(string filePath)
		=> Path.GetExtension(filePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase);

	public bool CanHandle(byte[] data, string fileName)
	{
		if (Path.GetExtension(fileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
			return true;

		if (data.Length < 4)
			return false;

		if (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
			return true;

		return data[0] == 0xFF && (data[1] & 0xE0) == 0xE0;
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
			return new AssetPreviewData { ErrorMessage = $"Failed to load MP3: {ex.Message}" };
		}
	}

	private AssetPreviewData LoadFromHeader(byte[] headerBytes, long fileSizeBytes, string fileName)
	{
		try
		{
			var info = ParseMp3(headerBytes, fileSizeBytes);

			var sb = new StringBuilder();
			sb.AppendLine("MP3 Audio");
			if (info.HasId3)
				sb.AppendLine("ID3: present");
			if (info.BitrateKbps > 0)
				sb.AppendLine($"Bitrate: {info.BitrateKbps} kbps{(info.IsVbr ? " (VBR?)" : "")}");
			if (info.SampleRateHz > 0)
				sb.AppendLine($"Sample Rate: {info.SampleRateHz} Hz");
			if (info.Channels > 0)
				sb.AppendLine($"Channels: {(info.Channels == 1 ? "Mono" : "Stereo")}");
			if (info.DurationSeconds > 0)
				sb.AppendLine($"Approx. Duration: {TimeSpan.FromSeconds(info.DurationSeconds):g}");

			return new AssetPreviewData
			{
				MetadataText = sb.ToString().TrimEnd(),
				RawPreviewBytes = headerBytes
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to parse MP3: {ex.Message}", RawPreviewBytes = headerBytes };
		}
	}

	public AssetPreviewData LoadFromData(byte[] data, string fileName)
	{
		try
		{
			var info = ParseMp3(data, data.LongLength);

			var sb = new StringBuilder();
			sb.AppendLine("MP3 Audio");
			if (info.HasId3)
				sb.AppendLine("ID3: present");
			if (info.BitrateKbps > 0)
				sb.AppendLine($"Bitrate: {info.BitrateKbps} kbps{(info.IsVbr ? " (VBR?)" : "")}");
			if (info.SampleRateHz > 0)
				sb.AppendLine($"Sample Rate: {info.SampleRateHz} Hz");
			if (info.Channels > 0)
				sb.AppendLine($"Channels: {(info.Channels == 1 ? "Mono" : "Stereo")}");
			if (info.DurationSeconds > 0)
				sb.AppendLine($"Approx. Duration: {TimeSpan.FromSeconds(info.DurationSeconds):g}");

			return new AssetPreviewData
			{
				MetadataText = sb.ToString().TrimEnd(),
				RawPreviewBytes = data.Length <= 64 * 1024 ? data : data.Take(64 * 1024).ToArray()
			};
		}
		catch (Exception ex)
		{
			return new AssetPreviewData { ErrorMessage = $"Failed to parse MP3: {ex.Message}", RawPreviewBytes = data };
		}
	}

	private static Mp3Info ParseMp3(byte[] bytes, long fileSizeBytes)
	{
		int offset = 0;
		bool hasId3 = false;

		if (bytes.Length >= 10 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3')
		{
			hasId3 = true;
			int tagSize = SynchSafeToInt(bytes.AsSpan(6, 4));
			offset = 10 + tagSize;
			if (offset > bytes.Length)
				offset = 10;
		}

		int headerOffset = FindMpegFrameHeader(bytes, offset);
		if (headerOffset < 0)
			throw new InvalidDataException("No MPEG frame header found.");

		uint header = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(headerOffset, 4));
		var frame = ParseFrameHeader(header);

		double durationSec = 0;
		if (frame.BitrateKbps > 0)
			durationSec = (fileSizeBytes * 8.0) / (frame.BitrateKbps * 1000.0);

		return new Mp3Info
		{
			HasId3 = hasId3,
			BitrateKbps = frame.BitrateKbps,
			SampleRateHz = frame.SampleRateHz,
			Channels = frame.Channels,
			IsVbr = frame.IsVbrHint,
			DurationSeconds = durationSec
		};
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

	private static int FindMpegFrameHeader(byte[] bytes, int start)
	{
		for (int i = Math.Max(0, start); i + 4 <= bytes.Length; i++)
		{
			if (bytes[i] == 0xFF && (bytes[i + 1] & 0xE0) == 0xE0)
			{

				byte b2 = bytes[i + 1];
				byte b3 = bytes[i + 2];
				int versionId = (b2 >> 3) & 0x03;
				int layer = (b2 >> 1) & 0x03;
				int sampleRateIndex = (b3 >> 2) & 0x03;
				if (versionId != 1 && layer != 0 && sampleRateIndex != 3)
					return i;
			}
		}
		return -1;
	}

	private static int SynchSafeToInt(ReadOnlySpan<byte> synchsafe4)
	{
		return ((synchsafe4[0] & 0x7F) << 21)
			 | ((synchsafe4[1] & 0x7F) << 14)
			 | ((synchsafe4[2] & 0x7F) << 7)
			 | (synchsafe4[3] & 0x7F);
	}

	private static FrameInfo ParseFrameHeader(uint header)
	{
		int versionId = (int)((header >> 19) & 0x3);
		int layerId = (int)((header >> 17) & 0x3);
		int bitrateIndex = (int)((header >> 12) & 0xF);
		int sampleRateIndex = (int)((header >> 10) & 0x3);
		int channelMode = (int)((header >> 6) & 0x3);

		bool isMono = channelMode == 3;

		int sampleRate = GetSampleRate(versionId, sampleRateIndex);
		int bitrate = GetBitrateKbps(versionId, layerId, bitrateIndex);

		return new FrameInfo
		{
			BitrateKbps = bitrate,
			SampleRateHz = sampleRate,
			Channels = isMono ? 1 : 2,
			IsVbrHint = bitrateIndex == 0
		};
	}

	private static int GetSampleRate(int versionId, int sampleRateIndex)
	{
		if (sampleRateIndex == 3)
			return 0;

		return versionId switch
		{
			3 => sampleRateIndex switch { 0 => 44100, 1 => 48000, 2 => 32000, _ => 0 },
			2 => sampleRateIndex switch { 0 => 22050, 1 => 24000, 2 => 16000, _ => 0 },
			0 => sampleRateIndex switch { 0 => 11025, 1 => 12000, 2 => 8000, _ => 0 },
			_ => 0
		};
	}

	private static int GetBitrateKbps(int versionId, int layerId, int bitrateIndex)
	{
		if (bitrateIndex == 0 || bitrateIndex == 15)
			return 0;

		bool isMpeg1 = versionId == 3;
		bool isLayer3 = layerId == 1;
		if (!isLayer3)
			return 0;

		int[] tableMpeg1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
		int[] tableMpeg2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

		return isMpeg1 ? tableMpeg1L3[bitrateIndex] : tableMpeg2L3[bitrateIndex];
	}

	private struct FrameInfo
	{
		public int BitrateKbps;
		public int SampleRateHz;
		public int Channels;
		public bool IsVbrHint;
	}

	private struct Mp3Info
	{
		public bool HasId3;
		public int BitrateKbps;
		public int SampleRateHz;
		public int Channels;
		public bool IsVbr;
		public double DurationSeconds;
	}
}
