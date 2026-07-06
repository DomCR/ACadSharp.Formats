using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.Formats.Svg;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace ACadSharp.Formats.Svg;

internal class CadXmlWriter : XmlTextWriter
{
	public event NotificationEventHandler OnNotification;

	public SvgConfiguration Configuration { get; } = new();

	public UnitsType Units { get; }

	public bool IsPaperSpace { get; set; } = false;

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

	public void WriteAttributeString(string localName, double value)
	{
		this.WriteAttributeString(localName, value, this.Units);
	}

	public void WriteAttributeString(string localName, double value, UnitsType units)
	{
		this.WriteAttributeString(localName, value.ToSvg(units));
	}

	protected void notify(string message, NotificationType type, Exception ex = null)
	{
		this.triggerNotification(this, new NotificationEventArgs(message, type, ex));
	}

	protected void triggerNotification(object sender, NotificationEventArgs e)
	{
		this.OnNotification?.Invoke(sender, e);
	}

	protected string colorSvg(Color color)
	{
		if (this.IsPaperSpace && color.Equals(Color.Default))
		{
			color = Color.Black;
		}

		return $"rgb({color.R},{color.G},{color.B})";
	}
}
