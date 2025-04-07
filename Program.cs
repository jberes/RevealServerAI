using Microsoft.Data.SqlClient;
using System.Data;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using Reveal.Sdk.Dom.Data;
using Reveal.Sdk.Dom.Visualizations;
using Reveal.Sdk.Dom;
using Reveal.Sdk;
using RevealSdk.Server.Reveal;
using Reveal.Sdk.Data;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataTable = System.Data.DataTable;
using DataColumn = System.Data.DataColumn;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("AzureSqlConnection");

builder.Services.AddControllers().AddReveal();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.DocumentFilter<CustomDocumentFilter>();
});

builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<SchemaStateService>();

var connectionSettings = builder.Configuration
    .GetSection("ConnectionSettings")
    .Get<ConnectionSettings>();

builder.Services.AddSingleton(connectionSettings);

var kernel = Kernel.CreateBuilder()
    .AddOpenAIChatCompletion("gpt-4o-mini", connectionSettings.ChatGptKey)
    .Build();

builder.Services.AddControllers().AddReveal(builder =>
{
    builder
        .AddAuthenticationProvider<AuthenticationProvider>()
        .AddDataSourceProvider<RevealSdk.Server.Reveal.DataSourceProvider>()
        .DataSources.RegisterMicrosoftSqlServer();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
      builder => builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
    );
});

var app = builder.Build();
app.UseCors("AllowAll");
app.UseRouting();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/results", async (string input) =>
{
    string schemaFilePath = Path.Combine(Environment.CurrentDirectory, "schemas", "schema.json");
    if (!File.Exists(schemaFilePath))
    {
        return Results.Problem("Schema file not found.", statusCode: 500);
    }
    string schemaContent = await File.ReadAllTextAsync(schemaFilePath);

    var prompt = $@"
    The following is the database schema:

    {schemaContent}

    Based on the above schema, generate a T-SQL statement for the following question: {input}.

    Rules:
    1. Use only the tables, fields, and relationships described in the schema.
    2. Do not invent or assume additional fields, tables, or relationships.
    3. Use meaningful column aliases and descriptive names for aggregations.
    4. Do not include any explanations or non-SQL text in the response.
    5. End the query with a semicolon.
    6. At the end of the T-SQL Statement, add a useful title prefixed by ~~~, based on the input question, that I can use for my chart.

    ## Rules ##
    - Avoid using query hints such as `MAXDOP`, `OPTION`, or server-specific optimizations.
    - Use table aliases for clarity.
    - Qualify column names to avoid ambiguity.
    - Use descriptive names for aggregated results and group data as needed.
    - Do not include explanations or non-SQL text.
    - Do not include `ORDER BY` clauses.
    - Do not include `WITH ROLLUP` statements.
    ";

    var skFunctions = kernel.CreateFunctionFromPrompt(prompt, new OpenAIPromptExecutionSettings()
    {
        Temperature = 0.5,
        MaxTokens = 2000,
    });

    var kernelArguments = new KernelArguments {
        {"input", input}
    };

    var functionResult = await kernel.InvokeAsync(skFunctions, kernelArguments);

    var result = functionResult.GetValue<string>();

    result = result.Replace("sql", "").Replace("```", "").Replace("\n", " ").Replace("Order_Details", "[Order Details]");

    var splitIndex = result.IndexOf("~~~");
    string sqlQuery, visualizationTitle;

    if (splitIndex >= 0)
    {
        sqlQuery = result.Substring(0, splitIndex).Trim();
        visualizationTitle = result.Substring(splitIndex + 3).Trim();
    }
    else
    {
        sqlQuery = result.Trim();
        visualizationTitle = "Default Title";
    }

    sqlQuery = StripOrderBy(sqlQuery);

    QueryStore.SqlQuery = sqlQuery;

    var dataTable = GetData(sqlQuery);
    var dataList = ConvertDataTableToList(dataTable);
    var chartDetails = dataList.Any() ? await GetChartTypeInfoAsync(sqlQuery, kernel) : null;
    var insights = await GetDataInsightsAsync(dataTable, kernel);

    if (chartDetails != null)
    {
        chartDetails.Description = visualizationTitle.Replace("~", "");
    }

    var response = new JsonResponse
    {
        OriginalQuery = StripOrderBy(sqlQuery),
        Data = dataList,
        ChartDetails = chartDetails,  
        Insights = insights,
        NewQuery = string.Empty
    };

    var document = CreateDashboard(dataTable, chartDetails, input);
    string _saveRdashToPath = Path.Combine(Environment.CurrentDirectory, "Dashboards", chartDetails.Description +  ".rdash");
    document.Save(_saveRdashToPath);
    return Results.Json(response);
});


