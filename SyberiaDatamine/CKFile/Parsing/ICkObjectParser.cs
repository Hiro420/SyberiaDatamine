using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile.Parsing;

public interface ICkObjectParser<T> where T : CkObject
{

	IReadOnlyList<int> SupportedClassIds { get; }

	CkParseResult<T> Parse(CkBinaryReader reader, NemoObject header);
}
