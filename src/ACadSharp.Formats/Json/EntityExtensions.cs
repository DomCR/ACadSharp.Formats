#if NET
using ACadSharp.Entities;
using ACadSharp.Formats.Json.Converters;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json;

public static class EntityExtensions
{
	public static string ToJson<T>(this T entity)
		where T : Entity
	{
		string json = JsonSerializer.Serialize(entity, entity.GetType(),
			options: new JsonSerializerOptions
			{
				WriteIndented = true,
				//ReferenceHandler = ReferenceHandler.IgnoreCycles,
				//ReferenceHandler = new ACadReferenceHandler(),
				IgnoreReadOnlyProperties = true,
				IgnoreReadOnlyFields = true,
				Converters =
				{
					new CadConverterFactory()
				}
			});

		return json;
	}
}
#endif