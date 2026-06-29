namespace SyberiaDatamine.Core;

public sealed class ArchiveEntry
{
	public required string Name { get; init; }
	public long FileSize { get; init; }
	public AssetKind Kind { get; set; }
	public string LoaderId { get; set; } = "";

	public NemoObject? NemoObj { get; init; }

	public required Func<byte[]?> DataLoader { get; init; }

	public Func<byte[]?>? RawDataLoader { get; init; }

	public byte[]? LoadData() => DataLoader();
	public byte[]? LoadRawData() => RawDataLoader?.Invoke() ?? DataLoader();
}

public interface IArchiveSource : IDisposable
{
	string ArchiveName { get; }
	IReadOnlyList<ArchiveEntry> Entries { get; }
}
