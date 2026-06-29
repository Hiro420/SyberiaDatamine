using SyberiaDatamine.Core;
using System.IO;
using System.Text;

namespace SyberiaDatamine.Parsers;

public sealed class VxbgArchiveSource : IArchiveSource
{
	private const uint Magic = 0x47425856;

	private readonly string _filePath;
	private readonly object _ioLock = new();
	private readonly List<ArchiveEntry> _entries;

	public string ArchiveName { get; }
	public IReadOnlyList<ArchiveEntry> Entries => _entries;

	public VxbgArchiveSource(string filePath)
	{
		_filePath = filePath;
		ArchiveName = Path.GetFileName(filePath);
		_entries = ReadToc(filePath);
	}

	private List<ArchiveEntry> ReadToc(string filePath)
	{
		using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

		var magic = br.ReadUInt32();
		if (magic != Magic)
			throw new InvalidDataException($"Not a VXBG file: {filePath}");

		var tableSize = br.ReadUInt32();
		if (tableSize > 0x1000_0000)
			throw new InvalidDataException("VXBG table size sanity check failed.");

		var table = br.ReadBytes((int)tableSize);
		long dataBase = 8L + tableSize;
		long offset = dataBase;

		var entries = new List<ArchiveEntry>();
		int pos = 0;

		while (pos < table.Length)
		{
			int end = Array.IndexOf(table, (byte)0, pos);
			if (end < 0) break;

			var name = Encoding.ASCII.GetString(table, pos, end - pos);
			pos = end + 1;

			if (pos + 4 > table.Length) break;
			var size = BitConverter.ToUInt32(table, pos);
			pos += 4;

			if (string.IsNullOrEmpty(name)) { offset += size; continue; }

			long capturedOffset = offset;
			uint capturedSize = size;

			entries.Add(new ArchiveEntry
			{
				Name = name,
				FileSize = size,
				DataLoader = () => LoadSlice(capturedOffset, capturedSize),
			});

			offset += size;
		}

		return entries;
	}

	private byte[] LoadSlice(long fileOffset, uint size)
	{
		lock (_ioLock)
		{
			using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			fs.Seek(fileOffset, SeekOrigin.Begin);
			var buf = new byte[size];
			int read = 0;
			while (read < buf.Length)
			{
				int n = fs.Read(buf, read, buf.Length - read);
				if (n == 0) break;
				read += n;
			}
			return buf;
		}
	}

	public void Dispose() { }
}
