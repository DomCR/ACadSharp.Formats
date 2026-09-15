using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using CSMath;
using System;
using System.IO;
using System.Text;
using System.Xml;

namespace ACadSharp.Formats.Svg;

internal class CadXmlWriter : XmlTextWriter
{
	public event NotificationEventHandler OnNotification;

	public SvgConfiguration Configuration { get; } = new();

	public bool IsPaperSpace { get; set; } = false;

	public UnitsType Units { get; }

	public CadXmlWriter(SvgConfiguration configuration, UnitsType units, Encoding encoding)
		: this(new MemoryStream(), configuration, units, encoding)
	{
	}

	public CadXmlWriter(Stream stream, SvgConfiguration configuration, UnitsType units, Encoding encoding)
		: base(stream, encoding)
	{
		this.Configuration = configuration;
		this.Units = units;
		this.Formatting = configuration.Formatting;
	}

	public string ColorSvg(Color color)
	{
		if (this.IsPaperSpace && color.Equals(Color.Default))
		{
			color = Color.Black;
		}

		return $"rgb({color.R},{color.G},{color.B})";
	}

	public string ToSvgPixelSizeFormat(IVector value)
	{
		return value.ToPixelSize(this.Units).ToSvg(this.Configuration.DecimalPlaces);
	}

	public string ToSvgPixelSizeFormat(double value)
	{
		return value.ToPixelSize(this.Units).ToSvg(this.Configuration.DecimalPlaces);
	}

	public string ToSvgFormat(double value)
	{
		return value.ToSvg(this.Units, this.Configuration.DecimalPlaces);
	}

	public double GetPointSize(IEntity entity)
	{
		return entity.GetActiveLineWeightType().GetLineWeightValue().ToPixelSize(this.Units);
	}

	public void WriteAttributeString(string localName, double value)
	{
		this.WriteAttributeString(localName, value, this.Units);
	}

	public void WriteAttributeString(string localName, double value, UnitsType units)
	{
		this.WriteAttributeString(localName, value.ToSvg(units));
	}

	public string LineWeightValueToSvg(LineWeightType lineWeight)
	{
		return $"{this.Configuration.GetLineWeightValue(lineWeight, this.Units).ToSvg(UnitsType.Millimeters)}";
	}

	public void Notify(string message, NotificationType type, Exception ex = null)
	{
		this.triggerNotification(this, new NotificationEventArgs(message, type, ex));
	}

	protected void triggerNotification(object sender, NotificationEventArgs e)
	{
		this.OnNotification?.Invoke(sender, e);
	}
}