static string StripOrderBy(string sqlQuery)
{
    if (string.IsNullOrWhiteSpace(sqlQuery))
        return sqlQuery;
    var orderByRegex = new Regex(@"\bORDER\s+BY\b.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    var strippedQuery = orderByRegex.Replace(sqlQuery, string.Empty);
    return strippedQuery.TrimEnd() + ";";
}

DataTable GetData(string query, Dictionary<string, object> parameters = null)
{
    using (SqlConnection connection = new SqlConnection(connectionString))
    {
        SqlCommand command = new SqlCommand(query, connection);

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
            }
        }

        SqlDataAdapter adapter = new SqlDataAdapter(command);
        DataTable table = new DataTable();
        try
        {
            connection.Open();
            adapter.Fill(table);
        }
        catch (SqlException ex)
        {
            Console.WriteLine($"SQL Error: {ex.Message}");
        }
        return table;
    }
}

static List<Dictionary<string, object>> ConvertDataTableToList(DataTable dataTable)
{
    var dataList = new List<Dictionary<string, object>>();
    foreach (DataRow row in dataTable.Rows)
    {
        var rowDict = new Dictionary<string, object>();
        foreach (DataColumn col in dataTable.Columns)
        {
            rowDict[col.ColumnName] = row[col];
        }
        dataList.Add(rowDict);
    }
    return dataList;
}


static async Task<ChartDetails> GetChartTypeInfoAsync(string sqlQuery, Kernel _kernel)
{
    string prompt =
    "Analyze the following SQL query to suggest a suitable Chart Type, X Axes (as a list), and Y Axes (as a list) in JSON format. " +
    "The suggestions should follow these rules: " +
    "1. Chart types should be one of: table, bar, column, pie, text (for single values), funnel, doughnut. " +
    "2. If there are categorical fields (e.g., names or IDs), prefer using them for the X-axis. Prioritize more descriptive fields like names over IDs. " +
    "3. Numerical or aggregated fields (e.g., sums or counts) should be used for the Y-axis. " +
    "4. Provide suggestions even if there are multiple possible configurations. " +
    "5. Use generic field names if possible (e.g., 'Category' for descriptive fields and 'Value' for numerical fields). " +
    "I don't need any explanation, just the JSON result: " + sqlQuery;

    var executionSettings = new OpenAIPromptExecutionSettings
    {
        Temperature = 0.7,
        MaxTokens = 1000, 
    };

    var skFunctions = _kernel.CreateFunctionFromPrompt(prompt, executionSettings);

    var kernelArguments = new KernelArguments
    {
        { "input", sqlQuery }
    };

    var functionResult = await _kernel.InvokeAsync(skFunctions, kernelArguments);
    var jsonResult = functionResult.GetValue<string>();

    Console.WriteLine("Raw JSON response: " + jsonResult);

    jsonResult = jsonResult.Trim();
    if (jsonResult.StartsWith("```"))
    {
        jsonResult = jsonResult.Substring(3);
    }
    if (jsonResult.EndsWith("```"))
    {
        jsonResult = jsonResult.Substring(0, jsonResult.Length - 3);
    }

    if (jsonResult.StartsWith("json", StringComparison.OrdinalIgnoreCase))
    {
        jsonResult = jsonResult.Substring(4).Trim();
    }

    if (!jsonResult.StartsWith("{"))
    {
        throw new InvalidOperationException("The response is not a valid JSON object: " + jsonResult);
    }

    try
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var chartDetails = JsonSerializer.Deserialize<ChartDetails>(jsonResult, options);
        return chartDetails;
    }
    catch (JsonException ex)
    {
        throw new InvalidOperationException("Failed to deserialize the JSON response: " + jsonResult, ex);
    }
}

