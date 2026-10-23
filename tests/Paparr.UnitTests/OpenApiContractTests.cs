using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Paparr.API;
using Paparr.API.Domain;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Paparr.UnitTests;

public sealed class OpenApiContractTests
{
    [Fact]
    public void Response_nulls_are_explicit_and_request_validation_remains_distinct()
    {
        using var document = CreateDocument();
        var schemas = Schemas(document);

        AssertRequiredNullable(schemas.GetProperty("ImportJobDto"), "lastError");
        AssertRequiredNullable(schemas.GetProperty("ImportJobDto"), "acceptedBook");
        AssertRequiredNullable(schemas.GetProperty("DuplicateSourceDto"), "relativePath");
        AssertRequiredNullable(schemas.GetProperty("LatestLibraryAuditDto"), "audit");
        AssertRequiredNullable(schemas.GetProperty("WorkerDiagnosticsSnapshot"), "lastProgressAt");
        AssertRequiredNullable(schemas.GetProperty("WorkerDiagnosticsSnapshot"), "currentStage");

        var latestAudit = Property(schemas.GetProperty("LatestLibraryAuditDto"), "audit");
        Assert.True(References(latestAudit, "LibraryAuditDto"));

        var editRequest = schemas.GetProperty("EditMetadataRequest");
        AssertRequired(editRequest, "title");
        AssertRequired(editRequest, "author");
        Assert.False(Property(editRequest, "title").TryGetProperty("nullable", out var titleNullable) &&
            titleNullable.ValueKind == System.Text.Json.JsonValueKind.True);
        AssertStringBounds(Property(editRequest, "title"), 1, 300);
        AssertStringBounds(Property(editRequest, "author"), 1, 300);

        var login = schemas.GetProperty("LoginRequest");
        AssertRequired(login, "password");
        AssertStringBounds(Property(login, "password"), 0, 512);

        var editionRequest = schemas.GetProperty("EditionInputDto");
        AssertStringBounds(Property(editionRequest, "isbn"), 0, 64);
        AssertStringBounds(Property(editionRequest, "publisher"), 0, 300);
        AssertStringBounds(Property(editionRequest, "language"), 0, 35);
        Assert.DoesNotContain("isbn", RequiredNames(editionRequest));
        Assert.DoesNotContain("edition", RequiredNames(editRequest));
    }

    [Fact]
    public void Import_duplicate_library_and_worker_responses_have_usable_typed_schemas()
    {
        using var document = CreateDocument();
        AssertPageResponse(document, "/api/imports", "ImportJobDto");
        AssertPageResponse(document, "/api/imports/{id}/duplicates", "DuplicateSourceDto");
        AssertPageResponse(document, "/api/library", "LibraryBookDto");

        var latestAuditResponse = GetResponseSchema(document, "/api/library/audits/latest", "get");
        Assert.True(References(latestAuditResponse, "LatestLibraryAuditDto"));
        var workerResponse = GetResponseSchema(document, "/api/diagnostics/worker", "get");
        Assert.True(References(workerResponse, "WorkerDiagnosticsSnapshot"));
    }

    [Fact]
    public void Response_schemas_do_not_publish_server_filesystem_paths_or_hashes()
    {
        using var document = CreateDocument();
        var schemas = Schemas(document);
        var privateProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "filePath", "sourcePath", "destinationPath", "libraryPath", "fileHash", "hash"
        };

        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties)) continue;
            foreach (var property in properties.EnumerateObject())
                Assert.False(privateProperties.Contains(property.Name),
                    $"The {schema.Name} schema exposes the private property '{property.Name}'.");
        }
    }

    [Fact]
    public void Response_status_properties_publish_their_exact_domain_enum_values()
    {
        using var document = CreateDocument();
        var schemas = Schemas(document);

        AssertEnumValues(schemas.GetProperty("ImportJobDto"), "status", Enum.GetNames<ImportStatus>());
        AssertEnumValues(schemas.GetProperty("SourceCleanupDto"), "status", Enum.GetNames<SourceCleanupStatus>());
        AssertEnumValues(schemas.GetProperty("LibraryBookDto"), "integrityStatus", Enum.GetNames<IntegrityStatus>());
        AssertEnumValues(schemas.GetProperty("LibraryAuditDto"), "status", Enum.GetNames<AuditStatus>());
        AssertEnumValues(schemas.GetProperty("EditionDto"), "isbnStatus", Enum.GetNames<EditionIsbnStatus>());
    }

    private static JsonDocument CreateDocument()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SupportNonNullableReferenceTypes();
            options.NonNullableReferenceTypesAsRequired();
            options.UseAllOfToExtendReferenceSchemas();
            options.SchemaFilter<ResponseSchemaFilter>();
            options.SchemaFilter<RequestSchemaFilter>();
        });

        using var provider = builder.Services.BuildServiceProvider();
        var document = provider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var text = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(text));
        return JsonDocument.Parse(text.ToString());
    }

    private static JsonElement Schemas(JsonDocument document) =>
        document.RootElement.GetProperty("components").GetProperty("schemas");

    private static void AssertPageResponse(JsonDocument document, string path, string itemType)
    {
        var page = Resolve(document, GetResponseSchema(document, path, "get"));
        Assert.Equal("object", page.GetProperty("type").GetString());
        foreach (var field in new[] { "items", "total", "page", "pageSize" })
            AssertRequired(page, field);
        var items = Property(page, "items");
        Assert.Equal("array", items.GetProperty("type").GetString());
        var item = Resolve(document, items.GetProperty("items"));
        Assert.Equal("object", item.GetProperty("type").GetString());
        Assert.True(item.GetProperty("properties").TryGetProperty("id", out _));
        Assert.Equal(itemType, ReferenceName(items.GetProperty("items")));
    }

    private static JsonElement GetResponseSchema(JsonDocument document, string path, string method)
    {
        var response = document.RootElement.GetProperty("paths").GetProperty(path)
            .GetProperty(method).GetProperty("responses").GetProperty("200");
        return response.GetProperty("content").GetProperty("application/json").GetProperty("schema");
    }

    private static JsonElement Resolve(JsonDocument document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;
        var name = reference.GetString()!.Split('/').Last();
        return Schemas(document).GetProperty(name);
    }

    private static string? ReferenceName(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference)) return reference.GetString()!.Split('/').Last();
        if (schema.TryGetProperty("allOf", out var allOf))
            foreach (var item in allOf.EnumerateArray())
                if (ReferenceName(item) is { } name) return name;
        return null;
    }

    private static bool References(JsonElement schema, string typeName) =>
        ReferenceName(schema) == typeName;

    private static JsonElement Property(JsonElement schema, string name) =>
        schema.GetProperty("properties").GetProperty(name);

    private static void AssertRequiredNullable(JsonElement schema, string name)
    {
        AssertRequired(schema, name);
        Assert.True(Property(schema, name).GetProperty("nullable").GetBoolean(),
            $"Expected response property '{name}' to allow null.");
    }

    private static void AssertRequired(JsonElement schema, string name) =>
        Assert.Contains(name, RequiredNames(schema));

    private static IEnumerable<string?> RequiredNames(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(item => item.GetString())
            : [];

    private static void AssertStringBounds(JsonElement schema, int minimum, int maximum)
    {
        Assert.Equal(minimum, schema.GetProperty("minLength").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maxLength").GetInt32());
    }

    private static void AssertEnumValues(JsonElement schema, string propertyName, IEnumerable<string> expected)
    {
        var values = Property(schema, propertyName).GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString());
        Assert.Equal(expected, values);
    }
}
