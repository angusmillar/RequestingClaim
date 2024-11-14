using System.Net;

namespace Abm.Requesting.Claim.Extensions;

public static class IpAddressExtensions
{
    public static IPAddress[] ResolveIp(this string? host, string? errorMessage = null)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(host) ? Dns.GetHostAddresses(host) : [];
        }
        catch (Exception exception)
        {
            if (errorMessage is null)
            {
                throw;
            }
            
            throw new ApplicationException(errorMessage, exception);
            
        }
    }
}