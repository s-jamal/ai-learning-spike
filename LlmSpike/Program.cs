using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using OpenAI.Embeddings;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var openAiClient = new AzureOpenAIClient(
    new Uri(config["AOAI_ENDPOINT"]!),
    new AzureKeyCredential(config["AOAI_KEY"]!));

var chat = openAiClient.GetChatClient("chat");
var embed = openAiClient.GetEmbeddingClient("embeddings");

string searchEndpoint = config["SEARCH_ENDPOINT"]!;
var searchCredential = new AzureKeyCredential(config["SEARCH_KEY"]!);
string indexName = "customs-chunks";

var indexClient = new SearchIndexClient(new Uri(searchEndpoint), searchCredential);
var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, searchCredential);


// ---------------------------------------------------------------
// 1. Index setup (unchanged from session 2)
// ---------------------------------------------------------------

var definition = new SearchIndex(indexName)
{
    Fields =
    {
        new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
        new SearchableField("content") { IsFilterable = false },
        new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
        {
            IsSearchable = true,
            VectorSearchDimensions = 1536,
            VectorSearchProfileName = "vector-profile"
        }
    },
    VectorSearch = new VectorSearch
    {
        Algorithms = { new HnswAlgorithmConfiguration("hnsw-config") },
        Profiles = { new VectorSearchProfile("vector-profile", "hnsw-config") }
    }
};

await indexClient.CreateOrUpdateIndexAsync(definition);

string document = @"
Customs Declaration CD-2024-08817. Importer of record: Nordwind Logistics LLC,
registered at 12 Neftchilar Avenue, Baku. Declaration submitted 14 March 2024
at the Baku Customs Post.

The consignment contains 480 units of stainless steel fittings, HS code 7307.29,
with a declared customs value of 62,400 USD. Country of origin is Turkiye.
Gross weight is 1,850 kg across 24 pallets.

Import duty is assessed at 5 percent of customs value, giving 3,120 USD.
Value added tax is applied at 18 percent on the sum of customs value and duty,
giving 11,793.60 USD. Total payable to customs is 14,913.60 USD.

Payment is due within 15 calendar days of declaration acceptance. Late payment
incurs a penalty of 0.1 percent of the outstanding amount per day.

Additional provisions. Goods may be inspected at the discretion of the customs
officer. Release is conditional on presentation of the original commercial
invoice and the certificate of origin.
";

var paragraphs = document.Split(
    new[] { "\r\n\r\n", "\n\n" },
    StringSplitOptions.RemoveEmptyEntries);

var chunks = new List<string>();
foreach (var paragraph in paragraphs)
{
    string cleaned = paragraph.Replace("\r\n", " ").Replace("\n", " ").Trim();
    if (cleaned.Length > 0) chunks.Add(cleaned);
}

var chunkEmbeddings = await embed.GenerateEmbeddingsAsync(chunks);

var documents = new List<SearchDocument>();
for (int i = 0; i < chunks.Count; i++)
{
    documents.Add(new SearchDocument
    {
        ["id"] = i.ToString(),
        ["content"] = chunks[i],
        ["contentVector"] = chunkEmbeddings.Value[i].ToFloats().ToArray()
    });
}

await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(documents));
await Task.Delay(3000);

Console.WriteLine($"Indexed {documents.Count} chunks.");


// ---------------------------------------------------------------
// 2. The C# methods behind the tools
// ---------------------------------------------------------------

static decimal CalculateLatePenalty(decimal amount, int daysLate)
{
    decimal dailyRate = 0.001m;
    return amount * dailyRate * daysLate;
}

static decimal ConvertCurrency(decimal amount, string toCurrency)
{
    decimal rate = toCurrency.ToUpperInvariant() switch
    {
        "EUR" => 0.92m,
        "AZN" => 1.70m,
        "GBP" => 0.79m,
        _ => throw new ArgumentException($"Unknown currency: {toCurrency}")
    };

    return amount * rate;
}

// Retrieval is now itself a tool the model can choose to call.
async Task<string> SearchDocuments(string query)
{
    var queryEmbedding = await embed.GenerateEmbeddingAsync(query);
    float[] queryVector = queryEmbedding.Value.ToFloats().ToArray();

    var vectorQuery = new VectorizedQuery(queryVector) { KNearestNeighborsCount = 3 };
    vectorQuery.Fields.Add("contentVector");

    var options = new SearchOptions { VectorSearch = new VectorSearchOptions() };
    options.VectorSearch.Queries.Add(vectorQuery);

    var response = await searchClient.SearchAsync<SearchDocument>(query, options);

    var builder = new StringBuilder();
    await foreach (var result in response.Value.GetResultsAsync())
    {
        builder.AppendLine($"- {result.Document["content"]}");
    }

    return builder.Length == 0 ? "No relevant content found." : builder.ToString();
}


