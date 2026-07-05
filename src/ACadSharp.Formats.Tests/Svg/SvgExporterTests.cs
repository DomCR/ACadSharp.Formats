using ACadSharp.Formats.Svg;
using System.IO;
using Xunit.Abstractions;

namespace ACadSharp.Formats.Tests.Svg;

public class SvgExporterTests : CommonExporterTests<SvgExporter>
{
	protected readonly SvgConfiguration _svgConfiguration = new SvgConfiguration
	{
		DefaultLineWeight = 0.15,
		LineWeightRatio = 50,
	};

	public SvgExporterTests(ITestOutputHelper output)
		: base(output)
	{
	}

	protected override SvgExporter getExporter(string name)
	{
		string filename = Path.Combine(TestVariables.OutputSvgFolder, $"{name}.svg");
		SvgExporter exporter = new SvgExporter(filename)
		{
			Configuration = this._svgConfiguration
		};
		return exporter;
	}
}