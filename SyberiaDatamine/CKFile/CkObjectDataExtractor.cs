using SyberiaDatamine.Core;
using SyberiaDatamine.Parsers;
using System.IO;

namespace SyberiaDatamine.CKFile;

public sealed class CkObjectDataExtractor
{
	private readonly string _filePath;
	private readonly NemoFileInfo _info;
	private readonly List<NemoObject> _objectsByOffset;
	private byte[]? _unpackedData;
	private bool _attemptedDataLoad;

	public CkObjectDataExtractor(string filePath, NemoFileInfo info)
	{
		_filePath = filePath;
		_info = info;
		_objectsByOffset = info.Objects
			.Where(o => o.FileIndex >= 0)
			.OrderBy(o => o.FileIndex)
			.ToList();
	}

	private int LogicalToDataOffset(int logicalOffset)
	{
		int offsetBase = 0x40 + Math.Max(0, _info.Hdr1UnPackSize);
		return logicalOffset - offsetBase;
	}

	public long EstimateObjectSize(NemoObject obj)
	{
		if (obj.FileIndex < 0)
			return 0;

		int idx = _objectsByOffset.FindIndex(o => o.CkId == obj.CkId && o.FileIndex == obj.FileIndex);
		if (idx < 0)
			return 0;

		int start = LogicalToDataOffset(obj.FileIndex);
		int end = (idx + 1 < _objectsByOffset.Count)
			? LogicalToDataOffset(_objectsByOffset[idx + 1].FileIndex)
			: Math.Max(_info.DataUnPackSize, _info.DataPackSize);

		if (end <= start)
			return 0;

		return end - start;
	}

	public byte[]? TryGetRawObjectBytes(NemoObject obj)
	{
		if (obj.FileIndex < 0)
			return null;

		var data = EnsureUnpackedData();
		if (data == null || data.Length == 0)
			return null;

		int idx = _objectsByOffset.FindIndex(o => o.CkId == obj.CkId && o.FileIndex == obj.FileIndex);
		if (idx < 0)
			return null;

		int start = LogicalToDataOffset(obj.FileIndex);
		int end = (idx + 1 < _objectsByOffset.Count)
			? LogicalToDataOffset(_objectsByOffset[idx + 1].FileIndex)
			: data.Length;

		if (start < 0 || end <= start || start >= data.Length)
			return null;

		end = Math.Min(end, data.Length);
		int len = end - start;
		if (len <= 0)
			return null;

		var slice = new byte[len];
		Buffer.BlockCopy(data, start, slice, 0, len);
		return slice;
	}

	private byte[]? EnsureUnpackedData()
	{
		if (_attemptedDataLoad)
			return _unpackedData;

		_attemptedDataLoad = true;

		try
		{
			using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

			long dataStart = 64L + Math.Max(0, _info.Hdr1PackSize);
			if (fs.Length <= dataStart || _info.DataPackSize <= 0)
				return null;

			fs.Seek(dataStart, SeekOrigin.Begin);
			int readSize = (int)Math.Min(_info.DataPackSize, fs.Length - fs.Position);
			if (readSize <= 0)
				return null;

			var packed = new byte[readSize];
			int read = 0;
			while (read < packed.Length)
			{
				int n = fs.Read(packed, read, packed.Length - read);
				if (n == 0) break;
				read += n;
			}

			if (read <= 0)
				return null;

			if (read != packed.Length)
				Array.Resize(ref packed, read);

			if (_info.DataUnPackSize > 0 && _info.DataPackSize != _info.DataUnPackSize)
			{

				_unpackedData = CkFileParser.ZlibDecompress(packed, _info.DataUnPackSize);
			}
			else
			{
				_unpackedData = packed;
			}
		}
		catch
		{
			_unpackedData = null;
		}

		return _unpackedData;
	}
}
