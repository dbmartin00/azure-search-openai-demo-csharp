// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Microsoft.ApplicationInsights;
using Microsoft.FeatureManagement;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;
using System.Text.RegularExpressions;

namespace MinimalApi.Services;
#pragma warning disable SKEXP0011 // Mark members as static
#pragma warning disable SKEXP0001 // Mark members as static
public class ReadRetrieveReadChatService
{

    public class ChatProperties
    {
        public double Temperature { get; set; }
        public string? ModelId { get; set; }
        public int MaxTokens { get; set; }

        public override string ToString()
        {
            return $"temperature: {Temperature} modelid: {ModelId} max_tokens: {MaxTokens}";
        }
    }

    private readonly ISearchService _searchClient;
    private readonly Kernel _kernel;
    private readonly IConfiguration _configuration;
    private readonly IComputerVisionService? _visionService;
    private readonly TokenCredential? _tokenCredential;
    private readonly IVariantFeatureManagerSnapshot _snapshot;

    private readonly TelemetryClient _telemetryClient;

    public ReadRetrieveReadChatService(
        TelemetryClient telemetryClient,
        IVariantFeatureManagerSnapshot snapshot,
        ISearchService searchClient,
        OpenAIClient client,
        IConfiguration configuration,
        IComputerVisionService? visionService = null,
        TokenCredential? tokenCredential = null
        )
    {
        _telemetryClient = telemetryClient;

        _searchClient = searchClient;
        _snapshot = snapshot;
        var kernelBuilder = Kernel.CreateBuilder();

        if (configuration["UseAOAI"] == "false")
        {
            var deployment = configuration["OpenAiChatGptDeployment"];
            ArgumentNullException.ThrowIfNullOrWhiteSpace(deployment);
            kernelBuilder = kernelBuilder.AddOpenAIChatCompletion(deployment, client);

            var embeddingModelName = configuration["OpenAiEmbeddingDeployment"];
            ArgumentNullException.ThrowIfNullOrWhiteSpace(embeddingModelName);
            kernelBuilder = kernelBuilder.AddOpenAITextEmbeddingGeneration(embeddingModelName, client);
        }
        else
        {
            var deployedModelName = configuration["AzureOpenAiChatGptDeployment"];
            ArgumentNullException.ThrowIfNullOrWhiteSpace(deployedModelName);
            var embeddingModelName = configuration["AzureOpenAiEmbeddingDeployment"];
            if (!string.IsNullOrEmpty(embeddingModelName))
            {
                var endpoint = configuration["AzureOpenAiServiceEndpoint"];
                ArgumentNullException.ThrowIfNullOrWhiteSpace(endpoint);
                kernelBuilder = kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(embeddingModelName, endpoint, tokenCredential ?? new DefaultAzureCredential());
                kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(deployedModelName, endpoint, tokenCredential ?? new DefaultAzureCredential());
            }
        }

        _kernel = kernelBuilder.Build();
        _configuration = configuration;
        _visionService = visionService;
        _tokenCredential = tokenCredential;
    }

    public static string HealJson(string json)
    {   
        string result = json.Trim();
        result = result.Replace("\r\n", " ");
        result = result.Replace("\n", " ");

        // Check if the JSON ends with a closing quote and brace
        if (!result.TrimEnd().EndsWith("\"}") 
                // && !result.TrimEnd().EndsWith("\" }")
                && !Regex.IsMatch(result, "\"\\s*}$")
                && !Regex.IsMatch(result, "\"\\s*]$"))
        {
            // Check for missing closing quote
            if (!result.TrimEnd().EndsWith("\""))
            {
                result += "\"";
            }
            
            // Check for missing closing brace
            if (result.TrimStart().StartsWith("{") && !result.TrimEnd().EndsWith("}"))
            {
                result += "}";
            } else if (result.TrimStart().StartsWith("[") && !result.TrimEnd().EndsWith("]")) {
                result += "]";
            }
        }
        
        return result;
    }

