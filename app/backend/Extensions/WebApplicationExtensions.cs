﻿// Copyright (c) Microsoft. All rights reserved.
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.Telemetry.ApplicationInsights;
using Microsoft.FeatureManagement.Telemetry.ApplicationInsights.AspNetCore;

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

        api.MapGet("talkToAPerson", OnTalkToAPersonAsync);
        api.MapGet("satisfiedResponse", OnSatisfiedResponseAsync);
        api.MapGet("sampleQuestions", OnSampleQuestionsAsync);
        api.MapGet("track", OnTrack);
        api.MapGet("initializeId", OnInitializeId);
        return app;
    }

    private static string OnInitializeId(TelemetryClient telemetryClient) {
        string name = Guid.NewGuid().ToString();
        telemetryClient.Context.User.AuthenticatedUserId = name;
        return "ok";
    }

    private static string OnTrack(
        string eventTypeId,
        TelemetryClient telemetryClient,
        IHttpContextAccessor httpContextAccessor)
    {
        // telemetryClient.getContext().getUser().setId("dmartin"); // Java land        
        // if (httpContext.User?.Identity?.IsAuthenticated == true) {
        //     telemetryClient.Context.User.AuthenticatedUserId = httpContext.User.Identity.Name;
        // }
        // telemetryClient.Context.User.Id = httpContext?.User?.Identity?.Name;

        // telemetryClient.TrackEvent(eventTypeId,
        //     new Dictionary<string,string> {
        //     ["userid"] = "[ \"" + httpContext?.User?.Identity?.Name + "\" ]",
        // });

        // telemetryClient.Context.User.AuthenticatedUserId = "david.martin@split.io";
        
        Dictionary<string, string> props = new Dictionary<string, string>();
        props.Add("TargetingId", telemetryClient.Context.User.AuthenticatedUserId);
        telemetryClient.TrackEvent(eventTypeId, props);

        return "sent";
    }

    private static async Task<string> OnTalkToAPersonAsync(IVariantFeatureManagerSnapshot snapshot) {
        return await GetFeatureVariantAsync(snapshot, "talk_to_a_person");
    }

    private static async Task<string> OnSatisfiedResponseAsync(IVariantFeatureManagerSnapshot snapshot) {
        return await GetFeatureVariantAsync(snapshot, "satisfied_response");
    }

    private static async Task<string> OnSampleQuestionsAsync(IVariantFeatureManagerSnapshot snapshot) {
        return await GetFeatureVariantAsync(snapshot, "sample_questions");
    }

    private static async Task<string> GetFeatureVariantAsync(IVariantFeatureManagerSnapshot snapshot, string flag_name)
    {
        CancellationToken cancellationToken = new CancellationToken();
        Variant personVariant = await snapshot.GetVariantAsync(flag_name, cancellationToken);
        return personVariant.Configuration.Get<string>() ?? "control";
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
        TelemetryClient telemetryClient,
        CancellationToken cancellationToken)
    {
        if (request is { History.Length: > 0 })
        {
            try {
                var response = await chatService.ReplyAsync(
                    request.History, request.Overrides, cancellationToken);

                return TypedResults.Ok(response);
            } catch (Exception ex) {
                Dictionary<string, string> props = new Dictionary<string, string>();
                props.Add("TargetingId", telemetryClient.Context.User.AuthenticatedUserId);                
                props.Add("ex.Message", ex.Message);
                props.Add("ex.StackTrace", ex.StackTrace!);
                props.Add("ex.Source", ex.Source!);

                telemetryClient.TrackEvent("error", props);
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
