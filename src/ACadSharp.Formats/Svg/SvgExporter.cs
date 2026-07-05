using ACadSharp.Objects;
using ACadSharp.Tables;
using System.IO;
using System.Xml;

namespace ACadSharp.Formats.Svg;

public class SvgExporter : ExporterBase
{
	public SvgConfiguration Configuration { get; set; } = new SvgConfiguration();

	private SvgDocumentBuilder _builder;

	public SvgExporter(string filename)
		: this(File.Create(filename))
	{
	}

	public SvgExporter(Stream stream)
		: base(stream)
	{
	}

	public override void Export(Layout layout)
	{
		this.createBuilder();

		this._builder.WriteLayout(layout);
	}

	public override void Export(BlockRecord record)
	{
		this.createBuilder();

		this._builder.WriteBlock(record);
	}

	public override void Export(CadDocument document)
	{
		this.createBuilder();

		this._builder.WriteBlock(document.ModelSpace);
	}

	private void createBuilder()
	{
		this._builder = new SvgDocumentBuilder(this._stream, this.Configuration);
		this._builder.OnNotification += this.triggerNotification;
	}
}