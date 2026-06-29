using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile.Objects;

public abstract class CkObject
{

	public int ObjectIndex { get; init; } = -1;

	public uint CkId { get; init; }

	public int ClassId { get; init; }

	public string ClassName => NemoClassRegistry.GetName(ClassId);

	public string GroupName => NemoClassRegistry.GetGroupName(ClassId);

	public string Name { get; init; } = string.Empty;

	public int BytesConsumed { get; init; }

	public abstract string Summary { get; }
}
