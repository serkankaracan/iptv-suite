using System.Security.Cryptography;
using IptvSuite.Domain;
using Microsoft.Windows.ApplicationModel.Resources;

namespace IptvSuite.Windows;

internal readonly record struct OpaqueOperationId
{
    private const int ByteCount = 16;

    private OpaqueOperationId(string value) => Value = value;

    internal string Value { get; }

    internal static OpaqueOperationId Create() =>
        new(Convert.ToHexString(RandomNumberGenerator.GetBytes(ByteCount)));

    public override string ToString() => "[OPAQUE-OPERATION-ID]";
}

internal sealed record DomainErrorPresentation(
    string Message,
    string OperationIdLabel,
    OpaqueOperationId OperationId,
    string? ConnectivityHint);

internal sealed class DomainErrorPresenter
{
    private const string GenericResourceKey = "Errors.Generic";
    private const string OperationIdLabelResourceKey = "Diagnostics.OperationIdLabel";
    private const string OfflineHintResourceKey = "Connectivity.OfflineHint";
    private const string OnlineHintResourceKey = "Connectivity.OnlineHint";
    private const string GenericFallback = "The operation could not be completed safely.";
    private const string OperationIdLabelFallback = "Operation ID";
    private const string OfflineHintFallback =
        "This device appears to be offline. Playback availability is still determined by the player.";
    private const string OnlineHintFallback =
        "Windows reports network access, but the playback source may still be unavailable.";

    private readonly ResourceLoader _resources;

    internal DomainErrorPresenter()
        : this(new ResourceLoader())
    {
    }

    internal DomainErrorPresenter(ResourceLoader resources) =>
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    internal DomainErrorPresentation Present(
        DomainError error,
        NetworkAvailabilityHint connectivityHint)
    {
        ArgumentNullException.ThrowIfNull(error);

        string message = GetLocalizedMessage(error);
        string? hint = connectivityHint switch
        {
            NetworkAvailabilityHint.Offline =>
                GetString(OfflineHintResourceKey, OfflineHintFallback),
            NetworkAvailabilityHint.Online =>
                GetString(OnlineHintResourceKey, OnlineHintFallback),
            _ => null,
        };

        return new DomainErrorPresentation(
            message,
            GetString(OperationIdLabelResourceKey, OperationIdLabelFallback),
            OpaqueOperationId.Create(),
            hint);
    }

    private string GetLocalizedMessage(DomainError error)
    {
        DomainError canonical;
        try
        {
            canonical = DomainError.Create(error.Code);
        }
        catch (ArgumentOutOfRangeException)
        {
            return GetString(GenericResourceKey, GenericFallback);
        }

        if (canonical != error)
        {
            return GetString(GenericResourceKey, GenericFallback);
        }

        return GetString(
            canonical.ResourceKey,
            GetString(GenericResourceKey, GenericFallback));
    }

    private string GetString(string resourceKey, string fallback)
    {
        try
        {
            string value = _resources.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return fallback;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
