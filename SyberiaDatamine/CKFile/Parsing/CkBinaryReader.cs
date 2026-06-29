using System.IO;
using System.Numerics;
using System.Text;

namespace SyberiaDatamine.CKFile.Parsing;

public sealed class CkBinaryReader : IDisposable
{
	private readonly BinaryReader _br;
	private bool _disposed;

	public long Position => _br.BaseStream.Position;
	public long Length => _br.BaseStream.Length;
	public long BytesRemaining => Length - Position;
	public bool IsAtEnd => Position >= Length;

	public CkBinaryReader(byte[] data)
		: this(new MemoryStream(data, writable: false), leaveOpen: false) { }

	public CkBinaryReader(Stream stream, bool leaveOpen = false)
	{
		_br = new BinaryReader(stream, Encoding.ASCII, leaveOpen);
	}

	public byte ReadByte() => _br.ReadByte();
	public sbyte ReadSByte() => _br.ReadSByte();
	public short ReadInt16() => _br.ReadInt16();
	public ushort ReadUInt16() => _br.ReadUInt16();
	public int ReadInt32() => _br.ReadInt32();
	public uint ReadUInt32() => _br.ReadUInt32();
	public long ReadInt64() => _br.ReadInt64();
	public ulong ReadUInt64() => _br.ReadUInt64();
	public float ReadFloat() => _br.ReadSingle();
	public double ReadDouble() => _br.ReadDouble();
	public byte[] ReadBytes(int count) => _br.ReadBytes(count);

	public bool ReadBool32() => _br.ReadInt32() != 0;

	public uint ReadCkId() => _br.ReadUInt32();

	public string? ReadCkString()
	{
		if (BytesRemaining < 4) return null;
		int len = _br.ReadInt32();
		if (len < 0) return null;
		if (len == 0) return string.Empty;
		if (BytesRemaining < len) return null;
		return Encoding.ASCII.GetString(_br.ReadBytes(len));
	}

	public Vector3 ReadVector3() => new(_br.ReadSingle(), _br.ReadSingle(), _br.ReadSingle());

	public (float U, float V) ReadUV() => (_br.ReadSingle(), _br.ReadSingle());

	public (byte B, byte G, byte R, byte A) ReadBgra()
		=> (_br.ReadByte(), _br.ReadByte(), _br.ReadByte(), _br.ReadByte());

	public void Skip(int count)
	{
		if (count <= 0) return;
		if (_br.BaseStream.CanSeek)
			_br.BaseStream.Seek(count, SeekOrigin.Current);
		else
			_br.ReadBytes(count);
	}

	public bool TryReadInt32(out int value)
	{
		if (BytesRemaining < 4) { value = 0; return false; }
		value = _br.ReadInt32();
		return true;
	}

	public bool TryReadFloat(out float value)
	{
		if (BytesRemaining < 4) { value = 0f; return false; }
		value = _br.ReadSingle();
		return true;
	}

	public bool TryReadUInt32(out uint value)
	{
		if (BytesRemaining < 4) { value = 0u; return false; }
		value = _br.ReadUInt32();
		return true;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_br.Dispose();
	}
}
