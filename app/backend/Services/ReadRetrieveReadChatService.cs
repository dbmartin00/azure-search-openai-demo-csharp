﻿// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;

using Splitio.Services.Client.Classes;
using Splitio.Services.Client.Interfaces;
using System.Text.RegularExpressions;
using System.Text.Json;
using SharedWebComponents.Services;

namespace MinimalApi.Services;
#pragma warning disable SKEXP0011 // Mark members as static
#pragma warning disable SKEXP0001 // Mark members as static
public class ReadRetrieveReadChatService
{
    private readonly ISearchService _searchClient;
    private readonly Kernel _kernel;
    private readonly IConfiguration _configuration;
    private readonly IComputerVisionService? _visionService;
    private readonly TokenCredential? _tokenCredential;

    private readonly ISplitClient _splitClient;
    private readonly string _splitTrafficKey;

    public ReadRetrieveReadChatService(
        ISearchService searchClient,
        OpenAIClient client,
        IConfiguration configuration,
        IComputerVisionService? visionService = null,
        TokenCredential? tokenCredential = null)
    {
        _searchClient = searchClient;
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

        _splitTrafficKey = ApiClient.GetSplitTrafficKey() ?? "<placeholder>";
        _splitClient = initSplit();
    }

    public ISplitClient initSplit() {
        ConfigurationOptions config = new ConfigurationOptions {
            FeaturesRefreshRate = 10,
            ImpressionsRefreshRate = 30,
            LabelsEnabled = true,
            EventsPushRate = 30,
            IPAddressesEnabled = false,
            StreamingEnabled = true
        };

        SplitFactory factory = new SplitFactory(ApiClient.GetSplitSdkKey(), config);
        ISplitClient _splitClient = factory.Client();
        try
        {
            _splitClient.BlockUntilReady(10000);
        }
        catch (Exception ex)
        {
            throw new TimeoutException("Split timed out during SDK init: " + ex.Message);
        }
        return _splitClient;
    }

    private class SplitConfig
    {
        public required string ModelId { get; set; }
        public required int MaxTokens { get; set; }
        public required float Temperature { get; set; }
    }    
    
    private class CursorConfig 
    {
        public required string Cursor { get; set; }        
    }

    // DBM doesn't address root cause 
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

        var splitResult = _splitClient.GetTreatmentWithConfig(
                                        _splitTrafficKey, // unique identifier for your user
                                        "chat_properties");
        var splitConfig = JsonSerializer.Deserialize<SplitConfig>(splitResult.Config);

        var cursorResult = _splitClient.GetTreatmentWithConfig(_splitTrafficKey, "answer_prefix");
        var cursorConfig = JsonSerializer.Deserialize<CursorConfig>(cursorResult.Config);
        // var cursor = cursorConfig?.Cursor ?? ">> ";
        var cursor = cursorConfig?.Cursor;

        var promptExecutingSetting = new OpenAIPromptExecutionSettings {
            ModelId = splitConfig?.ModelId ?? "gpt-4-16k",
            MaxTokens = splitConfig?.MaxTokens ?? 128,
            Temperature = splitConfig?.Temperature ?? 1.5,
            StopSequences = [],
        };

        // var promptExecutingSetting = new OpenAIPromptExecutionSettings
        // {
        //     MaxTokens = 1024,
        //     Temperature = overrides?.Temperature ?? 0.7,
        //     StopSequences = [],
        // };   

        // get answer
        DateTime startTime = DateTime.UtcNow;

        var answer = await chat.GetChatMessageContentAsync(
                       answerChat,
                       promptExecutingSetting,
                       cancellationToken: cancellationToken);
        
        var answerJson = answer.Content ?? throw new InvalidOperationException("Failed to get search query");

        DateTime endTime = DateTime.UtcNow;
        TimeSpan getChatMessageTimeSpan = endTime - startTime;
        long getChatMessageTimeInMillis = (long) getChatMessageTimeSpan.TotalMilliseconds;

        Dictionary<string, object> dict = new Dictionary<string, object>();
        dict.Add("ModelId", promptExecutingSetting.ModelId);
        dict.Add("MaxTokens", promptExecutingSetting.MaxTokens);
        dict.Add("Temperature", promptExecutingSetting.Temperature);

        _splitClient.Track(_splitTrafficKey, "user", "chat_api_in_millis", getChatMessageTimeInMillis, dict);

        // DBM for some reason some requests were missing their final } closing character
        // and sometimes empty?
        answerJson = HealJson(answerJson);

        var answerObject = new JsonElement();
        try {
            answerObject = JsonSerializer.Deserialize<JsonElement>(answerJson);
            answerObject.GetProperty("answer").GetString();
            answerObject.GetProperty("thoughts").GetString();
        } catch(Exception ex) {
            throw new JsonException(ex.Message + " answerJson: " + answerJson.ToString());
        }
        var ans = answerObject.GetProperty("answer").GetString() ?? throw new InvalidOperationException("Failed to get answer");
        var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

        // DBM edit
        ans = cursor + ans;
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
            
            // DBM for some reason some requests were missing their final } closing character
            // and sometimes empty?
            followUpQuestionsJson = HealJson(followUpQuestionsJson);

            var followUpQuestionsObject = new JsonElement();
            try {
                followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestionsJson);
            } catch(Exception ex) {
                throw new JsonException(ex.Message + " followUpQuestionsJson: " + followUpQuestionsJson.ToString());
            }

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