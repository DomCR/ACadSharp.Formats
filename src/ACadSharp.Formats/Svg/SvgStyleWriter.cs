using ACadSharp.Tables;
using System;
using System.Collections.Generic;
using System.Xml;

namespace ACadSharp.Formats.Svg;

internal class SvgStyleWriter
{
	private readonly Dictionary<string, Layer> _layers = new();

	private readonly Dictionary<string, LineType> _lineTypes = new();

	private readonly CadXmlWriter _writer;

	public SvgStyleWriter(CadXmlWriter writer)
	{
		this._writer = writer;
	}

	public void AddLayer(Layer layer)
	{
		if (layer == null || this._layers.ContainsKey(layer.Name))
		{
			return;
		}
		this._layers.Add(layer.Name, layer);
	}

	public void AddLineType(LineType lineType)
	{
		if (lineType == null || this._lineTypes.ContainsKey(lineType.Name))
		{
			return;
		}

		this._lineTypes.Add(lineType.Name, lineType);
	}

	public void WriteStyles()
	{
		this._writer.WriteStartElement("style");

		this._writer.WriteString(Environment.NewLine);
		this.writeCssLine(".entity{");
		this.writeCssLine("vector-effect: non-scaling-stroke;");
		this.writeCssLine("}");
		this._writer.WriteString(Environment.NewLine);

		foreach (var layer in this._layers.Values)
		{
			this.writeCssLine($".layer_{layer.Name}{{");
			this.writeCssLine($"stroke: {this._writer.ColorSvg(layer.Color)};");
			this.writeCssLine($"stroke-width: {this._writer.LineWeightValueToSvg(layer.LineWeight)};");
			this.writeCssLine("}");
			this._writer.WriteString(Environment.NewLine);
		}

		if (false)
		{
			// It cannot get the lineweight of the entity, so the points will not be the correct size
			foreach (var lt in this._lineTypes.Values)
			{
				if (lt.HasShapes)
				{
					continue;
				}

				this.writeCssLine($".lt_{lt.Name}{{");
				this.writeCssLine("}");
				this._writer.WriteString(Environment.NewLine);
			}
		}

		this._writer.WriteEndElement();
	}

	private void writeCssLine(string line)
	{
		if (this._writer.Formatting == Formatting.Indented)
		{
			string indent = new string(this._writer.IndentChar, this._writer.Indentation);
			this._writer.WriteString(indent);
		}

		this._writer.WriteString(line);
		this._writer.WriteString(Environment.NewLine);
	}
}