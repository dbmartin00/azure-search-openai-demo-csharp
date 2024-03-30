// Copyright (c) Microsoft. All rights reserved.

namespace SharedWebComponents.Components;

public sealed partial class Answer
{
    [Parameter, EditorRequired] public required ApproachResponse Retort { get; set; }
    [Parameter, EditorRequired] public required EventCallback<string> FollowupQuestionClicked { get; set; }

    [Inject] public required IPdfViewer PdfViewer { get; set; }

    private HtmlParsedAnswer? _parsedAnswer;

    protected override void OnParametersSet()
    {
        _parsedAnswer = ParseAnswerToHtml(
            Retort.Answer, Retort.CitationBaseUrl);

        base.OnParametersSet();
    }

    private async Task TrackEventToSplitAsync() {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string url = "https://events.split.io/api/events";
        string jsonData = "{" 
            + "\"eventTypeId\":\"thank_you\","
            + "\"trafficTypeName\":\"user\","
            + "\"key\":\"" + ApiClient.GetSplitTrafficKey() +  "\","
            + "\"environmentName\":\"Prod-microsoft\","
            + "\"timestamp\":" + now.ToUnixTimeMilliseconds()
        + "}";

        using (var client = new HttpClient())
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                
                request.Headers.Add("Authorization", "Bearer " + ApiClient.GetSplitSdkKey());
                
                // fire and forget
                await client.SendAsync(request);
            }
        }  
    }

    private async Task OnAskFollowupAsync(string followupQuestion)
    {
        if(followupQuestion.StartsWith("Thank you")) {
            await TrackEventToSplitAsync();
        }
        if (FollowupQuestionClicked.HasDelegate)
        {
            await FollowupQuestionClicked.InvokeAsync(followupQuestion);
        }
    }
    private ValueTask OnShowCitationAsync(CitationDetails citation) => PdfViewer.ShowDocumentAsync(citation.Name, citation.BaseUrl);

    private MarkupString RemoveLeadingAndTrailingLineBreaks(string input) => (MarkupString)HtmlLineBreakRegex().Replace(input, "");

    [GeneratedRegex("^(\\s*<br\\s*/?>\\s*)+|(\\s*<br\\s*/?>\\s*)+$", RegexOptions.Multiline)]
    private static partial Regex HtmlLineBreakRegex();
}