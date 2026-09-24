using System.Text.Json.Nodes;

namespace iOneDataGrove.Persistence.Data;

public static class PostgreSqlValueSanitizer
{
    private const char ReplacementCharacter = '\uFFFD';

    public static string NormalizeText(string value) =>
        value.Contains('\0', StringComparison.Ordinal)
            ? value.Replace('\0', ReplacementCharacter)
            : value;

    public static string NormalizeJson(string rawJson)
    {
        if (!rawJson.Contains("\\u0000", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(rawJson);
        }

        var root = JsonNode.Parse(rawJson)
            ?? throw new InvalidOperationException("JSON grezzo non valido.");
        NormalizeNode(root);
        return root.ToJsonString();
    }

    private static void NormalizeNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var propertyName in jsonObject.Select(item => item.Key).ToArray())
            {
                var child = jsonObject[propertyName];
                if (child is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    jsonObject[propertyName] = NormalizeText(text);
                }
                else if (child is not null)
                {
                    NormalizeNode(child);
                }
            }
            return;
        }

        if (node is not JsonArray jsonArray)
        {
            return;
        }

        for (var index = 0; index < jsonArray.Count; index++)
        {
            var child = jsonArray[index];
            if (child is JsonValue value && value.TryGetValue<string>(out var text))
            {
                jsonArray[index] = NormalizeText(text);
            }
            else if (child is not null)
            {
                NormalizeNode(child);
            }
        }
    }
}
