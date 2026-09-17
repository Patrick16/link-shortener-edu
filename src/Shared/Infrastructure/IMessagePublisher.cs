namespace Infrastructure;

public interface IMessagePublisher
{
    Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken);
}
