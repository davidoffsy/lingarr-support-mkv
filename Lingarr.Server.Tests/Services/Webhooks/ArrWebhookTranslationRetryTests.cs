using System;
using Lingarr.Server.Services.Webhooks;
using Xunit;

namespace Lingarr.Server.Tests.Services.Webhooks;

public class ArrWebhookTranslationRetryTests
{
    [Fact]
    public void Delays_AreThirtySecondsThenTwoThenTenMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ArrWebhookTranslationRetry.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), ArrWebhookTranslationRetry.NextDelay(0));
        Assert.Equal(TimeSpan.FromMinutes(10), ArrWebhookTranslationRetry.NextDelay(1));
        Assert.Null(ArrWebhookTranslationRetry.NextDelay(2));
        Assert.Null(ArrWebhookTranslationRetry.NextDelay(3));
    }
}
