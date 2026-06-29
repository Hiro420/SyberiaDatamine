namespace SyberiaDatamine.Core;

public interface IAssetLoader
{

	string Id { get; }

	AssetKind Kind { get; }

	string[] Extensions { get; }

	bool CanHandle(string filePath);
	bool CanHandle(byte[] data, string fileName);

	AssetPreviewData LoadFromFile(string filePath);
	AssetPreviewData LoadFromData(byte[] data, string fileName);
}
