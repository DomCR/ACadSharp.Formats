using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace ACadSharp.Formats.Svg;

internal class SvgDocumentBuilder
{
	public event NotificationEventHandler OnNotification;

	public SvgConfiguration Configuration { get; } = new();

	public UnitsType Units { get; private set; }

	private readonly Encoding _encoding;

	private readonly Stream _stream;

	private SvgEntityWriter _entitiesWriter;

	private CadXmlWriter _xmlWriter;

	private SvgStyleWriter _styleWriter;

	public SvgDocumentBuilder(Stream stream, SvgConfiguration configuration) : this(stream, configuration, null)
	{
	}

	public SvgDocumentBuilder(Stream stream, SvgConfiguration configuration, Encoding encoding)
	{
		this._stream = stream;
		this.Configuration = configuration;
		this._encoding = encoding;
	}

	public void WriteBlock(BlockRecord record)
	{
		this.Units = record.Units;

		BoundingBox box = record.GetBoundingBox();

		this.startDocument(box, box, this.Units);

		this.processEntities(record.Entities);

		this.endDocument();
	}

	public void WriteLayout(Layout layout)
	{
		this.Units = layout.PaperUnits.ToUnits();

		double paperWidth = layout.PaperWidth;
		double paperHeight = layout.PaperHeight;

		switch (layout.PaperRotation)
		{
			case PlotRotation.Degrees90:
			case PlotRotation.Degrees270:
				paperWidth = layout.PaperHeight;
				paperHeight = layout.PaperWidth;
				break;
		}

		XYZ lowerCorner = XYZ.Zero;
		XYZ upperCorner = new XYZ(paperWidth, paperHeight, 0.0);
		BoundingBox paper = new BoundingBox(lowerCorner, upperCorner);

		XYZ lowerMargin = layout.UnprintableMargin.BottomLeftCorner.Convert<XYZ>();
		XYZ upperMargin = upperCorner - layout.UnprintableMargin.TopCorner.Convert<XYZ>();
		BoundingBox margins = new BoundingBox(
			lowerMargin,
			upperMargin);


		Transform transform = new Transform(
			lowerMargin.ToPixelSize(UnitsType.Millimeters),
			new XYZ(layout.PrintScale),
			XYZ.Zero);

		this.startDocument(paper, null, UnitsType.Millimeters, true);

		processEntities(layout.AssociatedBlock.Entities, transform);

		this.endDocument();
	}

	protected void notify(string message, NotificationType type, Exception ex = null)
	{
		this.triggerNotification(this, new NotificationEventArgs(message, type, ex));
	}

	protected void triggerNotification(object sender, NotificationEventArgs e)
	{
		this.OnNotification?.Invoke(sender, e);
	}

	private void endDocument()
	{
		this._styleWriter.WriteStyles();

		//this._entitiesWriter.WriteEntities();

		this._xmlWriter.WriteEndElement();
		this._xmlWriter.WriteEndDocument();
		this._xmlWriter.Close();
	}

	private void initWriters(bool isPaperSpace)
	{
		this._xmlWriter = new CadXmlWriter(this._stream, this.Configuration, this.Units, this._encoding);
		this._xmlWriter.IsPaperSpace = isPaperSpace;
		this._xmlWriter.OnNotification += this.triggerNotification;

		this._entitiesWriter = new SvgEntityWriter(this._xmlWriter);

		this._styleWriter = new SvgStyleWriter(this._xmlWriter);
	}

	private void mergeStream(CadXmlWriter writer)
	{
		writer.Flush();
		writer.BaseStream.Position = 0;

		XDocument temp = XDocument.Load(writer.BaseStream, LoadOptions.None);
		if (temp.Root is null)
		{
			return;
		}

		foreach (XNode node in temp.Root.Nodes())
		{
			node.WriteTo(this._xmlWriter);
		}
	}

	private void processEntities(IEnumerable<Entity> entities)
	{
		this.processEntities(entities, new Transform());
	}

	private void processEntities(IEnumerable<Entity> entities, Transform transform)
	{
		foreach (var entity in entities)
		{
			this.processEntity(entity, transform);
		}
	}

	private void processEntity(Entity entity, Transform transform)
	{
		this._styleWriter.AddLineType(entity.LineType);
		this._styleWriter.AddLayer(entity.Layer);

		this._entitiesWriter.WriteEntity(entity, transform);
	}

	private void startDocument(BoundingBox box, BoundingBox? viewBox, UnitsType units, bool isPaperSpace = false)
	{
		this.initWriters(isPaperSpace);

		this._xmlWriter.WriteStartDocument();

		this._xmlWriter.WriteStartElement("svg");
		this._xmlWriter.WriteAttributeString("xmlns", "http://www.w3.org/2000/svg");

		this._xmlWriter.WriteAttributeString("width", box.Max.X - box.Min.X, units);
		this._xmlWriter.WriteAttributeString("height", box.Max.Y - box.Min.Y, units);

		if (viewBox.HasValue)
		{
			var vb = viewBox.Value;
			this._xmlWriter.WriteStartAttribute("viewBox");
			this._xmlWriter.WriteValue(vb.Min.X.ToPixelSize(units));
			this._xmlWriter.WriteValue(" ");
			this._xmlWriter.WriteValue(vb.Min.Y.ToPixelSize(units));
			this._xmlWriter.WriteValue(" ");
			this._xmlWriter.WriteValue(vb.LengthX.ToPixelSize(units));
			this._xmlWriter.WriteValue(" ");
			this._xmlWriter.WriteValue(vb.LengthY.ToPixelSize(units));
			this._xmlWriter.WriteEndAttribute();
		}

		this._xmlWriter.WriteAttributeString("transform", $"scale(1,-1)");

		if (isPaperSpace)
		{
			this._xmlWriter.WriteAttributeString("style", "background-color:white");
		}
	}
}