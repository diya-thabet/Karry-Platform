using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Karry.Host;

/// <summary>
/// The committed contract (<c>openapi/v1.json</c>) must not depend on the machine that builds it: the XML comments
/// behind its descriptions come with <c>\r\n</c> line ends on Windows and <c>\n</c> on Linux (CI), so every
/// description is written with <c>\n</c>.
/// </summary>
internal static class OpenApiLineEnds
{
    public static OpenApiOptions AddPortableLineEnds(this OpenApiOptions options) =>
        options.AddDocumentTransformer((document, _, _) =>
        {
            foreach (var schema in document.Components?.Schemas?.Values ?? Enumerable.Empty<IOpenApiSchema>())
            {
                Normalize(schema, []);
            }

            var paths = document.Paths?.Values ?? Enumerable.Empty<IOpenApiPathItem>();
            foreach (var operation in paths.SelectMany(p => p.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
            {
                operation.Summary = Lf(operation.Summary);
                operation.Description = Lf(operation.Description);
                foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
                {
                    parameter.Description = Lf(parameter.Description);
                }

                if (operation.RequestBody is OpenApiRequestBody body)
                {
                    body.Description = Lf(body.Description);
                }

                foreach (var response in (operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>()).OfType<OpenApiResponse>())
                {
                    response.Description = Lf(response.Description);
                }
            }

            return Task.CompletedTask;
        });

    private static void Normalize(IOpenApiSchema schema, HashSet<OpenApiSchema> seen)
    {
        if (schema is not OpenApiSchema target || !seen.Add(target))
        {
            return;
        }

        target.Description = Lf(target.Description);
        foreach (var child in (target.Properties?.Values ?? []).Concat(target.AllOf ?? []).Concat(target.AnyOf ?? []).Concat(target.OneOf ?? []))
        {
            Normalize(child, seen);
        }

        if (target.Items is { } items)
        {
            Normalize(items, seen);
        }

        if (target.AdditionalProperties is { } additional)
        {
            Normalize(additional, seen);
        }
    }

    private static string? Lf(string? text) => text?.Replace("\r\n", "\n", StringComparison.Ordinal);
}
