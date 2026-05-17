#if NET
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json.Converters;

public class CadDocumentConverter : JsonConverter<CadDocument>
{
	public override CadDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		throw new NotImplementedException();
	}
	public override void Write(Utf8JsonWriter writer, CadDocument value, JsonSerializerOptions options)
	{
		throw new NotImplementedException();
	}
}
#endif