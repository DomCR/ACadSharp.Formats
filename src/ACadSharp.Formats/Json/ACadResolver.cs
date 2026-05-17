#if NET
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACadSharp.Formats.Json;

public class ACadResolver : ReferenceResolver
{
	private readonly Dictionary<object, string> _objectToReferenceIdMap = new(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<string, object> _referenceIdToObjectMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

	private uint _referenceCount;

	public override void AddReference(string referenceId, object value)
	{
		if (!_referenceIdToObjectMap.TryAdd(referenceId, value))
		{
			throw new JsonException();
		}
	}

	public override string GetReference(object value, out bool alreadyExists)
	{
		string referenceId = string.Empty;

		if (_referenceCount == 0)
		{
			alreadyExists = false;
			_referenceCount++;

			if (value is IHandledCadObject id)
			{
				referenceId = id.ToString();
			}

			return referenceId;
		}

		if (value is INamedCadObject named)
		{
			referenceId = named.ToString();
		}
		else if (value is IHandledCadObject handled)
		{
			referenceId = handled.ToString();
		}
		else
		{
			alreadyExists = false;
			return string.Empty;
		}

		alreadyExists = true;
		return referenceId;
	}

	public override object ResolveReference(string referenceId)
	{
		if (!_referenceIdToObjectMap.TryGetValue(referenceId, out object value))
		{
			throw new JsonException();
		}

		return value;
	}
}
#endif