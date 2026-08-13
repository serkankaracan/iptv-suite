using System.Runtime.InteropServices;
using System.Security;
using IptvSuite.Application;
using IptvSuite.Infrastructure;
using Microsoft.Windows.Storage;

namespace IptvSuite.Windows;

internal static class WindowsSecretStoreFactory
{
    internal static SecretStoreInitializationResult Create(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string localCachePath = ApplicationData.GetDefault().LocalCachePath;
            string protectedStorePath = Path.Combine(
                localCachePath,
                "ProtectedStore",
                "v2");
            var store = new DpapiCurrentUserSecretStore(protectedStorePath, cancellationToken);
            return SecretStoreInitializationResult.Succeeded(store);
        }
        catch (UnauthorizedAccessException)
        {
            return StorageUnavailable();
        }
        catch (IOException)
        {
            return StorageUnavailable();
        }
        catch (ArgumentException)
        {
            return StorageUnavailable();
        }
        catch (InvalidOperationException)
        {
            return StorageUnavailable();
        }
        catch (NotSupportedException)
        {
            return StorageUnavailable();
        }
        catch (ExternalException)
        {
            return StorageUnavailable();
        }
        catch (SecurityException)
        {
            return StorageUnavailable();
        }
    }

    private static SecretStoreInitializationResult StorageUnavailable() =>
        SecretStoreInitializationResult.Failed(SecretStoreFailure.StorageUnavailable);
}
