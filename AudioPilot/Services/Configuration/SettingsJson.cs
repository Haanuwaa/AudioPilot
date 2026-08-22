using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AudioPilot.Services.Configuration
{
    internal static class SettingsJson
    {
        internal static JsonSerializerOptions Options { get; } = CreateOptions();
        internal static JsonSerializerOptions ImportOptions { get; } = CreateImportOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                AllowDuplicateProperties = false,
                Converters = { new DefinedEnumJsonConverterFactory() },
            };
            options.MakeReadOnly(populateMissingResolver: true);
            return options;
        }

        private static JsonSerializerOptions CreateImportOptions()
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(typeInfo =>
            {
                for (int index = typeInfo.Properties.Count - 1; index >= 0; index--)
                {
                    JsonPropertyInfo property = typeInfo.Properties[index];
                    if (property.IsExtensionData || (property.Get == null && property.Set == null))
                    {
                        typeInfo.Properties.RemoveAt(index);
                    }
                }
            });
            var options = new JsonSerializerOptions(Options)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                TypeInfoResolver = resolver,
            };
            options.MakeReadOnly();
            return options;
        }

        /// <summary>Validates import property names without materializing a mutable tree. The caller owns the returned document.</summary>
        internal static JsonDocument ParseDocument(string json)
        {
            JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            try
            {
                ValidatePropertyNames(document.RootElement);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        private static void ValidatePropertyNames(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException($"Duplicate JSON property: {property.Name}.");
                    }

                    ValidatePropertyNames(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in node.EnumerateArray())
                {
                    ValidatePropertyNames(item);
                }
            }
        }

        internal static bool TryGetProperty(JsonElement document, string name, out JsonElement value)
        {
            foreach (JsonProperty property in document.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
