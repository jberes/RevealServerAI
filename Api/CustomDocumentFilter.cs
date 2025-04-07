using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public class CustomDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // List of tags to filter out
        var tagsToFilterOut = new[] {"Dashboard", "Credentials", "DataSourceItems", "DataSources", "ExportTools", "SdkReveal", "SdkTools", "DashboardFile", };
        var pathsToKeep = swaggerDoc.Paths
            .Where(path =>
            {
                var filteredOperations = path.Value.Operations
                    .Where(operation =>
                    {
                        var tags = operation.Value.Tags?.Select(t => t.Name);
                        return tags == null || !tags.Any(tag => tagsToFilterOut.Contains(tag));
                    })
                    .ToDictionary(op => op.Key, op => op.Value);

                if (filteredOperations.Any())
                {
                    path.Value.Operations = filteredOperations;
                    return true;
                }

                return false;
            })
            .ToDictionary(path => path.Key, path => path.Value);

        swaggerDoc.Paths = new OpenApiPaths();
        foreach (var path in pathsToKeep)
        {
            swaggerDoc.Paths.Add(path.Key, path.Value);
        }
    }
}