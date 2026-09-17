using ACadSharp.Formats.Pdf.Core;
using ACadSharp.Objects;
using ACadSharp.Tables;
using System.IO;

namespace ACadSharp.Formats.Pdf;

/// <summary>
/// Represents an exporter that exports CAD documents to PDF format.
/// </summary>
public class PdfExporter : ExporterBase
{
	/// <summary>
	/// Configuration for the <see cref="PdfExporter"/> instance.
	/// </summary>
	public PdfConfiguration Configuration { get; } = new PdfConfiguration();

	private readonly PdfDocument _pdf = new PdfDocument();

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
