using ACadSharp.Objects;
using ACadSharp.Tables;
using System.IO;

namespace ACadSharp.Formats.Pdf;

public class PdfExporter : ExporterBase
{
	public PdfExporter(string filename)
		: this(File.Create(filename))
	{
	}

	public PdfExporter(Stream stream)
		: base(stream)
	{
	}

	public override void Export(Layout layout)
	{
		throw new System.NotImplementedException();
	}

	public override void Export(BlockRecord record)
	{
		throw new System.NotImplementedException();
	}

	public override void Export(CadDocument document)
	{
		throw new System.NotImplementedException();
	}
}