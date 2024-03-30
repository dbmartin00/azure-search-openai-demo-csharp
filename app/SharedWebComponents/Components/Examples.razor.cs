// Copyright (c) Microsoft. All rights reserved.

namespace SharedWebComponents.Components;

public sealed partial class Examples
{
    [Parameter, EditorRequired] public required string Message { get; set; }
    [Parameter, EditorRequired] public EventCallback<string> OnExampleClicked { get; set; }

    private string WhatIsIncluded { get; } = "What is included in my Northwind Health Plus plan that is not in standard?";
    private string WhatIsPerfReview { get; } = "What happens in a performance review?";
    private string WhatDoesPmDo { get; } = "What does a Product Manager do?";

    private string WhatHappens { get; } = "What happens if my Northwind Health Plus doesn't cover a procedure?";
    private string WhoReviews { get; } = "Who conducts the performance review?";
    private string HowDo { get; } = "How do product managers do market research?";

    private async Task OnClickedAsync(string exampleText)
    {
        if (OnExampleClicked.HasDelegate)
        {
            await OnExampleClicked.InvokeAsync(exampleText);
        }
    }
}