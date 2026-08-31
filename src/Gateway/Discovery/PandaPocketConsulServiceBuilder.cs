using Consul;
using Ocelot.Logging;
using Ocelot.Provider.Consul;
using Ocelot.Provider.Consul.Interfaces;

namespace PandaPocket.Gateway.Discovery;

/// <summary>
/// Tells Ocelot to route to the address a service registered, rather than to the
/// Consul node it registered with.
///
/// Ocelot's default builds the downstream host as <c>node?.Name ?? entry.Service.Address</c>.
/// That is right for the deployment Consul is usually run in, where every host
/// runs its own agent and the node name is therefore the routable hostname of
/// the machine the instance is on.
///
/// This system has one shared Consul agent for the whole Compose network, so the
/// node name is the Consul container's own hostname. Every lookup resolved to
/// that container and every route answered 502:
///
///     Connection refused (266bb74f110f:8080)
///
/// Preferring Service.Address fixes it, and is what each service actually
/// registers as its reachable address. The alternative, registering through the
/// catalogue API with a fabricated per-service node, would have meant giving up
/// Consul's actively polled health checks, which are the more valuable half of
/// running a registry at all.
/// </summary>
public sealed class PandaPocketConsulServiceBuilder(
    IHttpContextAccessor contextAccessor,
    IConsulClientFactory clientFactory,
    IOcelotLoggerFactory loggerFactory)
    : DefaultConsulServiceBuilder(contextAccessor, clientFactory, loggerFactory)
{
    protected override string GetDownstreamHost(ServiceEntry entry, Node node)
        => entry.Service.Address;
}
