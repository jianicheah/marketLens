using System.Net;
namespace MarketLens.Services;

public sealed class RenderHosting(IConfiguration configuration)
{
    public bool IsPublic => configuration.GetValue<bool>("Hosting:PublicMode");
    public string? PublicHost => configuration["RENDER_EXTERNAL_HOSTNAME"];
    public bool Allows(string host, IPAddress? address)
    {
        if (IsPublic) return !string.IsNullOrWhiteSpace(PublicHost) &&
            string.Equals(host, PublicHost, StringComparison.OrdinalIgnoreCase);
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return address is not null && IPAddress.IsLoopback(address) && host is "localhost" or "127.0.0.1" or "::1";
    }
}
