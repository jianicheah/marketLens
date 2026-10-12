using System.Security.Cryptography;
using System.Text;
namespace MarketLens.Services;

public static class OwnerAccess
{
    public static bool RequiresOwner(string path, string? handler, string method = "GET")
    {
        path = path.TrimEnd('/');
        return path.Equals("/Data", StringComparison.OrdinalIgnoreCase) ||
            ((path == "" || path.Equals("/Index", StringComparison.OrdinalIgnoreCase)) &&
                (!HttpMethods.IsGet(method) || string.Equals(handler, "Refresh", StringComparison.OrdinalIgnoreCase) || string.Equals(handler, "Ai", StringComparison.OrdinalIgnoreCase)));
    }
    public static bool Accepts(string authorization, string user, string password)
    {
        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..]));
            var expected = Encoding.UTF8.GetBytes(user + ":" + password);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value)), SHA256.HashData(expected));
        }
        catch (FormatException) { return false; }
    }
}
