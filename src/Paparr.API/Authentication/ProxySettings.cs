using IPNetwork = System.Net.IPNetwork;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Paparr.API.Authentication;

public sealed class ProxySettings
{
    public string TrustedNetworks { get; set; } = "";

    public string[] Networks() => TrustedNetworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProxySettings>().Bind(configuration.GetSection("ReverseProxy"))
            .Validate(settings => settings.Networks().All(value => IPNetwork.TryParse(value, out _)),
                "ReverseProxy:TrustedNetworks must contain semicolon-separated CIDR networks.")
            .ValidateOnStart();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ProxySettings>>((options, settings) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            foreach (var network in settings.Value.Networks()) options.KnownIPNetworks.Add(IPNetwork.Parse(network));
        });
    }
}
