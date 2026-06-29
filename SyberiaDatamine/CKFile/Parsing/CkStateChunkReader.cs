using System.Text;

namespace SyberiaDatamine.CKFile.Parsing;

public readonly record struct CkStateIdentifier(uint Id, int Offset, int Next, int PayloadDwordCount);

public sealed class CkStateChunkReader
{
	private readonly int[] _dwords;
	private int _cursor;

	public int DataVersion { get; }
	public int ClassId { get; }
	public int ChunkVersion { get; }
	public int ChunkOptions { get; }

	public const uint CK_STATESAVE_MESHFLAGS = 0x00002000;
	public const uint CK_STATESAVE_MESHCHANNELS = 0x00004000;
	public const uint CK_STATESAVE_MESHFACECHANMASK = 0x00008000;
	public const uint CK_STATESAVE_MESHFACES = 0x00010000;
	public const uint CK_STATESAVE_MESHVERTICES = 0x00020000;
	public const uint CK_STATESAVE_MESHLINES = 0x00040000;
	public const uint CK_STATESAVE_MESHWEIGHTS = 0x00080000;
	public const uint CK_STATESAVE_MESHMATERIALS = 0x00100000;

	public const uint CK_STATESAVE_DATAARRAYFORMAT = 0x00001000;
	public const uint CK_STATESAVE_DATAARRAYDATA = 0x00002000;

	public static CkStateChunkReader? TryParse(byte[] raw)
	{

		for (int hdrOff = 4; hdrOff <= raw.Length - 8; hdrOff += 4)
		{

			uint hdr = BitConverter.ToUInt32(raw, hdrOff);
			int rawChunkVer = (int)(hdr >> 16);
			int chunkVersion = rawChunkVer & 0xFF;
			if (chunkVersion < 2) continue;

			int chunkSize = BitConverter.ToInt32(raw, hdrOff + 4);
			if (chunkSize < 0 || chunkSize > 4 * 1024 * 1024) continue;

			int szPrefix = BitConverter.ToInt32(raw, hdrOff - 4);
			int minPrefix = 8 + chunkSize * 4;
			int bytesAvailableFromPrefix = raw.Length - (hdrOff - 4);
			if (szPrefix < minPrefix || szPrefix > bytesAvailableFromPrefix) continue;

			int dataStart = hdrOff + 8;
			int availDwords = (raw.Length - dataStart) / 4;
			int effectiveCount = Math.Min(chunkSize, availDwords);
			if (effectiveCount < 0) continue;

			int rawDataVer = (int)(hdr & 0xFFFF);
			int dataVersion = rawDataVer & 0xFF;
			int classId = (rawDataVer >> 8) & 0xFF;
			int chunkOptions = (rawChunkVer >> 8) & 0xFF;

			var dwords = new int[effectiveCount];
			if (effectiveCount > 0)
				Buffer.BlockCopy(raw, dataStart, dwords, 0, effectiveCount * 4);

			return new CkStateChunkReader(dwords, dataVersion, chunkVersion, classId, chunkOptions);
		}

		return TryParseAt(raw, 0) ?? TryParseAt(raw, 4);
	}

	private static CkStateChunkReader? TryParseAt(byte[] raw, int offset)
	{
		if (raw.Length < offset + 8) return null;

		uint hdr = BitConverter.ToUInt32(raw, offset);
		int rawDataVer = (int)(hdr & 0xFFFF);
		int rawChunkVer = (int)(hdr >> 16);

		int dataVersion = rawDataVer & 0xFF;
		int classId = (rawDataVer >> 8) & 0xFF;
		int chunkVersion = rawChunkVer & 0xFF;
		int chunkOptions = (rawChunkVer >> 8) & 0xFF;

		if (chunkVersion < 2) return null;

		int chunkSize = BitConverter.ToInt32(raw, offset + 4);
		if (chunkSize < 0 || chunkSize > 4 * 1024 * 1024) return null;

		int dataStart = offset + 8;
		if (raw.Length < dataStart + chunkSize * 4) return null;

		var dwords = new int[chunkSize];
		if (chunkSize > 0)
			Buffer.BlockCopy(raw, dataStart, dwords, 0, chunkSize * 4);

		return new CkStateChunkReader(dwords, dataVersion, chunkVersion, classId, chunkOptions);
	}

