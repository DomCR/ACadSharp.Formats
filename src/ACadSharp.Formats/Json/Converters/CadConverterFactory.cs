#if NET
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json.Converters;

public class CadConverterFactory : JsonConverterFactory
{
	/// <inheritdoc/>
	public override bool CanConvert(Type typeToConvert)
	{
		if (typeToConvert.IsSubclassOf(typeof(CadObject)))
		{
			return true;
		}

		if (typeToConvert == typeof(CadDocument))
		{
			return true;
		}

		return false;
	}

	/// <inheritdoc/>
	public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
	{
		if (typeToConvert == typeof(CadDocument))
		{
			return new CadDocumentConverter();
		}

		return new CommonCadConverter();
	}
}
#endif