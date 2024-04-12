// Copyright (c) Microsoft. All rights reserved.
using Microsoft.FeatureManagement.Telemetry.ApplicationInsights;
using Microsoft.FeatureManagement.Telemetry.ApplicationInsights.AspNetCore;
using Microsoft.ApplicationInsights;

namespace SharedWebComponents.Components;

public sealed partial class Answer
{
    [Parameter, EditorRequired] public required ApproachResponse Retort { get; set; }
    [Parameter, EditorRequired] public required EventCallback<string> FollowupQuestionClicked { get; set; }

    [Inject] public required IPdfViewer PdfViewer { get; set; }

    [Inject] public required TelemetryClient telemetryClient { get; set; }

    private HtmlParsedAnswer? _parsedAnswer;

    protected override void OnParametersSet()
    {
        _parsedAnswer = ParseAnswerToHtml(
            Retort.Answer, Retort.CitationBaseUrl);

        base.OnParametersSet();
    }

    private void TrackEvent(string eventTypeId)
    {
        telemetryClient.TrackEvent(eventTypeId);

        // telemetryClient.getContext().getUser().setId("dmartin");
    }

    private async Task OnAskFollowupAsync(string followupQuestion)
    {
        if(followupQuestion.StartsWith("Thank you")) {
            TrackEvent("thank_you");
        } else if (followupQuestion.StartsWith("Can I talk")) {
            TrackEvent("want_to_talk");
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
