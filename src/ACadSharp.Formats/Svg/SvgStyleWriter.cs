using ACadSharp.Tables;
using ACadSharp.Types.Units;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ACadSharp.Formats.Svg;

internal class SvgStyleWriter : CadXmlWriter
{
	private readonly Dictionary<string, Layer> _layers = new();

	private readonly Dictionary<string, LineType> _lineTypes = new();

	public SvgStyleWriter(SvgConfiguration configuration, UnitsType units, Encoding encoding) : base(configuration, units, encoding)
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
}