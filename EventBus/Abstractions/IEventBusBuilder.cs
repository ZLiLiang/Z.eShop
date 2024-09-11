using Microsoft.Extensions.DependencyInjection;

namespace Z.eShop.EventBus.Abstractions;

public interface IEventBusBuilder
{
    public IServiceCollection Services { get; }
}
