using ACadSharp.Formats.Pdf.Core;
using ACadSharp.Objects;
using CSMath;
using System;
using System.ComponentModel;

namespace ACadSharp.Formats.Pdf.Extensions;

internal static class PdfUnitTypeExtensions
{
	public static double Transform(this PdfUnitType type, double value)
	{
		return type switch
		{
			PdfUnitType.Point => value,
			PdfUnitType.Inch => value * 72,
			PdfUnitType.Millimeter => value * (72 / 25.4),
			PdfUnitType.Centimeter => value * (72 / 2.54),
			PdfUnitType.Presentation => value * (72d / 96),
			_ => throw new InvalidEnumArgumentException(nameof(type), (int)type, typeof(PdfUnitType))
		};
	}
}
