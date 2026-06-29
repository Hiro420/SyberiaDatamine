using SyberiaDatamine.Core;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SyberiaDatamine.Parsers;

public static class CkFileParser
{
	public static NemoFileInfo? TryParse(byte[] data)
	{
		try
		{
			using var ms = new MemoryStream(data, writable: false);
			return ParseInternal(ms);
		}
		catch (Exception ex) { return new NemoFileInfo { ParseError = ex.Message }; }
	}

	public static NemoFileInfo? TryParseHeaderOnly(string filePath)
	{
		try
		{
			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			return ParseInternal(fs);
		}
		catch (Exception ex) { return new NemoFileInfo { ParseError = ex.Message }; }
	}

	private static NemoFileInfo ParseInternal(Stream stream)
	{
		using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

		var sig = br.ReadBytes(8);
		if (sig.Length < 4 || sig[0] != 'N' || sig[1] != 'e' || sig[2] != 'm' || sig[3] != 'o')
			throw new InvalidDataException("Missing Nemo magic — not a NMO/CMO file.");

		br.ReadUInt32();
		var ckVersion = br.ReadUInt32();
		var fileVersion = br.ReadInt32();
		var fileVersion2 = br.ReadInt32();
		var fileWriteMode = br.ReadUInt32();
		var hdr1PackSize = br.ReadInt32();

		if (fileVersion2 != 0) fileVersion = 0;

		var dataPackSize = br.ReadInt32();
		var dataUnPackSize = br.ReadInt32();
		var managerCount = br.ReadInt32();
		var objectCount = br.ReadInt32();
		var maxIDSaved = br.ReadUInt32();
		var productVersion = br.ReadUInt32();
		var productBuild = br.ReadUInt32();
		var hdr1UnPackSize = br.ReadInt32();

		var info = new NemoFileInfo
		{
			CkVersion = ckVersion,
			FileVersion = fileVersion,
			FileWriteMode = fileWriteMode,
			ProductVersion = productVersion,
			ProductBuild = productBuild,
			ObjectCount = objectCount,
			ManagerCount = managerCount,
			MaxIDSaved = maxIDSaved,
			DataPackSize = dataPackSize,
			DataUnPackSize = dataUnPackSize,
			Hdr1PackSize = hdr1PackSize,
			Hdr1UnPackSize = hdr1UnPackSize,
		};

		if (fileVersion < 7) return info;

		if (hdr1PackSize <= 0 || hdr1PackSize > 64 * 1024 * 1024)
			return info;

		long available = stream.CanSeek
			? stream.Length - stream.Position
			: (long)hdr1PackSize;
		var readSize = (int)Math.Min(hdr1PackSize, available);
		if (readSize <= 0) return info;

		var hdrCompressed = br.ReadBytes(readSize);

		var safeUnpackSize = (hdr1UnPackSize > 0 && hdr1UnPackSize <= 128 * 1024 * 1024)
			? hdr1UnPackSize : 4096;
		var hdrData = (hdr1PackSize != hdr1UnPackSize && hdr1UnPackSize > 0)
			? ZlibDecompress(hdrCompressed, safeUnpackSize)
			: hdrCompressed;

		using var hms = new MemoryStream(hdrData, writable: false);
		using var hbr = new BinaryReader(hms, Encoding.ASCII, leaveOpen: false);

		var safeObjCount = Math.Min(objectCount, 100_000);
		for (int i = 0; i < safeObjCount; i++)
		{
			if (hms.Position + 12 > hms.Length) break;

			var ckId = (uint)hbr.ReadInt32();
			var classId = hbr.ReadInt32();
			var fileIndex = hbr.ReadInt32();
			var name = ReadCkString(hbr) ?? "";

			info.Objects.Add(new NemoObject
			{
				ObjectIndex = i,
				CkId = ckId,
				ClassId = classId,
				FileIndex = fileIndex,
				Name = name,
			});
		}

		return info;
	}

	private static string? ReadCkString(BinaryReader br)
	{
		if (br.BaseStream.Position + 4 > br.BaseStream.Length) return null;
		var len = br.ReadInt32();
		if (len <= 0 || br.BaseStream.Position + len > br.BaseStream.Length) return null;
		return Encoding.ASCII.GetString(br.ReadBytes(len));
	}

	internal static byte[] ZlibDecompress(byte[] data, int expectedSize)
	{
		if (data.Length < 2) return data;

		int offset = (data[0] == 0x78) ? 2 : 0;
		int initCapacity = (expectedSize > 0 && expectedSize <= 128 * 1024 * 1024) ? expectedSize : 4096;

		try
		{
			using var inp = new MemoryStream(data, offset, data.Length - offset);
			using var def = new DeflateStream(inp, CompressionMode.Decompress);
			using var out_ = new MemoryStream(initCapacity);
			def.CopyTo(out_);
			return out_.ToArray();
		}
		catch (InvalidDataException) when (offset > 0)
		{
			using var inp = new MemoryStream(data);
			using var def = new DeflateStream(inp, CompressionMode.Decompress);
			using var out_ = new MemoryStream(initCapacity);
			def.CopyTo(out_);
			return out_.ToArray();
		}
	}
}