static async Task<string> GetDataInsightsAsync(DataTable dataTable, Kernel _kernel)
{
    var dataList = ConvertDataTableToList(dataTable);
    string jsonData = JsonSerializer.Serialize(dataList);

    string prompt = $"Can you give me very brief data insights in markdown format, " +
        $"with a brief text-based explanation, " +
        $"including outliers or " +
        $"other statistics (like Average, or percentage increase or decrease if it's notable) " +
        $"that might be useful from this JSON list: {jsonData} - " +
        $"it is important to return the markdown in a natural, short, brief and direct language response, " +
        $"--- RULES --- " +
        $"1 - do not use the word 'Data Insights'" +
        $"2 - do not include a Title" +
        $"3 - do not table data" +
        $"4 - do not include any math calculations" + 
        $"5 -  do not include any AI-generated content";


    //$"4 - do not wrap in ``` markdown ```" + 

    var executionSettings = new OpenAIPromptExecutionSettings
    {
        Temperature = 0.7,
        MaxTokens = 400, 
    };

    var skFunctions = _kernel.CreateFunctionFromPrompt(prompt, executionSettings);

    var kernelArguments = new KernelArguments
    {
        { "input", prompt }
    };

    var functionResult = await _kernel.InvokeAsync(skFunctions, kernelArguments);
    var jsonResult = functionResult.GetValue<string>();

    return jsonResult;
}


