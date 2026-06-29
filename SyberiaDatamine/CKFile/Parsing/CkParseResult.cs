namespace SyberiaDatamine.CKFile.Parsing;

public sealed class CkParseResult<T>
{
	private CkParseResult() { }

	public T? Value { get; private init; }

	public string? Error { get; private init; }

	public bool Success => Error is null;

	public int BytesConsumed { get; private init; }

	public static CkParseResult<T> Ok(T value, int bytesConsumed = 0)
		=> new() { Value = value, BytesConsumed = bytesConsumed };

	public static CkParseResult<T> Fail(string error)
		=> new() { Error = error };

	public override string ToString()
		=> Success ? $"Ok({Value})" : $"Fail({Error})";
}
