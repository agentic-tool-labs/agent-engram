using Engram.Core;

namespace Engram.Core.Tests;

public class ModCallWebhookKindTests
{
    [Fact]
    public void KindsFilter_ModCall_IsAcceptedAndNotReportedAsUnknown()
    {
        var settings = WebhookSettings.Read(
            ConfigFile.Parse("[webhook]\nurl = \"http://127.0.0.1:8787/engram\"\nkinds = [\"mod-call\"]\n"));

        Assert.Empty(settings.Unknown);
        Assert.True(settings.IsEnabled);
    }

    [Fact]
    public void KindsFilter_NearMissOfModCall_IsReportedAsUnknown()
    {
        var settings = WebhookSettings.Read(
            ConfigFile.Parse("[webhook]\nurl = \"http://127.0.0.1:8787/engram\"\nkinds = [\"mod_call\"]\n"));

        Assert.Equal(["mod_call"], settings.Unknown);
    }
}
