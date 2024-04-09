// Copyright (c) Microsoft. All rights reserved.
using Splitio.Services.Client.Classes;
using Splitio.Services.Client.Interfaces;
using SharedWebComponents.Services;

namespace MinimalApi.Extensions;

internal static class WebApplicationExtensions
{   
    static WebApplicationExtensions() {
        InitSplit();
    }

    private static ISplitClient? s_splitClient;
    
    public static ISplitClient InitSplit() {
        ConfigurationOptions config = new ConfigurationOptions {
            FeaturesRefreshRate = 10,
            ImpressionsRefreshRate = 30,
            LabelsEnabled = true,
            EventsPushRate = 30,
            IPAddressesEnabled = false,
            StreamingEnabled = true
        };

        SplitFactory factory = new SplitFactory(ApiClient.GetSplitSdkKey(), config);
        s_splitClient = factory.Client();
        try
        {
            s_splitClient.BlockUntilReady(10000);
        }
        catch (Exception ex)
        {
            throw new TimeoutException("Split timed out during SDK init: " + ex.Message);
        }
        return s_splitClient;
    }

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

        return app;
    }

    private static IResult OnGetEnableLogout(HttpContext context)
    {
        var header = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"];
        var enableLogout = !string.IsNullOrEmpty(header);

        return TypedResults.Ok(enableLogout);
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
            try {
                var response = await chatService.ReplyAsync(
                    request.History, request.Overrides, cancellationToken);

                return TypedResults.Ok(response);
            } catch (Exception ex) {
                Dictionary<string, object> dict = new Dictionary<string, object>();
                dict.Add("ex.Message", ex.Message);
                dict.Add("ex.StackTrace", ex.StackTrace!);
                dict.Add("ex.Source", ex.Source!);
                dict.Add("ex.TargetSite", ex.TargetSite!);

                s_splitClient!.Track(ApiClient.GetSplitTrafficKey(), "user", "error", 1, dict);
                throw;
            }
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