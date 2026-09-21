using ACadSharp.Entities;
using ACadSharp.Formats.Pdf.Core;
using ACadSharp.Objects;
using ACadSharp.Tables;
using System.Collections.Generic;
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

	/// <summary>
	/// Initializes a new instance of the <see cref="PdfExporter"/> class with the specified filename.
	/// </summary>
	/// <param name="filename">The name of the file to create.</param>
	public PdfExporter(string filename)
		: this(File.Create(filename))
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="PdfExporter"/> class with the specified stream.
	/// </summary>
	/// <param name="stream">The stream to write the PDF to.</param>
	public PdfExporter(Stream stream)
		: base(stream)
	{
	}

	/// <summary>
	/// Adds the specified layouts designed as paper space to the PDF document.
	/// </summary>
	/// <param name="layouts">The layouts to add.</param>
	public void Add(IEnumerable<Layout> layouts)
	{
		foreach (var layout in layouts)
		{
			if (!layout.IsPaperSpace)
			{
				continue;
			}

			this.Add(layout);
		}
	}

	/// <summary>
	/// Adds the specified layout to the PDF document.
	/// </summary>
	/// <param name="layout">The layout to add.</param>
	public void Add(Layout layout)
	{
		PdfPage page = this._pdf.Pages.AddPage();

		page.Layout = layout;

		foreach (Entity e in layout.AssociatedBlock.Entities)
		{
			if (e is Viewport)
			{
				continue;
			}

			page.Entities.Add(e);
		}

		foreach (Viewport vp in layout.Viewports)
		{
			if (vp.RepresentsPaper)
			{
				continue;
			}

			page.Viewports.Add(vp);
		}
	}

	/// <summary>
	/// Adds the specified block record to the PDF document.
	/// </summary>
	/// <param name="block">The block record to add.</param>
	public void Add(BlockRecord block)
	{
		PdfPage page = this._pdf.Pages.AddPage();

		page.Add(block);
	}

	/// <summary>
	/// Adds the model space from the specified document to the PDF document.
	/// </summary>
	/// <param name="document">The CAD document containing the model space to add.</param>
	public void AddModelSpace(CadDocument document)
	{
		this.Add(document.ModelSpace);
	}

	/// <summary>
	/// Adds the paper layouts from the specified document to the PDF document.
	/// </summary>
	/// <param name="document">The CAD document containing the layouts to add.</param>
	public void AddPaperLayouts(CadDocument document)
	{
		this.Add(document.Layouts);
	}

	/// <summary>
	/// Close the document and save it.
	/// </summary>
	public void Close()
	{
		using (PdfWriter writer = new PdfWriter(this.stream, this._pdf, this.Configuration))
		{
			this.Configuration.OnNotification += this.triggerNotification;
			writer.Write();
		}
	}

	/// <inheritdoc/>
	public override void Export(Layout layout)
	{
		this.Add(layout);
		this.Close();
	}

	/// <inheritdoc/>
	public override void Export(BlockRecord record)
	{
		this.Add(record);
		this.Close();
	}

	/// <inheritdoc/>
	public override void Export(CadDocument document)
	{
		this.AddPaperLayouts(document);
		this.Close();
	}
}