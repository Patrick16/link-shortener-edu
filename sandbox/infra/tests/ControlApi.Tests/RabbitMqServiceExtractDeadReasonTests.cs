using System.Text.Json;
using ControlApi.Services;

namespace ControlApi.Tests;

public class RabbitMqServiceExtractDeadReasonTests
{
    private static JsonElement StringElement(string value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public void ExtractDeadReason_HeaderPresent_ReturnsItsStringValue()
    {
        var properties = new RabbitMqService.ManagementMessageProperties(
            MessageId: "msg-1",
            Headers: new Dictionary<string, JsonElement> { ["x-dead-reason"] = StringElement("deserialize-failed") });

        var reason = RabbitMqService.ExtractDeadReason(properties);

        Assert.Equal("deserialize-failed", reason);
    }

    [Fact]
    public void ExtractDeadReason_NoHeaders_ReturnsNull()
    {
        var properties = new RabbitMqService.ManagementMessageProperties(MessageId: "msg-1", Headers: null);

        Assert.Null(RabbitMqService.ExtractDeadReason(properties));
    }

    [Fact]
    public void ExtractDeadReason_HeaderMissing_ReturnsNull()
    {
        var properties = new RabbitMqService.ManagementMessageProperties(
            MessageId: "msg-1",
            Headers: new Dictionary<string, JsonElement> { ["some-other-header"] = StringElement("x") });

        Assert.Null(RabbitMqService.ExtractDeadReason(properties));
    }

    [Fact]
    public void ExtractDeadReason_NullProperties_ReturnsNull()
    {
        Assert.Null(RabbitMqService.ExtractDeadReason(null));
    }
}
