#if NET
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json;

public class CommonCadConverter : JsonConverter<CadObject>
{
	public override CadObject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		CadObject obj = (CadObject)Activator.CreateInstance(typeToConvert);

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.PropertyName)
			{
				string propertyName = reader.GetString();
				var prop = typeToConvert.GetProperty(propertyName);

				reader.Read();

				var value = JsonSerializer.Deserialize(ref reader, prop.PropertyType, options);
				prop.SetValue(obj, value);
			}
		}

		throw new NotImplementedException();
	}

	public override void Write(Utf8JsonWriter writer, CadObject value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();

		foreach (var prop in value.GetType().GetProperties())
		{
			if (!prop.CanRead)
				continue;

			if (!prop.CanWrite && options.IgnoreReadOnlyProperties)
				continue;

			var pValue = prop.GetValue(value);
			if (pValue == null)
			{
				if (!options.DefaultIgnoreCondition.HasFlag(JsonIgnoreCondition.WhenWritingNull))
				{
					writer.WriteNull(prop.Name);
				}
				continue;
			}

			if (pValue is INamedCadObject named)
			{
				writer.WriteString(prop.Name, named.Name);
				continue;
			}

			if (pValue is IHandledCadObject handled)
			{
				writer.WriteNumber(prop.Name, handled.Handle);
				continue;
			}

			writer.WritePropertyName(prop.Name);
			JsonSerializer.Serialize(writer, pValue, pValue.GetType(), options);
		}

		writer.WriteEndObject();
	}
}
#endif