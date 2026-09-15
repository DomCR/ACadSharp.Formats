using ACadSharp.Objects;
using ACadSharp.Tables;
using System.IO;

namespace ACadSharp.Formats.Svg;

/// <summary>
/// Exports CAD data to SVG (Scalable Vector Graphics) format.
/// </summary>
public class SvgExporter : ExporterBase
{
	/// <summary>
	/// Gets or sets the configuration settings for SVG export.
	/// </summary>
	public SvgConfiguration Configuration { get; set; } = new SvgConfiguration();

	private SvgDocumentBuilder _builder;

	/// <summary>
	/// Initializes a new instance of the <see cref="SvgExporter"/> class with the specified file path.
	/// </summary>
	/// <param name="filename">The path to the output SVG file.</param>
	public SvgExporter(string filename)
		: this(File.Create(filename))
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="SvgExporter"/> class with the specified stream.
	/// </summary>
	/// <param name="stream">The stream to write the SVG output to.</param>
	public SvgExporter(Stream stream)
		: base(stream)
	{
	}

	/// <inheritdoc/>
	public override void Export(Layout layout)
	{
		this.createBuilder();

		this._builder.WriteLayout(layout);
	}

	/// <inheritdoc/>
	public override void Export(BlockRecord record)
	{
		this.createBuilder();

		this._builder.WriteBlock(record);
	}

	/// <inheritdoc/>
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