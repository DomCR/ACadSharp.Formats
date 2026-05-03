using ACadSharp.Formats.Svg;
using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Formats.Tests.Svg;

public class SvgExporterTests : CommonMediaExporterTests<SvgExporter>
{
	public SvgExporterTests(ITestOutputHelper output)
		: base(output)
	{
	}

	protected override SvgExporter getExporter(string name)
	{
		string filename = Path.Combine(TestVariables.OutputSvgFolder, $"{name}.svg");
		return new SvgExporter(filename);
	}
}