    public async Task<ApproachResponse> ReplyAsync(
        ChatTurn[] history,
        RequestOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        var top = overrides?.Top ?? 3;
        var useSemanticCaptions = overrides?.SemanticCaptions ?? false;
        var useSemanticRanker = overrides?.SemanticRanker ?? false;
        var excludeCategory = overrides?.ExcludeCategory ?? null;
        var filter = excludeCategory is null ? null : $"category ne '{excludeCategory}'";
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var embedding = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();
        float[]? embeddings = null;
        var question = history.LastOrDefault()?.User is { } userQuestion
            ? userQuestion
            : throw new InvalidOperationException("Use question is null");
        if (overrides?.RetrievalMode != RetrievalMode.Text && embedding is not null)
        {
            embeddings = (await embedding.GenerateEmbeddingAsync(question, cancellationToken: cancellationToken)).ToArray();
        }

        // step 1
        // use llm to get query if retrieval mode is not vector
        string? query = null;
        if (overrides?.RetrievalMode != RetrievalMode.Vector)
        {
            var getQueryChat = new ChatHistory(@"You are a helpful AI assistant, generate search query for followup question.
Make your respond simple and precise. Return the query only, do not return any other text.
e.g.
Northwind Health Plus AND standard plan.
standard plan AND dental AND employee benefit.
");

            getQueryChat.AddUserMessage(question);
            var result = await chat.GetChatMessageContentAsync(
                getQueryChat,
                cancellationToken: cancellationToken);

            query = result.Content ?? throw new InvalidOperationException("Failed to get search query");
        }

        // step 2
        // use query to search related docs
        var documentContentList = await _searchClient.QueryDocumentsAsync(query, embeddings, overrides, cancellationToken);

        string documentContents = string.Empty;
        if (documentContentList.Length == 0)
        {
            documentContents = "no source available.";
        }
        else
        {
            documentContents = string.Join("\r", documentContentList.Select(x =>$"{x.Title}:{x.Content}"));
        }

        // step 2.5
        // retrieve images if _visionService is available
        SupportingImageRecord[]? images = default;
        if (_visionService is not null)
        {
            var queryEmbeddings = await _visionService.VectorizeTextAsync(query ?? question, cancellationToken);
            images = await _searchClient.QueryImagesAsync(query, queryEmbeddings.vector, overrides, cancellationToken);
        }

        // step 3
        // put together related docs and conversation history to generate answer
        var answerChat = new ChatHistory(
            "You are a system assistant who helps the company employees with their questions. Be brief in your answers");

        // add chat history
        foreach (var turn in history)
        {
            answerChat.AddUserMessage(turn.User);
            if (turn.Bot is { } botMessage)
            {
                answerChat.AddAssistantMessage(botMessage);
            }
        }

        
        if (images != null)
        {
            var prompt = @$"## Source ##
{documentContents}
## End ##

Answer question based on available source and images.
Your answer needs to be a json object with answer and thoughts field.
Don't put your answer between ```json and ```, return the json string directly. e.g {{""answer"": ""I don't know"", ""thoughts"": ""I don't know""}}";

            var tokenRequestContext = new TokenRequestContext(new[] { "https://storage.azure.com/.default" });
            var sasToken = await (_tokenCredential?.GetTokenAsync(tokenRequestContext, cancellationToken) ?? throw new InvalidOperationException("Failed to get token"));
            var sasTokenString = sasToken.Token;
            var imageUrls = images.Select(x => $"{x.Url}?{sasTokenString}").ToArray();
            var collection = new ChatMessageContentItemCollection();
            collection.Add(new TextContent(prompt));
            foreach (var imageUrl in imageUrls)
            {
                collection.Add(new ImageContent(new Uri(imageUrl)));
            }

            answerChat.AddUserMessage(collection);
        }
        else
        {
            var prompt = @$" ## Source ##
{documentContents}
## End ##

You answer needs to be a json object with the following format.
{{
    ""answer"": // the answer to the question, add a source reference to the end of each sentence. e.g. Apple is a fruit [reference1.pdf][reference2.pdf]. If no source available, put the answer as I don't know.
    ""thoughts"": // brief thoughts on how you came up with the answer, e.g. what sources you used, what you thought about, etc.
}}";
            answerChat.AddUserMessage(prompt);
        }

        var promptExecutingSetting = new OpenAIPromptExecutionSettings
        {
            MaxTokens = 1024,
            Temperature = overrides?.Temperature ?? 0.7,
            StopSequences = [],
        };

        Variant variant = await _snapshot!.GetVariantAsync("chat_properties", cancellationToken);
        string v = variant.Configuration.Get<string>() ?? "control";
        bool ChatPropertiesEnabled = string.Equals(v, "On");

        variant = await _snapshot!.GetVariantAsync("answer_prefix", cancellationToken);
        v = variant.Configuration.Get<string>() ?? "control";
        bool AnswerPrefixEnabled = string.Equals(v, "On");

        var appConfiguration = new ConfigurationBuilder()
            .AddAzureAppConfiguration("Endpoint=https://split-azure-experimentation-demo.azconfig.io;Id=EAg/;Secret=OlizPliX4JdgsxbM6MM97uf7ZzUrCJQOQAYh+RGV78E=")
            .Build();
                    
        var chatPropertiesOn = new ChatProperties();
        appConfiguration.GetSection("split:chat_properties:on").Bind(chatPropertiesOn);
        var chatPropertiesOff = new ChatProperties();
        appConfiguration.GetSection("split:chat_properties:off").Bind(chatPropertiesOff);

        if(ChatPropertiesEnabled) {
            promptExecutingSetting.ModelId = chatPropertiesOn.ModelId;
            promptExecutingSetting.MaxTokens = chatPropertiesOn.MaxTokens;
            promptExecutingSetting.Temperature = chatPropertiesOn.Temperature;
        } else {
            promptExecutingSetting.ModelId = chatPropertiesOff.ModelId;
            promptExecutingSetting.MaxTokens = chatPropertiesOff.MaxTokens;
            promptExecutingSetting.Temperature = chatPropertiesOff.Temperature;
        }

        var onCursor = appConfiguration.GetSection("split:answer_prefix:on").Get<string>();
        var offCursor = appConfiguration.GetSection("split:answer_prefix:off").Get<string>();


        DateTime startTime = DateTime.UtcNow;

        // get answer
        ChatMessageContent answer;
        var answerJson = "";
        try {
            answer = await chat.GetChatMessageContentAsync(
                        answerChat,
                        promptExecutingSetting,
                        cancellationToken: cancellationToken);
            answerJson = answer.Content ?? throw new InvalidOperationException("Failed to get search query");
        } catch (Exception ee) {
            if(!ee.Message.Contains("Status: 429")) {
                throw;
            }
            answerJson = $@" 
                {{ 
                    ""answer"": ""The squirrels are taking a breather.  Come back soon."", 
                    ""thoughts"": ""JSON malformed. {ee.Message.Replace("\"", "\\\"")}""
                }}";
        }

        DateTime endTime = DateTime.UtcNow;
        TimeSpan getChatMessageTimeSpan = endTime - startTime;
        long getChatMessageTimeInMillis = (long) getChatMessageTimeSpan.TotalMilliseconds;

        // DBM ought to look at HttpContext, but DI is failing at the ServiceCollectionExtensions
        string? name = _telemetryClient.Context.User.AuthenticatedUserId;

        if(name == null) {
            name = Guid.NewGuid().ToString();
            _telemetryClient.Context.User.AuthenticatedUserId = name;
        }

        Dictionary<string, string> props = new Dictionary<string, string>();
        props.Add("TargetingId", name);
        props.Add("MaxTokens", "" + promptExecutingSetting.MaxTokens);
        props.Add("Temperature", "" + promptExecutingSetting.Temperature);
        props.Add("ModelId", "" + promptExecutingSetting.ModelId);
        props.Add("chat_latency_in_ms", "" + getChatMessageTimeInMillis);
        _telemetryClient.TrackEvent("chat_latency_in_ms", props);

        answerJson = HealJson(answerJson);

        var answerObject = new JsonElement();
        try {
            answerObject = JsonSerializer.Deserialize<JsonElement>(answerJson);
        } catch (Exception) {
            var json2 = $@" 
                            {{ 
                                ""answer"": ""The squirrels are out to lunch.  Come back soon."", 
                                ""thoughts"": ""JSON malformed. {answerJson.Replace("\"", "\\\"")}""
                            }}";   

            answerObject = JsonSerializer.Deserialize<JsonElement>(json2);                           
        }
        Dictionary<string, string> jProps = new Dictionary<string, string>();
        jProps.Add("Json", answerObject.ToString());

        // DBM for debug
        _telemetryClient.TrackEvent("json", jProps);

        var ans = answerObject.GetProperty("answer").GetString() ?? throw new InvalidOperationException("Failed to get answer");
        var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

        if(AnswerPrefixEnabled) {
            ans = onCursor + ans;
        } else {
            ans = offCursor + ans;
        }


        // step 4
        // add follow up questions if requested
        if (overrides?.SuggestFollowupQuestions is true)
        {
            var followUpQuestionChat = new ChatHistory(@"You are a helpful AI assistant");
            followUpQuestionChat.AddUserMessage($@"Generate three follow-up question based on the answer you just generated.
# Answer
{ans}

# Format of the response
Return the follow-up question as a json string list. Don't put your answer between ```json and ```, return the json string directly.
e.g.
[
    ""What is the deductible?"",
    ""What is the co-pay?"",
    ""What is the out-of-pocket maximum?""
]");

            var followUpQuestions = await chat.GetChatMessageContentAsync(
                followUpQuestionChat,
                promptExecutingSetting,
                cancellationToken: cancellationToken);

            var followUpQuestionsJson = followUpQuestions.Content ?? throw new InvalidOperationException("Failed to get search query");
            var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestionsJson);
            var followUpQuestionsList = followUpQuestionsObject.EnumerateArray().Select(x => x.GetString()).ToList();
            foreach (var followUpQuestion in followUpQuestionsList)
            {
                ans += $" <<{followUpQuestion}>> ";
            }
        }
        return new ApproachResponse(
            DataPoints: documentContentList,
            Images: images,
            Answer: ans,
            Thoughts: thoughts,
            CitationBaseUrl: _configuration.ToCitationBaseUrl());
    }
}