// ---------------------------------------------------------------
// 3. Tool descriptions
// ---------------------------------------------------------------

var searchTool = ChatTool.CreateFunctionTool(
    functionName: "search_documents",
    functionDescription: "Searches the customs declaration documents. Use this to find "
                       + "any factual detail about a declaration: amounts, dates, HS codes, "
                       + "importer names, payment terms, penalty rates.",
    functionParameters: BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "What to look for in the documents"
            }
        },
        "required": ["query"]
    }
    """));

var penaltyTool = ChatTool.CreateFunctionTool(
    functionName: "calculate_late_penalty",
    functionDescription: "Calculates the late payment penalty on a customs amount. "
                       + "Use this whenever the user asks about penalties for late payment.",
    functionParameters: BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "amount":   { "type": "number",  "description": "The outstanding amount in USD" },
            "daysLate": { "type": "integer", "description": "How many days the payment is overdue" }
        },
        "required": ["amount", "daysLate"]
    }
    """));

var currencyTool = ChatTool.CreateFunctionTool(
    functionName: "convert_currency",
    functionDescription: "Converts an amount in USD to another currency. Supported: EUR, AZN, GBP.",
    functionParameters: BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "amount":     { "type": "number", "description": "The amount in USD" },
            "toCurrency": { "type": "string", "description": "Target currency code, for example EUR" }
        },
        "required": ["amount", "toCurrency"]
    }
    """));

var chatOptions = new ChatCompletionOptions();
chatOptions.Tools.Add(searchTool);
chatOptions.Tools.Add(penaltyTool);
chatOptions.Tools.Add(currencyTool);


// ---------------------------------------------------------------
// 4. Questions
// ---------------------------------------------------------------

await Ask("What is the total payable on this declaration?");
await Ask("What is the penalty if I pay this declaration 30 days late?");
await Ask("What is the total payable, in euros?");
await Ask("What is the capital of France?");


async Task Ask(string question)
{
    Console.WriteLine();
    Console.WriteLine($"=== {question} ===");

    var messages = new List<ChatMessage>
    {
        new SystemChatMessage(
            "You are a customs assistant. Use search_documents to find facts in the "
          + "declaration before answering. Use the calculation tools rather than doing "
          + "arithmetic yourself. Only answer questions about customs declarations and "
          + "payments; decline anything else."),
        new UserChatMessage(question)
    };

    while (true)
    {
        var completion = await chat.CompleteChatAsync(messages, chatOptions);
        var result = completion.Value;

        if (result.FinishReason != ChatFinishReason.ToolCalls)
        {
            Console.WriteLine("Answer: " + result.Content[0].Text);
            return;
        }

        messages.Add(new AssistantChatMessage(result));

        foreach (var toolCall in result.ToolCalls)
        {
            Console.WriteLine($"  [tool: {toolCall.FunctionName} {toolCall.FunctionArguments}]");

            string output = await RunTool(toolCall.FunctionName, toolCall.FunctionArguments);

            string preview = output.Length > 100 ? output.Substring(0, 100) + "..." : output;
            Console.WriteLine($"  [returned: {preview.Replace("\r\n", " ").Replace("\n", " ")}]");

            messages.Add(new ToolChatMessage(toolCall.Id, output));
        }
    }
}


async Task<string> RunTool(string name, BinaryData arguments)
{
    try
    {
        using var json = JsonDocument.Parse(arguments);
        var root = json.RootElement;

        if (name == "search_documents")
        {
            string query = root.GetProperty("query").GetString()!;
            return await SearchDocuments(query);
        }

        if (name == "calculate_late_penalty")
        {
            decimal amount = root.GetProperty("amount").GetDecimal();
            int daysLate = root.GetProperty("daysLate").GetInt32();
            return $"{CalculateLatePenalty(amount, daysLate):F2} USD";
        }

        if (name == "convert_currency")
        {
            decimal amount = root.GetProperty("amount").GetDecimal();
            string currency = root.GetProperty("toCurrency").GetString()!;
            return $"{ConvertCurrency(amount, currency):F2} {currency.ToUpperInvariant()}";
        }

        return $"Error: no such tool '{name}'";
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}


public partial class Program { }