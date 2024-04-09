// Copyright (c) Microsoft. All rights reserved.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.FeatureManagement;

namespace MinimalApi.Extensions;

internal static class WebApplicationExtensions
{
    internal static WebApplication MapApi(this WebApplication app)
    {
        var api = app.MapGroup("api");

        // Blazor 📎 Clippy streaming endpoint
        api.MapPost("openai/chat", OnPostChatPromptAsync);

        // Long-form chat w/ contextual history endpoint
        api.MapPost("chat", OnPostChatAsync);

        // Upload a document
        api.MapPost("documents", OnPostDocumentAsync);

        // Get all documents
        api.MapGet("documents", OnGetDocumentsAsync);

        // Get DALL-E image result from prompt
        api.MapPost("images", OnPostImagePromptAsync);

        api.MapGet("enableLogout", OnGetEnableLogout);

        api.MapGet("talk_to_a_person", OnTalkToAPersonAsync);

        api.MapGet("satisfied_response", OnSatisfiedResponseAsync);

        api.MapGet("sample_questions", OnSampleQuestionsAsync);

        return app;
    }

    private static IResult OnGetEnableLogout(HttpContext context)
    {
        var header = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"];
        var enableLogout = !string.IsNullOrEmpty(header);

        return TypedResults.Ok(enableLogout);
    }

    public static async Task<bool> IsFeatureEnabledAsync(IFeatureManager featureManager, string flag_name)
    {
        return await featureManager.IsEnabledAsync(flag_name);
    }  

    private static IFeatureManager GetFeatureManager() 
    {
        var configuration = new ConfigurationBuilder()
            .AddAzureAppConfiguration(AzureAppConfigServices.GetAzureAppConfigConnectionString())
            .Build();

        // Set up DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddFeatureManagement();
        var serviceProvider = services.BuildServiceProvider();

        return serviceProvider.GetRequiredService<IFeatureManager>();
    }

    private static async Task<IResult> OnTalkToAPersonAsync() 
    {
        string flag_name = "talk_to_a_person";
        bool isMyFeatureEnabled = await IsFeatureEnabledAsync(GetFeatureManager(), flag_name);
        // Console.WriteLine($"{flag_name} enabled: {isMyFeatureEnabled}");
        
        return TypedResults.Ok(isMyFeatureEnabled);
    }
    private static async Task<IResult> OnSatisfiedResponseAsync() 
    {
        string flag_name = "satisfied_response";
        bool isMyFeatureEnabled = await IsFeatureEnabledAsync(GetFeatureManager(), flag_name);
        // Console.WriteLine($"{flag_name} enabled: {isMyFeatureEnabled}");
        
        return TypedResults.Ok(isMyFeatureEnabled);
    }
    private static async Task<IResult> OnSampleQuestionsAsync() 
    {
        string flag_name = "sample_questions";
        bool isMyFeatureEnabled = await IsFeatureEnabledAsync(GetFeatureManager(), flag_name);
        // Console.WriteLine($"{flag_name} enabled: {isMyFeatureEnabled}");
        
        return TypedResults.Ok(isMyFeatureEnabled);
    }
    private static async IAsyncEnumerable<ChatChunkResponse> OnPostChatPromptAsync(
        PromptRequest prompt,
        OpenAIClient client,
        IConfiguration config,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var deploymentId = config["AZURE_OPENAI_CHATGPT_DEPLOYMENT"];
        var response = await client.GetChatCompletionsStreamingAsync(
            new ChatCompletionsOptions
            {
                DeploymentName = deploymentId,
                Messages =
                {
                    new ChatRequestSystemMessage("""
                        You're an AI assistant for developers, helping them write code more efficiently.
                        You're name is **Blazor 📎 Clippy** and you're an expert Blazor developer.
                        You're also an expert in ASP.NET Core, C#, TypeScript, and even JavaScript.
                        You will always reply with a Markdown formatted response.
                        """),
                    new ChatRequestUserMessage("What's your name?"),
                    new ChatRequestAssistantMessage("Hi, my name is **Blazor 📎 Clippy**! Nice to meet you."),
                    new ChatRequestUserMessage(prompt.Prompt)
                }
            }, cancellationToken);

        await foreach (var choice in response.WithCancellation(cancellationToken))
        {
            if (choice.ContentUpdate is { Length: > 0 })
            {
                yield return new ChatChunkResponse(choice.ContentUpdate.Length, choice.ContentUpdate);
            }
        }
    }

    private static async Task<IResult> OnPostChatAsync(
        ChatRequest request,
        ReadRetrieveReadChatService chatService,
        CancellationToken cancellationToken)
    {
        if (request is { History.Length: > 0 })
        {
            var response = await chatService.ReplyAsync(
                request.History, request.Overrides, cancellationToken);

            return TypedResults.Ok(response);
        }

        return Results.BadRequest();
    }

    private static async Task<IResult> OnPostDocumentAsync(
        [FromForm] IFormFileCollection files,
        [FromServices] AzureBlobStorageService service,
        [FromServices] ILogger<AzureBlobStorageService> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Upload documents");

        var response = await service.UploadFilesAsync(files, cancellationToken);

        logger.LogInformation("Upload documents: {x}", response);

        return TypedResults.Ok(response);
    }

    private static async IAsyncEnumerable<DocumentResponse> OnGetDocumentsAsync(
        BlobContainerClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in client.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (blob is not null and { Deleted: false })
            {
                var props = blob.Properties;
                var baseUri = client.Uri;
                var builder = new UriBuilder(baseUri);
                builder.Path += $"/{blob.Name}";

                var metadata = blob.Metadata;
                var documentProcessingStatus = GetMetadataEnumOrDefault<DocumentProcessingStatus>(
                    metadata, nameof(DocumentProcessingStatus), DocumentProcessingStatus.NotProcessed);
                var embeddingType = GetMetadataEnumOrDefault<EmbeddingType>(
                    metadata, nameof(EmbeddingType), EmbeddingType.AzureSearch);

                yield return new(
                    blob.Name,
                    props.ContentType,
                    props.ContentLength ?? 0,
                    props.LastModified,
                    builder.Uri,
                    documentProcessingStatus,
                    embeddingType);

                static TEnum GetMetadataEnumOrDefault<TEnum>(
                    IDictionary<string, string> metadata,
                    string key,
                    TEnum @default) where TEnum : struct => metadata.TryGetValue(key, out var value)
                        && Enum.TryParse<TEnum>(value, out var status)
                            ? status
                            : @default;
            }
        }
    }

    private static async Task<IResult> OnPostImagePromptAsync(
        PromptRequest prompt,
        OpenAIClient client,
        IConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = await client.GetImageGenerationsAsync(new ImageGenerationOptions
        {
            Prompt = prompt.Prompt,
        },
        cancellationToken);

        var imageUrls = result.Value.Data.Select(i => i.Url).ToList();
        var response = new ImageResponse(result.Value.Created, imageUrls);

        return TypedResults.Ok(response);
    }
}
