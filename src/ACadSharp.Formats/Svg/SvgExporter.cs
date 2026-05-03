using ACadSharp.Objects;
using ACadSharp.Tables;
using System.IO;
using System.Xml;

namespace ACadSharp.Formats.Svg;

public class SvgExporter : Exporter, IMediaExporter
{
	public SvgConfiguration Configuration { get; set; } = new SvgConfiguration();

	private SvgXmlWriter _writer;

	public SvgExporter(string filename)
		: this(File.Create(filename))
	{
	}

	public SvgExporter(Stream stream)
		: base(stream)
	{
	}

	public void Export(Layout layout)
	{
		this.createWriter();

		this._writer.WriteLayout(layout);
	}

	public override void Export(BlockRecord record)
	{
		this.createWriter();

		this._writer.WriteBlock(record);
	}

	public override void Export(CadDocument document)
	{
		this.createWriter();

		this._writer.WriteBlock(document.ModelSpace);
	}

	private void createWriter()
	{
		StreamWriter textWriter = new StreamWriter(this._stream);
		this._writer = new SvgXmlWriter(this._stream, this.Configuration)
		{
			Formatting = Formatting.Indented
		};

		this._writer.OnNotification += this.triggerNotification;
	}
}