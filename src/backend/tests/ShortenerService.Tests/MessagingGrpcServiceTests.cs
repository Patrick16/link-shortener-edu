using System.Text.Json;
using Common;
using Common.Models;
using Contracts.Events;
using Grpc.Core;
using Grpc.Core.Testing;
using Infrastructure;
using Infrastructure.Grpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;

namespace ShortenerService.Tests;

public class MessagingGrpcServiceTests
{
    private static IDbContextFactory<DatabaseContext> NewFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(dbName).Options;
        var factory = new Mock<IDbContextFactory<DatabaseContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(options));
        return factory.Object;
    }

    private static ServerCallContext NewContext() =>
        TestServerCallContext.Create(
            method: "Publish", host: "localhost", deadline: DateTime.UtcNow.AddMinutes(1), requestHeaders: [],
            cancellationToken: CancellationToken.None, peer: "test", authContext: null,
            contextPropagationToken: null, writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null, writeOptionsSetter: _ => { });

    private static MessagingGrpcService NewSut(IDbContextFactory<DatabaseContext> factory) => new(
        new LinkCreatedConsumer(Mock.Of<IMessageConsumer>(), factory, Mock.Of<IEntityCacheService<Link>>(), NullLogger<LinkCreatedConsumer>.Instance));

    [Fact]
    public async Task Publish_ValidLinkCreatedEvent_PersistsToDatabase()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = new LinkCreatedEvent { Hash = "abc12345", OriginalLink = "https://example.com", ShortenLink = "abc12345", CreatedAt = DateTime.UtcNow };
        var request = new PublishRequest { MessageId = Guid.NewGuid().ToString(), Topic = Topics.LinkCreated, Payload = JsonSerializer.Serialize(@event) };

        var ack = await sut.Publish(request, NewContext());

        Assert.True(ack.Success);
        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal("abc12345", stored.Hash);
    }

    [Fact]
    public async Task Publish_UnknownTopic_ThrowsInvalidArgumentWithoutPersisting()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var request = new PublishRequest { MessageId = Guid.NewGuid().ToString(), Topic = "some.other.topic", Payload = "{}" };

        var ex = await Assert.ThrowsAsync<RpcException>(() => sut.Publish(request, NewContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.Links.CountAsync());
    }

    [Fact]
    public async Task Publish_PayloadDoesNotDeserialize_ThrowsInvalidArgument()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var request = new PublishRequest { MessageId = Guid.NewGuid().ToString(), Topic = Topics.LinkCreated, Payload = "null" };

        var ex = await Assert.ThrowsAsync<RpcException>(() => sut.Publish(request, NewContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
