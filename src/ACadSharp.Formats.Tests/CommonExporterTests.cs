using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Formats.Tests;

public abstract class CommonExporterTests<T>
	where T : IExporter
{
	public static CadDocument Document { get; }

	public static readonly TheoryData<string> LayoutNames = new();

	protected readonly ITestOutputHelper _output;

	static CommonExporterTests()
	{
		Document = TestUtils.GetDocument();

		foreach (var item in Document.Layouts)
		{
			if (!item.IsPaperSpace)
			{
				continue;
			}

			LayoutNames.Add(item.Name);
		}
	}

	[Theory]
	[MemberData(nameof(LayoutNames))]
	public void ExportLayout(string name)
	{
		var layout = Document.Layouts[name];

		using (T exporter = this.getExporter(name))
		{
			exporter.Export(layout);
		}
	}

	protected abstract T getExporter(string name);

	protected CommonExporterTests(ITestOutputHelper output)
	{
		this._output = output;
	}
}