static RdashDocument CreateDashboard(DataTable dataTable, ChartDetails chartDetails, string originalQuestion)
{

    if (chartDetails == null)
    {
        throw new ArgumentNullException(nameof(chartDetails), "chartDetails cannot be null.");
    }

    if (string.IsNullOrEmpty(chartDetails.ChartType))
    {
        throw new InvalidOperationException($"ChartType is null or empty. JSON data: {JsonSerializer.Serialize(chartDetails)}");
    }

    var document = new RdashDocument("My Dashboard");

        var sqlServerDS = new MicrosoftSqlServerDataSource()
        {
            Title = "Northwind Cloud",
            Subtitle = "Northwind Cloud Subtitle",
            Id = "NorthwindCloud"
        };

        var fields = new List<IField>();
        foreach (DataColumn column in dataTable.Columns)
        {
            fields.Add(new TextField(column.ColumnName) { FieldLabel = column.ColumnName });
        }

        var dataSourceItem = new MicrosoftSqlServerDataSourceItem("Data Source Item", sqlServerDS)
        {
            Id = chartDetails.Description,
            Subtitle = "SQL Server Data Source Item",
            Fields = fields
        };


    IVisualization visualization;

    var visualizationTitle = $"{chartDetails.Description}";


    switch (chartDetails.ChartType.ToLower())
    {
        case "column":
        case "column chart":
            visualization = new ColumnChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabels(chartDetails.XAxes.ToArray())
                .SetValues(chartDetails.YAxes.ToArray());
            break;

        case "bar":
        case "bar chart":
            visualization = new BarChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabels(chartDetails.XAxes.ToArray())
                .SetValues(chartDetails.YAxes.ToArray());
            break;

        case "line":
        case "line chart":
            visualization = new LineChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabels(chartDetails.XAxes.ToArray())
                .SetValues(chartDetails.YAxes.ToArray());
            break;

        case "stacked area":
        case "stacked area chart":
            visualization = new StackedAreaChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabels(chartDetails.XAxes.ToArray())
                .SetValues(chartDetails.YAxes.ToArray());
            break;

        case "pie":
        case "pie chart":
            visualization = new PieChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabel(chartDetails.XAxes.FirstOrDefault() ?? string.Empty)
                .SetValue(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .ConfigureSettings(settings =>
                {
                    settings.SliceLabelDisplay = LabelDisplayMode.Value;
                });
            break;

        case "doughnut":
        case "doughnut chart":
            visualization = new DoughnutChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabel(chartDetails.XAxes.FirstOrDefault() ?? string.Empty)
                .SetValue(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .ConfigureSettings(settings =>
                {
                    settings.SliceLabelDisplay = LabelDisplayMode.ValueAndPercentage;
                });
            break;

        case "funnel":
        case "funnel chart":
            visualization = new FunnelChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabel(chartDetails.XAxes.FirstOrDefault() ?? string.Empty)
                .SetValue(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .ConfigureSettings(settings =>
                {
                    settings.SliceLabelDisplay = LabelDisplayMode.Percentage;
                });
            break;

        case "text":
        case "single value":
        case "single value chart":
            visualization = new TextVisualization(visualizationTitle, dataSourceItem)
                .SetValue(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .ConfigureSettings(settings =>
                {
                    settings.ConditionalFormattingEnabled = true;
                    settings.UpperBand.Shape = ShapeType.ArrowUp;
                    settings.MiddleBand.Shape = ShapeType.Dash;
                    settings.LowerBand.Shape = ShapeType.ArrowDown;
                });
            break;

        case "combo":
        case "combo chart":
            visualization = new ComboChartVisualization(visualizationTitle, dataSourceItem)
                .SetLabel(new DateDataField(chartDetails.XAxes.FirstOrDefault() ?? "Date") { AggregationType = DateAggregationType.Month })
                .SetChart1Value(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .SetChart2Value(chartDetails.YAxes.ElementAtOrDefault(1) ?? string.Empty)
                .ConfigureSettings(settings =>
                {
                    settings.Chart1Type = ComboChartType.Column;
                    settings.Chart2Type = ComboChartType.Line;
                });
            break;

        case "bubble":
        case "bubble chart":
            visualization = new BubbleVisualization(visualizationTitle, dataSourceItem)
                .SetLabel(chartDetails.XAxes.FirstOrDefault() ?? string.Empty)
                .SetXAxis(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .SetYAxis(chartDetails.YAxes.ElementAtOrDefault(1) ?? string.Empty)
                .SetRadius(chartDetails.YAxes.ElementAtOrDefault(2) ?? string.Empty);
            break;

        case "scatter":
        case "scatter chart":
            visualization = new ScatterVisualization(visualizationTitle, dataSourceItem)
                .SetLabel(chartDetails.XAxes.FirstOrDefault() ?? string.Empty)
                .SetXAxis(chartDetails.YAxes.FirstOrDefault() ?? string.Empty)
                .SetYAxis(chartDetails.YAxes.ElementAtOrDefault(1) ?? string.Empty);
            break;

        case "grid":
        case "table":
            if ((chartDetails.XAxes == null || !chartDetails.XAxes.Any()) &&
                (chartDetails.YAxes == null || !chartDetails.YAxes.Any()))
            {
                throw new InvalidOperationException("Grid visualization requires at least one column (XAxes or YAxes) to be defined.");
            }

            var combinedAxes = new List<string>();
            if (chartDetails.XAxes != null)
            {
                combinedAxes.AddRange(chartDetails.XAxes);
            }
            if (chartDetails.YAxes != null)
            {
                combinedAxes.AddRange(chartDetails.YAxes);
            }

            visualization = new GridVisualization(visualizationTitle, dataSourceItem)
                .SetColumns(combinedAxes.ToArray());
            break;

        default:
            throw new InvalidOperationException($"Unsupported chart type: {chartDetails.ChartType}");
    }

    document.Visualizations.Add(visualization);
    return document;
}

app.MapPost("/executeQuery", async (QueryRequest request) =>
{
    using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    using (var command = new SqlCommand(request.SqlQuery, connection))
    {
        using (var reader = await command.ExecuteReaderAsync())
        {
            var results = new List<Dictionary<string, object>>();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.GetValue(i);
                }
                results.Add(row);
            }
            return Results.Ok(results);
        }
    }
})
.WithName("ExecuteQuery")
.WithOpenApi();

app.Run();

public class ChartDetails
{
    [JsonPropertyName("chartType")]
    public string ChartType { get; set; }

    [JsonPropertyName("xAxes")]
    [JsonConverter(typeof(SingleOrListConverter))]
    public List<string> XAxes { get; set; } = new();

    [JsonPropertyName("yAxes")]
    [JsonConverter(typeof(SingleOrListConverter))]
    public List<string> YAxes { get; set; } = new();

    public string Description { get; set; }
    public string Title { get; set; }
}

public class SingleOrListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new List<string>();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new List<string> { reader.GetString()! };
        }

        throw new JsonException("Expected string or array.");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

public class JsonResponse
{
    public  string OriginalQuery { get; set; }
    public  List<Dictionary<string, object>> Data { get; set; }
    public  ChartDetails ChartDetails { get; set; }
    public  object Insights { get; set; }
    public  string NewQuery { get; set; }
}

public class QueryRequest
{
    public string SqlQuery { get; set; } = string.Empty;
}

public static class QueryStore
{
    public static string SqlQuery { get; set; }
}

public class SchemaStateService
{
    public bool IsSchemaInitialized { get; private set; } = false;
    public string CachedSchema { get; private set; }

    public async Task InitializeSchemaAsync(string schemaFilePath)
    {
        if (IsSchemaInitialized) return;

        if (!File.Exists(schemaFilePath))
        {
            throw new FileNotFoundException("Schema file not found.");
        }

        CachedSchema = await File.ReadAllTextAsync(schemaFilePath);
        IsSchemaInitialized = true;
    }
}

public class ConnectionSettings
{
    public required string Host { get; set; }
    public required string Database { get; set; }
    public required string DatabaseUserName { get; set; }
    public required string DatabasePassword { get; set; }
    public required string ChatGptKey { get; set; }
}
