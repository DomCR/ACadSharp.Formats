using ACadSharp.Tables;
using ACadSharp.Types.Units;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace ACadSharp.Formats.Svg;

internal class SvgStyleWriter : CadXmlWriter
{
	private readonly Dictionary<string, Layer> _layers = new();

	private readonly Dictionary<string, LineType> _lineTypes = new();

	public SvgStyleWriter(SvgConfiguration configuration, UnitsType units, Encoding encoding)
		: base(configuration, units, encoding)
	{
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
		this.WriteStartElement("svg", "http://www.w3.org/2000/svg");
		this.WriteStartElement("style");

		this.WriteString(Environment.NewLine);
		this.writeCssLine(".entity{");
		this.writeCssLine("vector-effect: non-scaling-stroke;");
		this.writeCssLine("}");
		this.WriteString(Environment.NewLine);

		foreach (var layer in this._layers.Values)
		{
			this.writeCssLine($".layer_{layer.Name}");
			this.writeCssLine("{");
			this.writeCssLine($"stroke: {this.colorSvg(layer.Color)};");
			this.writeCssLine($"stroke-width: {this.Configuration.GetLineWeightValue(layer.LineWeight, this.Units).ToSvg(UnitsType.Millimeters)}");
			this.writeCssLine("}");
			this.WriteString(Environment.NewLine);
		}

		this.WriteEndElement();
		this.WriteEndElement();
	}

	private void writeCssLine(string line)
	{
		string indent = new string(this.IndentChar, this.Indentation);
		this.WriteString(indent);
		this.WriteString(line);
		this.WriteString(Environment.NewLine);
	}
}