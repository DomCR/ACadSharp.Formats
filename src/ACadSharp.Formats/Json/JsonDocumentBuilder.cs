using ACadSharp.IO;

namespace ACadSharp.Formats.Json;

internal class JsonDocumentBuilder : CadDocumentBuilder
{
	public override bool IgnoreProxyGraphics { get; }

	public override bool KeepUnknownEntities { get; }

	public override bool KeepUnknownNonGraphicalObjects { get; }

	public JsonDocumentBuilder(CadDocument document) : base(ACadVersion.Unknown, document)
	{
	}
}