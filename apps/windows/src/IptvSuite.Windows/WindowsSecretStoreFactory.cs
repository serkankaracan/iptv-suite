using IptvSuite.Application;
using IptvSuite.Infrastructure;
using Microsoft.Windows.Storage;

namespace IptvSuite.Windows;

internal static class WindowsSecretStoreFactory
{
    internal static ISecretStore Create()
    {
        string localCachePath = ApplicationData.GetDefault().LocalCachePath;
        string protectedStorePath = Path.Combine(
            localCachePath,
            "IptvSuite",
            "ProtectedStore",
            "v1");
        return new DpapiCurrentUserSecretStore(protectedStorePath);
    }
}
