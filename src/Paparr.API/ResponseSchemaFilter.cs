using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Paparr.API.Domain;
using Paparr.API.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Paparr.API;

public sealed class ResponseSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        var type = context.Type;
        if (type.Namespace != "Paparr.API.Models" && type != typeof(Controllers.SessionDto) &&
            type != typeof(Services.WorkerDiagnosticsSnapshot) && type != typeof(Services.WorkerStageSnapshot)) return;
        if (type.Name.EndsWith("InputDto")) return;
        if (!type.Name.EndsWith("Dto") && !type.Name.StartsWith("PagedResult`") &&
            !type.Name.EndsWith("Snapshot")) return;

        // Responses include nullable properties as explicit nulls; input validation is separate.
        if (schema is OpenApiSchema response)
            response.Required = new HashSet<string>(response.Properties?.Keys ?? []);

        if (schema is not OpenApiSchema responseSchema || responseSchema.Properties is null) return;
        if (type == typeof(ImportJobDto)) SetEnum(responseSchema, "status", typeof(ImportStatus));
        else if (type == typeof(SourceCleanupDto)) SetEnum(responseSchema, "status", typeof(SourceCleanupStatus));
        else if (type == typeof(LibraryBookDto)) SetEnum(responseSchema, "integrityStatus", typeof(IntegrityStatus));
        else if (type == typeof(LibraryAuditDto)) SetEnum(responseSchema, "status", typeof(AuditStatus));
        else if (type == typeof(EditionDto)) SetEnum(responseSchema, "isbnStatus", typeof(EditionIsbnStatus));
    }

    private static void SetEnum(OpenApiSchema schema, string propertyName, Type enumType)
    {
        if (schema.Properties is not { } properties || !properties.TryGetValue(propertyName, out var property) ||
            property is not OpenApiSchema propertySchema)
            return;
        propertySchema.Enum = Enum.GetNames(enumType).Select(name => (JsonNode)JsonValue.Create(name)!).ToList();
    }
}
