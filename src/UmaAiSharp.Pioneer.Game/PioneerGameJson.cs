using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Game;

internal static class PioneerGameJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        IncludeFields = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info =>
            {
                if (info.Kind == JsonTypeInfoKind.Object && info.Type.IsValueType
                    && info.Type.Namespace?.StartsWith("UmaAiSharp.", StringComparison.Ordinal) == true)
                    foreach (var property in info.Properties)
                        if (property.Set is not null
                            && !(info.Type == typeof(PioneerRuntimeAction) && property.Name is "plan" or "storyId" or "supportPosition")
                            && !(info.Type == typeof(TrainingPlacement) && property.Name == "director"))
                            property.IsRequired = true;
            } }
        },
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false), new ConstructionPlanConverter() }
    };

    sealed class ConstructionPlanConverter : JsonConverter<PioneerConstructionPlan>
    {
        sealed record Plan(ImmutableArray<PlannedUpgrade> Steps);
        public override PioneerConstructionPlan Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<Plan>(ref reader, options) ?? throw new JsonException("建设计划为空。");
            if (value.Steps.IsDefault) throw new JsonException("建设计划缺少steps。");
            return new(value.Steps);
        }
        public override void Write(Utf8JsonWriter writer, PioneerConstructionPlan value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new Plan(value.Steps), options);
    }
}
