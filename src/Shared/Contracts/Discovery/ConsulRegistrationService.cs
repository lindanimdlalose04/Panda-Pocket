using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PandaPocket.Shared.Contracts.Discovery;

/// <summary>
/// Registers this service instance with Consul on startup and removes it on
/// shutdown.
///
/// Self-registration, rather than a separate deployment step, because the
/// instance is the only thing that knows it has finished starting. It also means
/// scaling a service to three containers puts three instances in the catalogue
/// with no configuration change anywhere else.
///
/// The registration includes a health check pointing back at this instance's own
/// /health endpoint, which is the difference between a registry and a
/// configuration file. Consul polls it and marks the instance critical when it
/// fails, so the gateway stops routing to an instance whose database has gone
/// away. Docker Compose DNS, which this replaces, resolves a name to a container
/// whether or not that container can actually serve a request.
/// </summary>
public sealed class ConsulRegistrationService(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceRegistryOptions> options,
    ILogger<ConsulRegistrationService> logger) : IHostedService
{
    private readonly ServiceRegistryOptions _options = options.Value;

    /// <summary>
    /// Unique per instance, so several containers of the same service can
    /// coexist in the catalogue. The service NAME is what callers look up; the
    /// instance ID is what gets deregistered.
    /// </summary>
    private readonly string _instanceId =
        $"{options.Value.ServiceName}-{Guid.NewGuid().ToString("N")[..8]}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Service registry disabled; not registering with Consul");
            return;
        }

        var registration = new
        {
            ID = _instanceId,
            Name = _options.ServiceName,
            Address = _options.ServiceAddress,
            Port = _options.ServicePort,
            Tags = _options.Tags,
            Check = new
            {
                HTTP = $"http://{_options.ServiceAddress}:{_options.ServicePort}{_options.HealthCheckPath}",
                Interval = $"{_options.HealthCheckIntervalSeconds}s",
                Timeout = "5s",
                DeregisterCriticalServiceAfter = $"{_options.DeregisterAfterMinutes}m"
            }
        };

        // Retried, because Consul and the service start at the same time and
        // whichever wins the race is not something either can control.
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                var client = httpClientFactory.CreateClient("consul");
                var response = await client.PutAsJsonAsync(
                    "/v1/agent/service/register", registration, cancellationToken);

                response.EnsureSuccessStatusCode();

                logger.LogInformation(
                    "Registered {InstanceId} with Consul as {ServiceName} at {Address}:{Port}, health check every {Interval}s",
                    _instanceId, _options.ServiceName, _options.ServiceAddress,
                    _options.ServicePort, _options.HealthCheckIntervalSeconds);
                return;
            }
            catch (Exception ex) when (attempt < 6)
            {
                logger.LogWarning("Consul registration attempt {Attempt} failed ({Message}); retrying in {Delay}s",
                    attempt, ex.Message, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 20));
            }
            catch (Exception ex)
            {
                // The service starts anyway. An unreachable registry should not
                // stop a service that is otherwise perfectly able to work, and
                // Consul removes instances that stop answering their health
                // check in any case.
                logger.LogError(ex, "Could not register with Consul after several attempts; continuing unregistered");
                return;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;

        try
        {
            var client = httpClientFactory.CreateClient("consul");
            await client.PutAsync($"/v1/agent/service/deregister/{_instanceId}", null, cancellationToken);

            logger.LogInformation("Deregistered {InstanceId} from Consul", _instanceId);
        }
        catch (Exception ex)
        {
            // Not fatal. The health check will fail and Consul will remove the
            // instance on its own; deregistering just makes it immediate rather
            // than waiting out DeregisterCriticalServiceAfter.
            logger.LogWarning(ex, "Could not deregister {InstanceId} cleanly", _instanceId);
        }
    }
}

public static class ServiceRegistryExtensions
{
    /// <summary>
    /// Wires up self-registration. Called identically by all four services.
    /// </summary>
    public static IServiceCollection AddServiceRegistry(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ServiceRegistryOptions>(
            configuration.GetSection(ServiceRegistryOptions.SectionName));

        services.AddHttpClient("consul", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ServiceRegistryOptions>>().Value;
            client.BaseAddress = new Uri(options.ConsulUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddHostedService<ConsulRegistrationService>();
        return services;
    }
}
