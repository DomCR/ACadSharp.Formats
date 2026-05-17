#if NET
using ACadSharp.Entities;
using ACadSharp.Formats.Json.Converters;
using System.Text.Json;

namespace ACadSharp.Formats.Json;

public class JsonExporter
{
	public static string Serialize<T>(T obj, JsonSerializerOptions options = null)
		where T : CadObject
	{
		if (options == null)
		{
			options = new JsonSerializerOptions();
		}

		options.Converters.Add(new CadConverterFactory());

		return JsonSerializer.Serialize(obj, obj.GetType(), options);
	}
}
#endif