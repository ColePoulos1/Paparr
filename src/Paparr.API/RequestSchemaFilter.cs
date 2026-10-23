using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Paparr.API;

public sealed class RequestSchemaFilter : ISchemaFilter
{
    private static readonly HashSet<Type> RequestTypes =
    [
        typeof(Controllers.LoginRequest),
        typeof(Models.EditMetadataRequest),
        typeof(Models.EditionInputDto)
    ];

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema request || !RequestTypes.Contains(context.Type)) return;

        var constructor = context.Type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null || request.Properties is null) return;

        foreach (var parameter in constructor.GetParameters())
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(parameter.Name ?? string.Empty);
            if (!request.Properties.TryGetValue(name, out var propertySchema) || propertySchema is not OpenApiSchema property)
                continue;

            var propertyInfo = context.Type.GetProperty(parameter.Name!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            var attributes = parameter.GetCustomAttributes<ValidationAttribute>(inherit: true)
                .Concat(propertyInfo?.GetCustomAttributes<ValidationAttribute>(inherit: true) ?? []);

            foreach (var attribute in attributes)
            {
                switch (attribute)
                {
                    case RequiredAttribute:
                        request.Required ??= new HashSet<string>();
                        request.Required.Add(name);
                        break;
                    case StringLengthAttribute length:
                        property.MinLength = length.MinimumLength;
                        property.MaxLength = length.MaximumLength;
                        break;
                    case MinLengthAttribute minimum:
                        property.MinLength = minimum.Length;
                        break;
                    case MaxLengthAttribute maximum:
                        property.MaxLength = maximum.Length;
                        break;
                    case RangeAttribute range:
                        property.Minimum = Convert.ToString(range.Minimum, CultureInfo.InvariantCulture);
                        property.Maximum = Convert.ToString(range.Maximum, CultureInfo.InvariantCulture);
                        break;
                }
            }
        }
    }
}
