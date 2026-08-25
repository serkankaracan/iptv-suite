using Windows.Networking.Connectivity;

namespace IptvSuite.Windows;

internal enum NetworkAvailabilityHint
{
    Unknown,
    Offline,
    Online,
}

internal interface INetworkAvailabilityHintSource
{
    NetworkAvailabilityHint ReadCurrent();
}

internal sealed class WindowsNetworkAvailabilityHintSource :
    INetworkAvailabilityHintSource
{
    public NetworkAvailabilityHint ReadCurrent()
    {
        try
        {
            ConnectionProfile? profile =
                NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return NetworkAvailabilityHint.Offline;
            }

            return profile.GetNetworkConnectivityLevel() ==
                NetworkConnectivityLevel.None
                    ? NetworkAvailabilityHint.Offline
                    : NetworkAvailabilityHint.Online;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return NetworkAvailabilityHint.Unknown;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