	private CkStateChunkReader(int[] dwords, int dataVersion, int chunkVersion, int classId, int chunkOptions)
	{
		_dwords = dwords;
		DataVersion = dataVersion;
		ChunkVersion = chunkVersion;
		ClassId = classId;
		ChunkOptions = chunkOptions;
	}

	public int DwordCount => _dwords.Length;

	public IEnumerable<CkStateIdentifier> EnumerateIdentifiers()
	{
		if (_dwords.Length < 2) yield break;

		var seen = new HashSet<int>();
		int pos = 0;

		while (pos >= 0 && pos + 1 < _dwords.Length && seen.Add(pos))
		{
			uint id = (uint)_dwords[pos];
			int next = _dwords[pos + 1];

			int payloadEnd = (next > pos + 1 && next <= _dwords.Length)
				? next
				: _dwords.Length;

			int payloadCount = Math.Max(0, payloadEnd - (pos + 2));
			yield return new CkStateIdentifier(id, pos, next, payloadCount);

			if (next == 0 || next >= _dwords.Length || next <= pos)
				break;

			pos = next;
		}
	}

	public bool TryGetPayload(uint id, out int[] payload)
	{
		foreach (var ident in EnumerateIdentifiers())
		{
			if (ident.Id != id) continue;

			payload = new int[ident.PayloadDwordCount];
			if (payload.Length > 0)
				Array.Copy(_dwords, ident.Offset + 2, payload, 0, payload.Length);
			return true;
		}

		payload = Array.Empty<int>();
		return false;
	}

	public static byte[] PayloadToBytes(IReadOnlyList<int> payload)
	{
		var bytes = new byte[payload.Count * 4];
		for (int i = 0; i < payload.Count; i++)
		{
			var v = payload[i];
			bytes[i * 4 + 0] = (byte)(v & 0xFF);
			bytes[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
			bytes[i * 4 + 2] = (byte)((v >> 16) & 0xFF);
			bytes[i * 4 + 3] = (byte)((v >> 24) & 0xFF);
		}
		return bytes;
	}

	public bool SeekIdentifier(uint id)
	{
		if (_dwords.Length < 2) return false;

		int pos = 0;
		while (true)
		{
			if ((uint)_dwords[pos] == id)
			{
				_cursor = pos + 2;
				return true;
			}
			int next = _dwords[pos + 1];
			if (next == 0 || next >= _dwords.Length) break;
			pos = next;
		}
		return false;
	}

	public int ReadInt32() => _cursor < _dwords.Length ? _dwords[_cursor++] : 0;
	public uint ReadUInt32() => _cursor < _dwords.Length ? (uint)_dwords[_cursor++] : 0u;
	public float ReadFloat() => _cursor < _dwords.Length ? BitConverter.Int32BitsToSingle(_dwords[_cursor++]) : 0f;
	public bool HasMore => _cursor < _dwords.Length;
	public int Remaining => _dwords.Length - _cursor;
	public int Cursor => _cursor;

	public void Skip(int dwordCount)
	{
		_cursor = Math.Min(_cursor + dwordCount, _dwords.Length);
	}

	public string ReadString()
	{
		if (_cursor >= _dwords.Length) return string.Empty;

		int size = _dwords[_cursor++];
		if (size <= 0) return string.Empty;

		int dwordCount = (size + 3) / 4;
		if (_cursor + dwordCount > _dwords.Length)
		{

			_cursor = _dwords.Length;
			return string.Empty;
		}

		var bytes = new byte[dwordCount * 4];
		Buffer.BlockCopy(_dwords, _cursor * 4, bytes, 0, bytes.Length);
		_cursor += dwordCount;

		int actualLen = Math.Max(0, Math.Min(size - 1, bytes.Length));
		return Encoding.ASCII.GetString(bytes, 0, actualLen);
	}
}
