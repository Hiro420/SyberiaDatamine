using System.IO;

namespace SyberiaDatamine;

internal static class TextureModule
{
	private static readonly byte[] JpgHeader =
	{
		0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46
	};

	public static MemoryStream LoadSyjTexture(string filePath)
	{
		if (!File.Exists(filePath))
			throw new FileNotFoundException($"File not found: {filePath}");

		var inputBytes = File.ReadAllBytes(filePath);
		var output = new MemoryStream();
		output.Write(JpgHeader, 0, JpgHeader.Length);
		output.Write(inputBytes, 0, inputBytes.Length);
		output.Seek(0, SeekOrigin.Begin);
		return output;
	}

	public static MemoryStream LoadSyjTextureFromData(byte[] data)
	{
		if (data == null || data.Length == 0)
			throw new ArgumentException("Data is null or empty");

		var output = new MemoryStream();
		output.Write(JpgHeader, 0, JpgHeader.Length);
		output.Write(data, 0, data.Length);
		output.Seek(0, SeekOrigin.Begin);
		return output;
	}

	public static MemoryStream FixHeader(MemoryStream input)
	{
		var output = new MemoryStream();
		output.Write(JpgHeader, 0, JpgHeader.Length);
		input.Seek(0, SeekOrigin.Begin);
		input.CopyTo(output);
		output.Seek(0, SeekOrigin.Begin);
		return output;
	}
}
