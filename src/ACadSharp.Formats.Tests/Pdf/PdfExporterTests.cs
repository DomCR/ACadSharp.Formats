using ACadSharp.Formats.Pdf;
using System.IO;
using Xunit.Abstractions;

namespace ACadSharp.Formats.Tests.Pdf;

public class PdfExporterTests : CommonMediaExporterTests<PdfExporter>
{
	public PdfExporterTests(ITestOutputHelper output)
		: base(output)
	{
	}

	protected override PdfExporter getExporter(string name)
	{
		string filename = Path.Combine(TestVariables.OutputPdfFolder, $"{name}.pdf");
		PdfExporter exporter = new PdfExporter(filename);
		return exporter;
	}
}