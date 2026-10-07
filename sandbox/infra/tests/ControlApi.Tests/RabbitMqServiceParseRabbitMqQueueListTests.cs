using ControlApi.Services;

namespace ControlApi.Tests;

public class RabbitMqServiceParseRabbitMqQueueListTests
{
    [Fact]
    public void ParseRabbitMqQueueList_NewlineDelimitedObjects_ParsesEachLine()
    {
        // The shape actually observed from `rabbitmqctl list_queues ... --formatter=json` - one
        // compact JSON object per line, no enclosing array.
        var output = "{\"name\":\"shortener-service.link-created\",\"messages\":0}\n" +
                     "{\"name\":\"shortener-service.link-created.dead\",\"messages\":3}\n";

        var result = RabbitMqService.ParseRabbitMqQueueList(output);

        Assert.Equal(2, result.Count);
        Assert.Equal("shortener-service.link-created.dead", result[1].Name);
        Assert.Equal(3, result[1].Messages);
    }

    [Fact]
    public void ParseRabbitMqQueueList_WrappedArray_StillParses()
    {
        // Not the documented default, but tolerated rather than assumed away - see the comment on
        // ParseRabbitMqQueueList itself.
        var output = "[{\"name\":\"traffic-service.click-tracked.dead\",\"messages\":1}]";

        var result = RabbitMqService.ParseRabbitMqQueueList(output);

        Assert.Single(result);
        Assert.Equal("traffic-service.click-tracked.dead", result[0].Name);
    }

    [Fact]
    public void ParseRabbitMqQueueList_EmptyOutput_ReturnsEmptyList()
    {
        Assert.Empty(RabbitMqService.ParseRabbitMqQueueList(string.Empty));
    }

    [Fact]
    public void ParseRabbitMqQueueList_NonJsonNoise_SkipsUnparseableLines()
    {
        // Defensive: a stderr banner or warning line mixed into the output shouldn't blow up parsing.
        var output = "Listing queues for vhost / ...\n" +
                     "{\"name\":\"reporting-service.click-tracked.dead\",\"messages\":0}\n";

        var result = RabbitMqService.ParseRabbitMqQueueList(output);

        Assert.Single(result);
        Assert.Equal("reporting-service.click-tracked.dead", result[0].Name);
    }

    [Fact]
    public void ParseRabbitMqQueueList_TruncatedJsonLine_SkipsItInsteadOfThrowing()
    {
        // A line that starts with '{' but isn't valid/complete JSON - e.g. the exec timeout cutting
        // rabbitmqctl off mid-stream - must not take the whole read down.
        var output = "{\"name\":\"shortener-service.click-tracked.dead\",\"mess\n" +
                     "{\"name\":\"traffic-service.click-tracked.dead\",\"messages\":2}\n";

        var result = RabbitMqService.ParseRabbitMqQueueList(output);

        Assert.Single(result);
        Assert.Equal("traffic-service.click-tracked.dead", result[0].Name);
    }
}
