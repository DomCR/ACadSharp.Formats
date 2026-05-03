using ACadSharp.Objects;
using ACadSharp.Tables;
using System.IO;
using System.Xml;

namespace ACadSharp.Formats.Svg;

public class SvgExporter : Exporter, IMediaExporter
{
	public SvgConfiguration Configuration { get; set; } = new SvgConfiguration();

	private readonly SvgXmlWriter _writer;

	public SvgExporter(string filename)
		: this(File.Create(filename))
	{
	}

	public SvgExporter(Stream stream)
		: base(stream)
	{
		StreamWriter textWriter = new StreamWriter(this._stream);
		this._writer = new SvgXmlWriter(this._stream, this.Configuration)
		{
			Formatting = Formatting.Indented
		};

		this._writer.OnNotification += this.triggerNotification;
	}

	public void Export(Layout layout)
	{
		this._writer.WriteLayout(layout);
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