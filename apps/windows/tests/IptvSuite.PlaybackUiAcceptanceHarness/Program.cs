using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using IptvSuite.Testing;
using Microsoft.Data.Sqlite;

namespace IptvSuite.PlaybackUiAcceptanceHarness;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Command = "serve-and-seed";
    private const string OnboardingCommand = "serve-onboarding";
    private const string ExistingCatalogSourceName = "Synthetic 50k source";
    private const string PlaybackSourceName = "00 Synthetic protected playback source";
    private const string PlaybackChannelAName = "Synthetic protected Tier A channel A";
    private const string PlaybackChannelBName = "Synthetic protected Tier A channel B";
    private const string MediaRouteA = "/direct-h264-aac-a.ts";
    private const string MediaRouteB = "/direct-h264-aac-b.ts";
    private const string OnboardingPlaylistRoute = "/synthetic-onboarding.m3u";
    private const string M16CanaryOnboardingToken = TestCanary.Marker;
    private const string M16CanaryOnboardingPlaylistRoute =
        "/" + TestCanary.Marker + OnboardingPlaylistRoute;
    private const string FixtureId = "iptvsuite-tier-a-synthetic-v1";
    private const string FixtureLicense = "CC0-1.0";
    private const string FixtureFileName = "direct-h264-aac.ts";
    private const string FixtureManifestName = "fixture-manifest.json";
    private const string ReadyTicketName = "ready.json";
    private const string ResultTicketName = "result.json";
    private const string StopSignalName = "stop.signal";
    private const string CancelVerificationSignalName = "verify-cancel.signal";
    private const string CancelVerificationTicketName = "cancel-result.json";
    private const string DialogCloseVerificationSignalName = "verify-dialog-close.signal";
    private const string DialogCloseVerificationTicketName = "dialog-close-result.json";
    private const string DeletionFaultArmSignalName = "arm-delete-failure.signal";
    private const string DeletionFaultReadyTicketName = "delete-failure-ready.json";
    private const string PendingVerificationSignalName = "verify-pending.signal";
    private const string PendingVerificationTicketName = "pending-result.json";
    private const string StreamFaultArmSignalName = "arm-stream-fault.signal";
    private const string StreamFaultReadyTicketName = "stream-fault-ready.json";
    private const string StreamEndSignalName = "end-stream.signal";
    private const string StreamEndResultTicketName = "stream-end-result.json";
    private const string StreamRestoreSignalName = "restore-stream.signal";
    private const string StreamRestoreResultTicketName = "stream-restore-result.json";
    private const string StreamEndForCancelSignalName = "end-stream-for-cancel.signal";
    private const string StreamCancelReadyTicketName = "stream-cancel-ready.json";
    private const string StreamCancelVerificationSignalName = "verify-stream-cancel.signal";
    private const string StreamCancelResultTicketName = "stream-cancel-result.json";
    private const string PublicCertificateName = "loopback.cer";
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoLaterStreamOpenObservation = TimeSpan.FromSeconds(31);
    private static readonly JsonSerializerOptions TicketJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 3;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args is
                [Command, string catalogDatabasePath, string protectedStorePath,
                    string fixtureRoot, string controlDirectory])
            {
                return await RunAsync(
                    catalogDatabasePath,
                    protectedStorePath,
                    fixtureRoot,
                    controlDirectory,
                    cancellation.Token).ConfigureAwait(false);
            }

            if (args is
                [OnboardingCommand, string onboardingFixtureRoot,
                    string onboardingControlDirectory, string pipeName])
            {
                return await RunOnboardingAsync(
                    onboardingFixtureRoot,
                    onboardingControlDirectory,
                    pipeName,
                    OnboardingPlaylistRoute,
                    cancellation.Token).ConfigureAwait(false);
            }

            if (args is
                [OnboardingCommand, string canaryOnboardingFixtureRoot,
                    string canaryOnboardingControlDirectory, string canaryPipeName,
                    string canaryToken] &&
                string.Equals(
                    canaryToken,
                    M16CanaryOnboardingToken,
                    StringComparison.Ordinal))
            {
                return await RunOnboardingAsync(
                    canaryOnboardingFixtureRoot,
                    canaryOnboardingControlDirectory,
                    canaryPipeName,
                    M16CanaryOnboardingPlaylistRoute,
                    cancellation.Token).ConfigureAwait(false);
            }

            return 2;
        }
        catch
        {
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunOnboardingAsync(
        string fixtureRoot,
        string controlDirectory,
        string pipeName,
        string onboardingPlaylistRoute,
        CancellationToken cancellationToken)
    {
        OnboardingPaths paths = ValidateOnboardingPaths(
            fixtureRoot,
            controlDirectory,
            pipeName);
        LocalHttpFixtureServer? server = null;
        bool readyPublished = false;
        bool locatorTransferred = false;
        bool stopObserved = false;
        bool stoppedGracefully = false;
        string? certificateThumbprint = null;
        int exitCode = 0;

        try
        {
            byte[] fixture = LoadValidatedFixture(paths.FixtureRoot);
            byte[] playlist = BuildOnboardingPlaylist();
            try
            {
                server = await LocalHttpFixtureServer.StartHttpsAsync(
                    new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
                    {
                        [onboardingPlaylistRoute] = new FixtureHttpResponse(
                            200,
                            "audio/x-mpegurl",
                            playlist,
                            SupportsByteRanges: false),
                        [MediaRouteA] = new FixtureHttpResponse(
                            200,
                            "video/mp2t",
                            fixture,
                            SupportsByteRanges: true),
                        [MediaRouteB] = new FixtureHttpResponse(
                            200,
                            "video/mp2t",
                            fixture,
                            SupportsByteRanges: true),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(playlist);
                CryptographicOperations.ZeroMemory(fixture);
            }

            X509Certificate2 certificate = server.Certificate ??
                throw new InvalidOperationException("The loopback certificate is unavailable.");
            certificateThumbprint = NormalizeThumbprint(certificate.Thumbprint);
            WritePublicCertificate(certificate, paths.PublicCertificatePath);
            WriteJsonAtomically(
                new OnboardingReadyTicket(
                    IsReady: true,
                    CertificateThumbprint: certificateThumbprint),
                paths.ReadyTicketPath);
            readyPublished = true;

            await using (var pipe = new NamedPipeServerStream(
                paths.PipeName,
                PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 4096,
                outBufferSize: 4096))
            {
                using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                transferTimeout.CancelAfter(PhaseTimeout);
                await pipe.WaitForConnectionAsync(transferTimeout.Token).ConfigureAwait(false);

                string absolutePlaylistUri = string.Equals(
                    onboardingPlaylistRoute,
                    OnboardingPlaylistRoute,
                    StringComparison.Ordinal)
                    ? new Uri(server.BaseAddress, OnboardingPlaylistRoute).AbsoluteUri
                    : new Uri(server.BaseAddress, M16CanaryOnboardingPlaylistRoute).AbsoluteUri;
                byte[] locator = Encoding.UTF8.GetBytes(absolutePlaylistUri);
                byte[] length = new byte[sizeof(int)];
                try
                {
                    BinaryPrimitives.WriteInt32LittleEndian(length, locator.Length);
                    await pipe.WriteAsync(length, transferTimeout.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(locator, transferTimeout.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(transferTimeout.Token).ConfigureAwait(false);
                    locatorTransferred = true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(length);
                    CryptographicOperations.ZeroMemory(locator);
                }
            }

            await WaitForOnboardingStopSignalAsync(paths, cancellationToken).ConfigureAwait(false);
            stopObserved = true;

            IReadOnlyList<FixtureHttpRequest> requests = server.Requests;
            int playlistRequestCount = requests.Count(request =>
                string.Equals(request.Method, "GET", StringComparison.Ordinal) &&
                string.Equals(request.Path, onboardingPlaylistRoute, StringComparison.Ordinal));
            int mediaRequestCount = requests.Count(request =>
                string.Equals(request.Path, MediaRouteA, StringComparison.Ordinal) ||
                string.Equals(request.Path, MediaRouteB, StringComparison.Ordinal));
            if (server.RequestCount != 1 ||
                playlistRequestCount != 1 ||
                mediaRequestCount != 0 ||
                server.CompletedResponseCount != 1 ||
                server.FailureCount != 0)
            {
                throw new InvalidDataException(
                    "The clean-install onboarding transport accounting is invalid.");
            }

            await server.DisposeAsync().ConfigureAwait(false);
            stoppedGracefully = true;
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            if (server is not null && !stoppedGracefully)
            {
                try
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    stoppedGracefully = stopObserved;
                }
                catch
                {
                    exitCode = 1;
                }
            }

            try
            {
                IReadOnlyList<FixtureHttpRequest> requests = server?.Requests ?? [];
                WriteJsonAtomically(
                    new OnboardingResultTicket(
                        ReadyPublished: readyPublished,
                        LocatorTransferred: locatorTransferred,
                        StopObserved: stopObserved,
                        StoppedGracefully: stoppedGracefully,
                        CertificateThumbprint: certificateThumbprint,
                        RequestCount: server?.RequestCount ?? 0,
                        CompletedResponseCount: server?.CompletedResponseCount ?? 0,
                        FailureCount: server?.FailureCount ?? 0,
                        PlaylistRequestCount: requests.Count(request =>
                            string.Equals(request.Method, "GET", StringComparison.Ordinal) &&
                            string.Equals(
                                request.Path,
                                onboardingPlaylistRoute,
                                StringComparison.Ordinal)),
                        MediaRequestCount: requests.Count(request =>
                            string.Equals(request.Path, MediaRouteA, StringComparison.Ordinal) ||
                            string.Equals(request.Path, MediaRouteB, StringComparison.Ordinal))),
                    paths.ResultTicketPath);
            }
            catch
            {
                exitCode = 1;
            }
        }

        return exitCode;
    }

    private static async Task<int> RunAsync(
        string catalogDatabasePath,
        string protectedStorePath,
        string fixtureRoot,
        string controlDirectory,
        CancellationToken cancellationToken)
    {
        HarnessPaths paths = ValidatePaths(
            catalogDatabasePath,
            protectedStorePath,
            fixtureRoot,
            controlDirectory);
        LocalHttpFixtureServer? server = null;
        string? certificateThumbprint = null;
        bool seedCompleted = false;
        bool readyPublished = false;
        bool stopObserved = false;
        bool stoppedGracefully = false;
        bool cancelNoMutationVerified = false;
        bool dialogCloseNoMutationVerified = false;
        bool pendingDeletionVerified = false;
        bool pendingTargetCatalogPreserved = false;
        bool pendingConfigurationRecordPreserved = false;
        bool pendingTombstoneBindingVerified = false;
        bool pendingSiblingCatalogRetained = false;
        bool deletionFaultReleased = false;
        bool targetCatalogDeleted = false;
        bool targetProtectedRecordsDeleted = false;
        bool tombstoneBindingCompleted = false;
        bool siblingCatalogRetained = false;
        bool streamRecoveryVerified = false;
        bool streamCancelVerified = false;
        bool streamNoLaterOpenVerified = false;
        long streamNoLaterOpenObservationMilliseconds = 0;
        int streamNoLaterOpenRequestCountAtReady = 0;
        int streamNoLaterOpenRequestCountAfterObservation = 0;
        ControlledFixtureStreamSnapshot? normalStreamSnapshot = null;
        ControlledFixtureStreamSnapshot? faultStreamSnapshot = null;
        SeedContext? seedContext = null;
        FileStream? deletionFaultLease = null;
        ControlledFixtureStreamControl? normalStreamControl = null;
        ControlledFixtureStreamControl? faultStreamControl = null;
        int exitCode = 0;

        try
        {
            byte[] fixture = LoadValidatedFixture(paths.FixtureRoot);
            try
            {
                server = await LocalHttpFixtureServer.StartHttpsAsync(
                    new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
                    {
                        [MediaRouteA] = new FixtureHttpResponse(
                            200,
                            "video/mp2t",
                            fixture,
                            SupportsByteRanges: true),
                        [MediaRouteB] = new FixtureHttpResponse(
                            200,
                            "video/mp2t",
                            fixture,
                            SupportsByteRanges: true),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fixture);
            }

            normalStreamControl = server.EnableControlledStream(
                MediaRouteA,
                new ControlledFixtureStreamOptions
                {
                    MaximumRequestOrdinals = 64,
                });

            X509Certificate2 certificate = server.Certificate ??
                throw new InvalidOperationException("The loopback certificate is unavailable.");
            certificateThumbprint = NormalizeThumbprint(certificate.Thumbprint);
            WritePublicCertificate(certificate, paths.PublicCertificatePath);

            seedContext = await SeedAndVerifyAsync(
                paths.CatalogDatabasePath,
                paths.ProtectedStorePath,
                server.BaseAddress,
                cancellationToken).ConfigureAwait(false);
            seedCompleted = true;

            WriteJsonAtomically(
                new ReadyTicket(
                    IsReady: true,
                    SeedCompleted: true,
                    CertificateThumbprint: certificateThumbprint),
                paths.ReadyTicketPath);
            readyPublished = true;

            bool streamFaultArmSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.StreamFaultArmSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!streamFaultArmSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before stream-fault arming.");
            }

            faultStreamControl = server.EnableControlledStream(
                MediaRouteB,
                new ControlledFixtureStreamOptions
                {
                    MaximumRequestOrdinals = 3,
                });
            ControlledFixtureStreamSnapshot faultReady = faultStreamControl.Snapshot;
            if (faultReady.Mode != ControlledFixtureStreamMode.Enabled ||
                faultReady.LastAssignedRequestOrdinal != 0 ||
                faultReady.ActiveRequestOrdinal != 0 ||
                faultReady.CurrentHeldRequestCount != 0)
            {
                throw new InvalidDataException("The controlled stream did not begin in the exact ready state.");
            }

            WriteJsonAtomically(
                new StreamFaultReadyTicket(
                    IsReady: true,
                    MaximumRequestOrdinals: 3),
                paths.StreamFaultReadyTicketPath);

            bool streamEndSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.StreamEndSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!streamEndSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before the recovery stream ended.");
            }

            await WaitForControlledStreamSnapshotAsync(
                paths,
                faultStreamControl,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                ],
                snapshot =>
                    snapshot.Mode == ControlledFixtureStreamMode.Enabled &&
                    snapshot.LastAssignedRequestOrdinal == 1 &&
                    snapshot.ActiveRequestOrdinal == 1 &&
                    snapshot.CurrentHeldRequestCount == 0,
                cancellationToken).ConfigureAwait(false);
            faultStreamControl.HoldSubsequentRequests();
            if (!faultStreamControl.TryCompleteActive(1))
            {
                throw new InvalidDataException("The exact first controlled stream could not be completed.");
            }

            ControlledFixtureStreamSnapshot firstEnded = await WaitForControlledStreamSnapshotAsync(
                paths,
                faultStreamControl,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                ],
                snapshot =>
                    snapshot.Mode == ControlledFixtureStreamMode.Holding &&
                    snapshot.LastAssignedRequestOrdinal == 2 &&
                    snapshot.ActiveRequestOrdinal == 0 &&
                    snapshot.CurrentHeldRequestCount == 1 &&
                    snapshot.ExpectedCompletionCount == 1 &&
                    snapshot.LastExpectedCompletionOrdinal == 1 &&
                    snapshot.ExpectedAbortCount == 0 &&
                    snapshot.LastExpectedAbortOrdinal == 0,
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(
                new StreamEndResultTicket(
                    IsVerified: true,
                    LastAssignedRequestOrdinal: firstEnded.LastAssignedRequestOrdinal,
                    ActiveRequestOrdinal: firstEnded.ActiveRequestOrdinal,
                    CurrentHeldRequestCount: firstEnded.CurrentHeldRequestCount,
                    ExpectedCompletionCount: firstEnded.ExpectedCompletionCount,
                    LastExpectedCompletionOrdinal: firstEnded.LastExpectedCompletionOrdinal),
                paths.StreamEndResultTicketPath);

            bool streamRestoreSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.StreamRestoreSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StreamEndResultTicketName,
                    StreamRestoreSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!streamRestoreSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before stream recovery.");
            }

            faultStreamControl.Restore();
            ControlledFixtureStreamSnapshot restoredStream = await WaitForControlledStreamSnapshotAsync(
                paths,
                faultStreamControl,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StreamEndResultTicketName,
                    StreamRestoreSignalName,
                ],
                snapshot =>
                    snapshot.Mode == ControlledFixtureStreamMode.Enabled &&
                    snapshot.LastAssignedRequestOrdinal == 2 &&
                    snapshot.ActiveRequestOrdinal == 2 &&
                    snapshot.CurrentHeldRequestCount == 0 &&
                    snapshot.ExpectedCompletionCount == 1 &&
                    snapshot.LastExpectedCompletionOrdinal == 1 &&
                    snapshot.ExpectedAbortCount == 0,
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(
                new StreamRestoreResultTicket(
                    IsVerified: true,
                    LastAssignedRequestOrdinal: restoredStream.LastAssignedRequestOrdinal,
                    ActiveRequestOrdinal: restoredStream.ActiveRequestOrdinal,
                    CurrentHeldRequestCount: restoredStream.CurrentHeldRequestCount,
                    ExpectedCompletionCount: restoredStream.ExpectedCompletionCount,
                    LastExpectedCompletionOrdinal: restoredStream.LastExpectedCompletionOrdinal),
                paths.StreamRestoreResultTicketPath);
            streamRecoveryVerified = true;

            bool streamEndForCancelSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.StreamEndForCancelSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StreamEndResultTicketName,
                    StreamRestoreSignalName,
                    StreamRestoreResultTicketName,
                    StreamEndForCancelSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!streamEndForCancelSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before the cancel stream ended.");
            }

            ControlledFixtureStreamSnapshot beforeCancelEnd = faultStreamControl.Snapshot;
            if (beforeCancelEnd.Mode != ControlledFixtureStreamMode.Enabled ||
                beforeCancelEnd.LastAssignedRequestOrdinal != 2 ||
                beforeCancelEnd.ActiveRequestOrdinal != 2 ||
                beforeCancelEnd.CurrentHeldRequestCount != 0 ||
                beforeCancelEnd.ExpectedCompletionCount != 1 ||
                beforeCancelEnd.LastExpectedCompletionOrdinal != 1 ||
                beforeCancelEnd.ExpectedAbortCount != 0 ||
                beforeCancelEnd.LastExpectedAbortOrdinal != 0)
            {
                throw new InvalidDataException("The recovered stream was not exact before cancel completion.");
            }

            faultStreamControl.HoldSubsequentRequests();
            if (!faultStreamControl.TryCompleteActive(2))
            {
                throw new InvalidDataException("The exact recovered controlled stream could not be completed.");
            }

            ControlledFixtureStreamSnapshot cancelReady = await WaitForControlledStreamSnapshotAsync(
                paths,
                faultStreamControl,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StreamEndResultTicketName,
                    StreamRestoreSignalName,
                    StreamRestoreResultTicketName,
                    StreamEndForCancelSignalName,
                ],
                snapshot =>
                    snapshot.Mode == ControlledFixtureStreamMode.Holding &&
                    snapshot.LastAssignedRequestOrdinal == 3 &&
                    snapshot.ActiveRequestOrdinal == 0 &&
                    snapshot.CurrentHeldRequestCount == 1 &&
                    snapshot.ExpectedCompletionCount == 2 &&
                    snapshot.LastExpectedCompletionOrdinal == 2 &&
                    snapshot.ExpectedAbortCount == 0 &&
                    snapshot.LastExpectedAbortOrdinal == 0,
                cancellationToken).ConfigureAwait(false);
            streamNoLaterOpenRequestCountAtReady = server.RequestCount;
            WriteJsonAtomically(
                new StreamCancelReadyTicket(
                    IsVerified: true,
                    LastAssignedRequestOrdinal: cancelReady.LastAssignedRequestOrdinal,
                    ActiveRequestOrdinal: cancelReady.ActiveRequestOrdinal,
                    CurrentHeldRequestCount: cancelReady.CurrentHeldRequestCount,
                    ExpectedCompletionCount: cancelReady.ExpectedCompletionCount,
                    LastExpectedCompletionOrdinal: cancelReady.LastExpectedCompletionOrdinal,
                    RequestCountAtReady: streamNoLaterOpenRequestCountAtReady),
                paths.StreamCancelReadyTicketPath);

            bool streamCancelVerificationSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.StreamCancelVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StreamEndResultTicketName,
                    StreamRestoreSignalName,
                    StreamRestoreResultTicketName,
                    StreamEndForCancelSignalName,
                    StreamCancelReadyTicketName,
                    StreamCancelVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!streamCancelVerificationSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before cancel verification.");
            }

            StreamCancelResultTicket streamCancelResult = await VerifyNoLaterStreamOpenAsync(
                paths,
                server,
                faultStreamControl,
                streamNoLaterOpenRequestCountAtReady,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    StreamFaultArmSignalName,
                    StreamFaultReadyTicketName,
                    StreamEndSignalName,
                    StreamEndResultTicketName,
                    StreamRestoreSignalName,
                    StreamRestoreResultTicketName,
                    StreamEndForCancelSignalName,
                    StreamCancelReadyTicketName,
                    StreamCancelVerificationSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(streamCancelResult, paths.StreamCancelResultTicketPath);
            streamCancelVerified = streamCancelResult.IsVerified;
            streamNoLaterOpenVerified = streamCancelResult.IsVerified;
            streamNoLaterOpenObservationMilliseconds = streamCancelResult.ObservationMilliseconds;
            streamNoLaterOpenRequestCountAfterObservation = streamCancelResult.RequestCountAfterObservation;
            faultStreamSnapshot = faultStreamControl.Snapshot;

            string[] completedStreamProtocolNames =
            [
                StreamFaultArmSignalName,
                StreamFaultReadyTicketName,
                StreamEndSignalName,
                StreamEndResultTicketName,
                StreamRestoreSignalName,
                StreamRestoreResultTicketName,
                StreamEndForCancelSignalName,
                StreamCancelReadyTicketName,
                StreamCancelVerificationSignalName,
                StreamCancelResultTicketName,
            ];

            bool cancelSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.CancelVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    .. completedStreamProtocolNames,
                    CancelVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!cancelSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before cancel verification.");
            }

            PreservationOracleResult cancelOracle = await VerifyPreservedStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(cancelOracle, paths.CancelVerificationTicketPath);
            cancelNoMutationVerified = cancelOracle.IsVerified;
            if (!cancelOracle.IsVerified)
            {
                throw new InvalidDataException("The cancel preservation oracle failed.");
            }

            bool closeSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.DialogCloseVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    .. completedStreamProtocolNames,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!closeSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before dialog-close verification.");
            }

            PreservationOracleResult closeOracle = await VerifyPreservedStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(closeOracle, paths.DialogCloseVerificationTicketPath);
            dialogCloseNoMutationVerified = closeOracle.IsVerified;
            if (!closeOracle.IsVerified)
            {
                throw new InvalidDataException("The dialog-close preservation oracle failed.");
            }

            bool deletionFaultArmSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.DeletionFaultArmSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    .. completedStreamProtocolNames,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    DialogCloseVerificationTicketName,
                    DeletionFaultArmSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!deletionFaultArmSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before arming deletion failure.");
            }

            deletionFaultLease = OpenDeletionFaultLease(paths, seedContext);
            WriteJsonAtomically(
                new DeletionFaultReadyTicket(IsReady: true),
                paths.DeletionFaultReadyTicketPath);

            bool pendingSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.PendingVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    .. completedStreamProtocolNames,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    DialogCloseVerificationTicketName,
                    DeletionFaultArmSignalName,
                    DeletionFaultReadyTicketName,
                    PendingVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!pendingSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before pending verification.");
            }

            PendingOracleResult pendingOracle = await VerifyPendingStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            deletionFaultLease.Dispose();
            deletionFaultLease = null;
            deletionFaultReleased = true;
            WriteJsonAtomically(
                new PendingVerificationTicket(
                    IsVerified: pendingOracle.IsVerified,
                    TargetCatalogPreserved: pendingOracle.TargetCatalogPreserved,
                    ConfigurationRecordPreserved: pendingOracle.ConfigurationRecordPreserved,
                    TombstoneBindingPending: pendingOracle.TombstoneBindingPending,
                    SiblingCatalogRetained: pendingOracle.SiblingCatalogRetained,
                    DeletionFaultReleased: deletionFaultReleased),
                paths.PendingVerificationTicketPath);
            pendingDeletionVerified = pendingOracle.IsVerified;
            pendingTargetCatalogPreserved = pendingOracle.TargetCatalogPreserved;
            pendingConfigurationRecordPreserved = pendingOracle.ConfigurationRecordPreserved;
            pendingTombstoneBindingVerified = pendingOracle.TombstoneBindingPending;
            pendingSiblingCatalogRetained = pendingOracle.SiblingCatalogRetained;
            if (!pendingOracle.IsVerified)
            {
                throw new InvalidDataException("The pending source-deletion oracle failed.");
            }

            await WaitForFinalStopSignalAsync(
                paths,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    .. completedStreamProtocolNames,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    DialogCloseVerificationTicketName,
                    DeletionFaultArmSignalName,
                    DeletionFaultReadyTicketName,
                    PendingVerificationSignalName,
                    PendingVerificationTicketName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            stopObserved = true;

            DeletionOracleResult deletionOracle = await VerifyDeletedStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            targetCatalogDeleted = deletionOracle.TargetCatalogDeleted;
            targetProtectedRecordsDeleted = deletionOracle.TargetProtectedRecordsDeleted;
            tombstoneBindingCompleted = deletionOracle.TombstoneBindingCompleted;
            siblingCatalogRetained = deletionOracle.SiblingCatalogRetained;
            if (!deletionOracle.IsVerified)
            {
                throw new InvalidDataException("The source-deletion oracle failed.");
            }

            string[] finalControlNames =
            [
                ReadyTicketName,
                PublicCertificateName,
                .. completedStreamProtocolNames,
                CancelVerificationSignalName,
                CancelVerificationTicketName,
                DialogCloseVerificationSignalName,
                DialogCloseVerificationTicketName,
                DeletionFaultArmSignalName,
                DeletionFaultReadyTicketName,
                PendingVerificationSignalName,
                PendingVerificationTicketName,
                StopSignalName,
            ];
            normalStreamSnapshot = await WaitForControlledStreamSnapshotAsync(
                paths,
                normalStreamControl,
                finalControlNames,
                snapshot =>
                    snapshot.Mode == ControlledFixtureStreamMode.Enabled &&
                    snapshot.LastAssignedRequestOrdinal is > 0 and <= 64 &&
                    snapshot.ActiveRequestOrdinal == 0 &&
                    snapshot.CurrentHeldRequestCount == 0 &&
                    snapshot.PeakActiveRequestCount == 1 &&
                    snapshot.OverlapViolationCount == 0 &&
                    snapshot.ExpectedCompletionCount == 0 &&
                    snapshot.LastExpectedCompletionOrdinal == 0 &&
                    snapshot.ExpectedAbortCount == 0 &&
                    snapshot.LastExpectedAbortOrdinal == 0 &&
                    snapshot.ExpectedRejectCount == 0 &&
                    snapshot.LastExpectedRejectOrdinal == 0 &&
                    snapshot.ClientDetachCount == snapshot.LastAssignedRequestOrdinal &&
                    snapshot.LastClientDetachOrdinal == snapshot.LastAssignedRequestOrdinal &&
                    snapshot.DisabledFallbackCount == 0 &&
                    snapshot.LastDisabledFallbackOrdinal == 0 &&
                    snapshot.CapacityRejectCount == 0 &&
                    snapshot.UnexpectedFailureCount == 0 &&
                    snapshot.LastUnexpectedFailureOrdinal == 0,
                cancellationToken,
                stopSignalIsExpected: true).ConfigureAwait(false);
            faultStreamSnapshot = faultStreamControl.Snapshot;
            if (!IsExactFaultStreamFinalSnapshot(faultStreamSnapshot) ||
                !streamRecoveryVerified ||
                !streamCancelVerified ||
                !streamNoLaterOpenVerified ||
                server.FailureCount != 0 ||
                server.CompletedResponseCount + normalStreamSnapshot.ClientDetachCount +
                    faultStreamSnapshot.ClientDetachCount !=
                    server.RequestCount)
            {
                throw new InvalidDataException("The final controlled playback accounting is invalid.");
            }

            await server.DisposeAsync().ConfigureAwait(false);
            stoppedGracefully = true;
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            if (deletionFaultLease is not null)
            {
                try
                {
                    deletionFaultLease.Dispose();
                    deletionFaultReleased = true;
                }
                catch
                {
                    exitCode = 1;
                }
                finally
                {
                    deletionFaultLease = null;
                }
            }

            if (server is not null && !stoppedGracefully)
            {
                try
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    stoppedGracefully = stopObserved;
                }
                catch
                {
                    exitCode = 1;
                }
            }

            try
            {
                IReadOnlyList<FixtureHttpRequest> requests = server?.Requests ?? [];
                ControlledFixtureStreamSnapshot? normalResultSnapshot =
                    normalStreamSnapshot ?? normalStreamControl?.Snapshot;
                ControlledFixtureStreamSnapshot? faultResultSnapshot =
                    faultStreamSnapshot ?? faultStreamControl?.Snapshot;
                WriteJsonAtomically(
                    new ResultTicket(
                        ReadyPublished: readyPublished,
                        SeedCompleted: seedCompleted,
                        StopObserved: stopObserved,
                        StoppedGracefully: stoppedGracefully,
                        CertificateThumbprint: certificateThumbprint,
                        RequestCount: server?.RequestCount ?? 0,
                        CompletedResponseCount: server?.CompletedResponseCount ?? 0,
                        CompletedBodyBytes: server?.CompletedBodyBytes ?? 0,
                        FailureCount: server?.FailureCount ?? 0,
                        ChannelARequestCount: requests.Count(request =>
                            string.Equals(request.Path, MediaRouteA, StringComparison.Ordinal)),
                        ChannelBRequestCount: requests.Count(request =>
                            string.Equals(request.Path, MediaRouteB, StringComparison.Ordinal)),
                        CancelNoMutationVerified: cancelNoMutationVerified,
                        DialogCloseNoMutationVerified: dialogCloseNoMutationVerified,
                        PendingDeletionVerified: pendingDeletionVerified,
                        PendingTargetCatalogPreserved: pendingTargetCatalogPreserved,
                        PendingConfigurationRecordPreserved: pendingConfigurationRecordPreserved,
                        PendingTombstoneBindingVerified: pendingTombstoneBindingVerified,
                        PendingSiblingCatalogRetained: pendingSiblingCatalogRetained,
                        DeletionFaultReleased: deletionFaultReleased,
                        TargetCatalogDeleted: targetCatalogDeleted,
                        TargetProtectedRecordsDeleted: targetProtectedRecordsDeleted,
                        TombstoneBindingCompleted: tombstoneBindingCompleted,
                        SiblingCatalogRetained: siblingCatalogRetained,
                        StreamRecoveryVerified: streamRecoveryVerified,
                        StreamCancelVerified: streamCancelVerified,
                        StreamNoLaterOpenVerified: streamNoLaterOpenVerified,
                        StreamNoLaterOpenObservationMilliseconds:
                            streamNoLaterOpenObservationMilliseconds,
                        StreamNoLaterOpenRequestCountAtReady:
                            streamNoLaterOpenRequestCountAtReady,
                        StreamNoLaterOpenRequestCountAfterObservation:
                            streamNoLaterOpenRequestCountAfterObservation,
                        NormalStreamLastAssignedRequestOrdinal:
                            normalResultSnapshot?.LastAssignedRequestOrdinal ?? 0,
                        NormalStreamActiveRequestOrdinal:
                            normalResultSnapshot?.ActiveRequestOrdinal ?? 0,
                        NormalStreamCurrentHeldRequestCount:
                            normalResultSnapshot?.CurrentHeldRequestCount ?? 0,
                        NormalStreamPeakHeldRequestCount:
                            normalResultSnapshot?.PeakHeldRequestCount ?? 0,
                        NormalStreamPeakActiveRequestCount:
                            normalResultSnapshot?.PeakActiveRequestCount ?? 0,
                        NormalStreamOverlapViolationCount:
                            normalResultSnapshot?.OverlapViolationCount ?? 0,
                        NormalStreamExpectedCompletionCount:
                            normalResultSnapshot?.ExpectedCompletionCount ?? 0,
                        NormalStreamLastExpectedCompletionOrdinal:
                            normalResultSnapshot?.LastExpectedCompletionOrdinal ?? 0,
                        NormalStreamExpectedAbortCount:
                            normalResultSnapshot?.ExpectedAbortCount ?? 0,
                        NormalStreamLastExpectedAbortOrdinal:
                            normalResultSnapshot?.LastExpectedAbortOrdinal ?? 0,
                        NormalStreamExpectedRejectCount:
                            normalResultSnapshot?.ExpectedRejectCount ?? 0,
                        NormalStreamLastExpectedRejectOrdinal:
                            normalResultSnapshot?.LastExpectedRejectOrdinal ?? 0,
                        NormalStreamClientDetachCount:
                            normalResultSnapshot?.ClientDetachCount ?? 0,
                        NormalStreamLastClientDetachOrdinal:
                            normalResultSnapshot?.LastClientDetachOrdinal ?? 0,
                        NormalStreamDisabledFallbackCount:
                            normalResultSnapshot?.DisabledFallbackCount ?? 0,
                        NormalStreamLastDisabledFallbackOrdinal:
                            normalResultSnapshot?.LastDisabledFallbackOrdinal ?? 0,
                        NormalStreamCapacityRejectCount:
                            normalResultSnapshot?.CapacityRejectCount ?? 0,
                        NormalStreamUnexpectedFailureCount:
                            normalResultSnapshot?.UnexpectedFailureCount ?? 0,
                        NormalStreamLastUnexpectedFailureOrdinal:
                            normalResultSnapshot?.LastUnexpectedFailureOrdinal ?? 0,
                        FaultStreamHolding:
                            faultResultSnapshot?.Mode == ControlledFixtureStreamMode.Holding,
                        FaultStreamLastAssignedRequestOrdinal:
                            faultResultSnapshot?.LastAssignedRequestOrdinal ?? 0,
                        FaultStreamActiveRequestOrdinal:
                            faultResultSnapshot?.ActiveRequestOrdinal ?? 0,
                        FaultStreamCurrentHeldRequestCount:
                            faultResultSnapshot?.CurrentHeldRequestCount ?? 0,
                        FaultStreamPeakHeldRequestCount:
                            faultResultSnapshot?.PeakHeldRequestCount ?? 0,
                        FaultStreamPeakActiveRequestCount:
                            faultResultSnapshot?.PeakActiveRequestCount ?? 0,
                        FaultStreamOverlapViolationCount:
                            faultResultSnapshot?.OverlapViolationCount ?? 0,
                        FaultStreamExpectedCompletionCount:
                            faultResultSnapshot?.ExpectedCompletionCount ?? 0,
                        FaultStreamLastExpectedCompletionOrdinal:
                            faultResultSnapshot?.LastExpectedCompletionOrdinal ?? 0,
                        FaultStreamExpectedAbortCount:
                            faultResultSnapshot?.ExpectedAbortCount ?? 0,
                        FaultStreamLastExpectedAbortOrdinal:
                            faultResultSnapshot?.LastExpectedAbortOrdinal ?? 0,
                        FaultStreamExpectedRejectCount:
                            faultResultSnapshot?.ExpectedRejectCount ?? 0,
                        FaultStreamLastExpectedRejectOrdinal:
                            faultResultSnapshot?.LastExpectedRejectOrdinal ?? 0,
                        FaultStreamClientDetachCount:
                            faultResultSnapshot?.ClientDetachCount ?? 0,
                        FaultStreamLastClientDetachOrdinal:
                            faultResultSnapshot?.LastClientDetachOrdinal ?? 0,
                        FaultStreamDisabledFallbackCount:
                            faultResultSnapshot?.DisabledFallbackCount ?? 0,
                        FaultStreamLastDisabledFallbackOrdinal:
                            faultResultSnapshot?.LastDisabledFallbackOrdinal ?? 0,
                        FaultStreamCapacityRejectCount:
                            faultResultSnapshot?.CapacityRejectCount ?? 0,
                        FaultStreamUnexpectedFailureCount:
                            faultResultSnapshot?.UnexpectedFailureCount ?? 0,
                        FaultStreamLastUnexpectedFailureOrdinal:
                            faultResultSnapshot?.LastUnexpectedFailureOrdinal ?? 0),
                    paths.ResultTicketPath);
            }
            catch
            {
                exitCode = 1;
            }

            seedContext?.Dispose();
        }

        return exitCode;
    }

    private static async Task<SeedContext> SeedAndVerifyAsync(
        string catalogDatabasePath,
        string protectedStorePath,
        Uri baseAddress,
        CancellationToken cancellationToken)
    {
        var browser = new SqliteCatalogQuery(catalogDatabasePath);
        IReadOnlyList<CatalogSourceItem> existingSources = await browser
            .ReadSourcesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existingSources.Count != 1 ||
            !string.Equals(existingSources[0].Name, ExistingCatalogSourceName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The disposable catalog prerequisite is invalid.");
        }

        CatalogChannelPage existingPage = await browser.ReadChannelsAsync(
            existingSources[0].SourceId,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 1,
            cancellationToken).ConfigureAwait(false);
        if (existingPage.TotalCount != 50_000 || existingPage.Items.Count != 1)
        {
            throw new InvalidDataException("The disposable catalog prerequisite is incomplete.");
        }

        Uri playlistUri = new(baseAddress, "/synthetic-playlist.m3u");
        Uri mediaUriA = new(baseAddress, MediaRouteA);
        Uri mediaUriB = new(baseAddress, MediaRouteB);
        var secretStore = new DpapiCurrentUserSecretStore(
            protectedStorePath,
            cancellationToken);
        string[] protectedRecordsBefore = ReadProtectedRecordInventory(protectedStorePath);
        SourceId sourceId = SourceId.Generate();
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(secretStore)
            .ProtectRemotePlaylistAsync(
                sourceId,
                PlaybackSourceName,
                playlistUri.AbsoluteUri,
                cancellationToken)
            .ConfigureAwait(false);
        if (!draft.IsSuccess)
        {
            throw new InvalidDataException("The protected source draft could not be created.");
        }

        string configurationRecordFileName = GetAddedProtectedRecordFileName(
            protectedRecordsBefore,
            ReadProtectedRecordInventory(protectedStorePath));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        if (!source.IsSuccess)
        {
            throw new InvalidDataException("The synthetic content source is invalid.");
        }

        byte[] playlist = BuildPlaylist(mediaUriA, mediaUriB);
        await using var sink = new SqliteRemoteM3uImportSink(catalogDatabasePath);
        try
        {
            DomainResult<bool> began = await sink.BeginAsync(
                source.Value!,
                entityTag: null,
                lastModified: null,
                cancellationToken).ConfigureAwait(false);
            if (!began.IsSuccess)
            {
                throw new InvalidDataException("The protected catalog seed could not begin.");
            }

            await using var content = new MemoryStream(playlist, writable: false);
            DomainResult<RemoteM3uParseResult> parsed = await RemoteM3uPlaylistParser
                .ParseToSinkAsync(content, playlistUri, sink, cancellationToken)
                .ConfigureAwait(false);
            if (!parsed.IsSuccess || parsed.Value?.ProcessedEntryCount != 2)
            {
                throw new InvalidDataException("The synthetic playlist could not be parsed.");
            }

            DomainResult<bool> completed = await sink.CompleteAsync(
                parsed.Value,
                cancellationToken).ConfigureAwait(false);
            if (!completed.IsSuccess)
            {
                throw new InvalidDataException("The protected catalog seed could not complete.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(playlist);
        }

        IReadOnlyList<CatalogSourceItem> sources = await browser
            .ReadSourcesAsync(cancellationToken)
            .ConfigureAwait(false);
        CatalogSourceItem? playbackSource = sources.SingleOrDefault(item =>
            string.Equals(item.Name, PlaybackSourceName, StringComparison.Ordinal));
        if (sources.Count != 2 || playbackSource is null ||
            !ReferenceEquals(sources[0], playbackSource))
        {
            throw new InvalidDataException("The synthetic playback source ordering is invalid.");
        }

        CatalogChannelPage playbackPage = await browser.ReadChannelsAsync(
            playbackSource.SourceId,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 2,
            cancellationToken).ConfigureAwait(false);
        if (playbackPage.TotalCount != 2 || playbackPage.Items.Count != 2)
        {
            throw new InvalidDataException("The synthetic playback channel is invalid.");
        }

        var resolver = new SqlitePlaybackSourceResolver(catalogDatabasePath, secretStore);
        (string Name, Uri Locator)[] expectedChannels =
        [
            (PlaybackChannelAName, mediaUriA),
            (PlaybackChannelBName, mediaUriB),
        ];
        foreach ((string expectedName, Uri expectedUri) in expectedChannels)
        {
            CatalogChannelItem? channel = playbackPage.Items.SingleOrDefault(item =>
                string.Equals(item.Name, expectedName, StringComparison.Ordinal));
            if (channel is null)
            {
                throw new InvalidDataException("The synthetic playback channel is invalid.");
            }

            PlaybackSourceResolutionResult resolved = await resolver.ResolveAsync(
                new PlaybackSelection(playbackSource.SourceId, channel.ChannelId),
                cancellationToken).ConfigureAwait(false);
            if (!resolved.IsSuccess)
            {
                resolved.Lease?.Dispose();
                throw new InvalidDataException("The protected playback binding is unavailable.");
            }

            byte[] expectedLocator = Encoding.UTF8.GetBytes(expectedUri.AbsoluteUri);
            try
            {
                using SecretLease lease = resolved.Lease!;
                if (!CryptographicOperations.FixedTimeEquals(expectedLocator, lease.Value.Span))
                {
                    throw new InvalidDataException("The protected playback binding is inconsistent.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedLocator);
            }
        }

        if (draft.Value!.Configuration is not RemotePlaylistSourceConfiguration configuration)
        {
            throw new InvalidDataException("The protected source configuration is invalid.");
        }

        SeedBaseline baseline = await ReadSeedBaselineAsync(
            catalogDatabasePath,
            playbackSource.SourceId,
            existingSources[0].SourceId,
            configuration.ConfigurationId,
            playbackPage.Items.Select(item => item.ChannelId).ToArray(),
            cancellationToken).ConfigureAwait(false);
        SecretStoreReadResult configurationRead = await secretStore.ReadLocatorAsync(
            playbackSource.SourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.LocatorReference,
            cancellationToken).ConfigureAwait(false);
        if (!configurationRead.IsSuccess)
        {
            configurationRead.Lease?.Dispose();
            throw new InvalidDataException("The protected source configuration is unavailable.");
        }

        byte[] expectedConfigurationDigest;
        using (SecretLease lease = configurationRead.Lease!)
        {
            expectedConfigurationDigest = SHA256.HashData(lease.Value.Span);
        }

        return new SeedContext(
            playbackSource.SourceId,
            existingSources[0].SourceId,
            configuration.ConfigurationId,
            configuration.LocatorReference,
            baseline.ConfigurationReference,
            baseline.SnapshotId,
            playbackPage.Items.Select(item => item.ChannelId).ToArray(),
            baseline.TargetGraph,
            secretStore,
            configurationRecordFileName,
            expectedConfigurationDigest);
    }

    private static string[] ReadProtectedRecordInventory(string protectedStorePath)
    {
        EnsureNoReparsePoints(protectedStorePath);
        string[] entries = Directory.EnumerateFileSystemEntries(
                protectedStorePath,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        var fileNames = new string[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            string entryPath = entries[index];
            FileAttributes attributes = File.GetAttributes(entryPath);
            string fileName = Path.GetFileName(entryPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !IsProtectedRecordFileName(fileName))
            {
                throw new IOException("The protected-store inventory is invalid.");
            }

            fileNames[index] = fileName;
        }

        Array.Sort(fileNames, StringComparer.Ordinal);
        return fileNames;
    }

    private static string GetAddedProtectedRecordFileName(
        string[] before,
        string[] after)
    {
        string[] added = after.Except(before, StringComparer.Ordinal).ToArray();
        if (added.Length != 1 ||
            after.Length != before.Length + 1 ||
            before.Except(after, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("The protected source record inventory is invalid.");
        }

        return added[0];
    }

    private static bool IsProtectedRecordFileName(string value)
    {
        const string prefix = "record-v2-";
        const string suffix = ".dpapi";
        if (value.Length != prefix.Length + 64 + suffix.Length ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in value.AsSpan(prefix.Length, 64))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<SeedBaseline> ReadSeedBaselineAsync(
        string catalogDatabasePath,
        SourceId targetSourceId,
        SourceId siblingSourceId,
        SourceConfigurationId configurationId,
        ChannelId[] channelIds,
        CancellationToken cancellationToken)
    {
        if (channelIds.Length != 2 || channelIds.Any(channelId => channelId.IsEmpty))
        {
            throw new InvalidDataException("The protected channel baseline is invalid.");
        }

        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            catalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceBindingRow? target = await ReadSourceBindingAsync(
            connection,
            transaction,
            targetSourceId,
            cancellationToken).ConfigureAwait(false);
        if (target is null ||
            target.Status != ContentSourceStatus.Ready ||
            target.ConfigurationId != configurationId.Value.ToString("N") ||
            target.SourceKind != (long)SourceKind.RemotePlaylist ||
            target.SnapshotId is null)
        {
            throw new InvalidDataException("The protected source baseline is invalid.");
        }

        TargetGraphCounts graph = await ReadTargetGraphAsync(
            connection,
            transaction,
            targetSourceId,
            target.SnapshotId.Value,
            cancellationToken).ConfigureAwait(false);
        long exactChannels = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot AND channel_id IN ($channelA, $channelB);",
            cancellationToken,
            ("$snapshot", target.SnapshotId.Value.Value.ToString("N")),
            ("$channelA", channelIds[0].Value.ToString("N")),
            ("$channelB", channelIds[1].Value.ToString("N"))).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            siblingSourceId,
            cancellationToken).ConfigureAwait(false);
        long tombstones = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM source_deletion_tombstones WHERE source_id = $source;",
            cancellationToken,
            ("$source", targetSourceId.Value.ToString("N"))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (graph is not
            {
                Snapshots: 1,
                SnapshotKeys: 1,
                Categories: 1,
                Channels: 2,
                ProtectedLocators: 2,
                Favorites: 0,
                SyncRuns: 1,
            } || exactChannels != 2 || !siblingRetained || tombstones != 0)
        {
            throw new InvalidDataException("The protected catalog baseline is invalid.");
        }

        return new SeedBaseline(target.ConfigurationReference, target.SnapshotId.Value, graph);
    }

    private static async Task<PreservationOracleResult> VerifyPreservedStateAsync(
        HarnessPaths paths,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            paths.CatalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceBindingRow? target = await ReadSourceBindingAsync(
            connection,
            transaction,
            context.TargetSourceId,
            cancellationToken).ConfigureAwait(false);
        TargetGraphCounts graph = await ReadTargetGraphAsync(
            connection,
            transaction,
            context.TargetSourceId,
            context.TargetSnapshotId,
            cancellationToken).ConfigureAwait(false);
        long exactChannels = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot AND channel_id IN ($channelA, $channelB);",
            cancellationToken,
            ("$snapshot", context.TargetSnapshotId.Value.ToString("N")),
            ("$channelA", context.TargetChannelIds[0].Value.ToString("N")),
            ("$channelB", context.TargetChannelIds[1].Value.ToString("N"))).ConfigureAwait(false);
        long tombstones = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM source_deletion_tombstones WHERE source_id = $source;",
            cancellationToken,
            ("$source", context.TargetSourceId.Value.ToString("N"))).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            context.SiblingSourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool targetPreserved = target is not null &&
            target.Status == ContentSourceStatus.Ready &&
            target.ConfigurationId == context.ConfigurationId.Value.ToString("N") &&
            target.SourceKind == (long)SourceKind.RemotePlaylist &&
            target.ConfigurationReference == context.ConfigurationReference &&
            target.SnapshotId == context.TargetSnapshotId &&
            graph == context.TargetGraph &&
            exactChannels == 2;
        bool configurationPreserved = await VerifyConfigurationRecordAsync(
            context,
            expectPresent: true,
            cancellationToken).ConfigureAwait(false);
        return new PreservationOracleResult(
            IsVerified: targetPreserved && configurationPreserved && tombstones == 0 && siblingRetained,
            TargetCatalogPreserved: targetPreserved,
            ConfigurationRecordPreserved: configurationPreserved,
            NoDeletionTombstone: tombstones == 0,
            SiblingCatalogRetained: siblingRetained);
    }

    private static FileStream OpenDeletionFaultLease(
        HarnessPaths paths,
        SeedContext context)
    {
        if (!IsProtectedRecordFileName(context.ConfigurationRecordFileName))
        {
            throw new InvalidDataException("The protected source record binding is invalid.");
        }

        string recordPath = Path.GetFullPath(Path.Combine(
            paths.ProtectedStorePath,
            context.ConfigurationRecordFileName));
        if (!string.Equals(
                Path.GetDirectoryName(recordPath),
                paths.ProtectedStorePath,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(recordPath))
        {
            throw new IOException("The protected source record is unavailable.");
        }

        EnsureNoReparsePoints(recordPath);
        FileAttributes attributes = File.GetAttributes(recordPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The protected source record is invalid.");
        }

        var options = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 4096,
        };
        var lease = new FileStream(recordPath, options);
        if (lease.Length <= 0)
        {
            lease.Dispose();
            throw new InvalidDataException("The protected source record is empty.");
        }

        return lease;
    }

    private static async Task<PendingOracleResult> VerifyPendingStateAsync(
        HarnessPaths paths,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            paths.CatalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceBindingRow? target = await ReadSourceBindingAsync(
            connection,
            transaction,
            context.TargetSourceId,
            cancellationToken).ConfigureAwait(false);
        TargetGraphCounts graph = await ReadTargetGraphAsync(
            connection,
            transaction,
            context.TargetSourceId,
            context.TargetSnapshotId,
            cancellationToken).ConfigureAwait(false);
        long exactChannels = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot AND channel_id IN ($channelA, $channelB);",
            cancellationToken,
            ("$snapshot", context.TargetSnapshotId.Value.ToString("N")),
            ("$channelA", context.TargetChannelIds[0].Value.ToString("N")),
            ("$channelB", context.TargetChannelIds[1].Value.ToString("N"))).ConfigureAwait(false);
        bool tombstoneBindingPending = await VerifyPendingTombstoneAsync(
            connection,
            transaction,
            context,
            cancellationToken).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            context.SiblingSourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool targetPreserved = target is not null &&
            target.Status == ContentSourceStatus.DeletionPending &&
            target.ConfigurationId == context.ConfigurationId.Value.ToString("N") &&
            target.SourceKind == (long)SourceKind.RemotePlaylist &&
            target.ConfigurationReference == context.ConfigurationReference &&
            target.SnapshotId == context.TargetSnapshotId &&
            graph == context.TargetGraph &&
            exactChannels == 2;
        bool configurationPreserved = await VerifyConfigurationRecordAsync(
            context,
            expectPresent: true,
            cancellationToken).ConfigureAwait(false);
        return new PendingOracleResult(
            IsVerified: targetPreserved && configurationPreserved &&
                tombstoneBindingPending && siblingRetained,
            TargetCatalogPreserved: targetPreserved,
            ConfigurationRecordPreserved: configurationPreserved,
            TombstoneBindingPending: tombstoneBindingPending,
            SiblingCatalogRetained: siblingRetained);
    }

    private static async Task<DeletionOracleResult> VerifyDeletedStateAsync(
        HarnessPaths paths,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            paths.CatalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        string source = context.TargetSourceId.Value.ToString("N");
        string snapshot = context.TargetSnapshotId.Value.ToString("N");
        bool targetCatalogDeleted =
            await CountAsync(connection, transaction, "SELECT count(*) FROM sources WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM snapshots WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM snapshots WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM snapshot_keys WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM categories WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM channels WHERE channel_id IN ($channelA, $channelB);", cancellationToken,
                ("$channelA", context.TargetChannelIds[0].Value.ToString("N")),
                ("$channelB", context.TargetChannelIds[1].Value.ToString("N"))).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM protected_locators WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM favorites WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM sync_runs WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0;

        bool tombstoneBindingCompleted = await VerifyCompletedTombstoneAsync(
            connection,
            transaction,
            context,
            cancellationToken).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            context.SiblingSourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool configurationDeleted = await VerifyConfigurationRecordAsync(
            context,
            expectPresent: false,
            cancellationToken).ConfigureAwait(false);
        return new DeletionOracleResult(
            IsVerified: targetCatalogDeleted && configurationDeleted &&
                tombstoneBindingCompleted && siblingRetained,
            TargetCatalogDeleted: targetCatalogDeleted,
            TargetProtectedRecordsDeleted: configurationDeleted,
            TombstoneBindingCompleted: tombstoneBindingCompleted,
            SiblingCatalogRetained: siblingRetained);
    }

    private static async Task<SqliteConnection> OpenReadOnlyConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SourceBindingRow?> ReadSourceBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference, status, active_snapshot_id
            FROM sources
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? snapshotValue = reader.IsDBNull(4) ? null : reader.GetString(4);
        SnapshotId? snapshotId = snapshotValue is null
            ? null
            : SnapshotId.Create(Guid.ParseExact(snapshotValue, "N")).Value;
        var row = new SourceBindingRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            (ContentSourceStatus)reader.GetInt64(3),
            snapshotId);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The source binding is not unique.");
        }

        return row;
    }

    private static async Task<TargetGraphCounts> ReadTargetGraphAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId sourceId,
        SnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        string source = sourceId.Value.ToString("N");
        string snapshot = snapshotId.Value.ToString("N");
        return new TargetGraphCounts(
            Snapshots: await CountAsync(connection, transaction, "SELECT count(*) FROM snapshots WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false),
            SnapshotKeys: await CountAsync(connection, transaction, "SELECT count(*) FROM snapshot_keys WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            Categories: await CountAsync(connection, transaction, "SELECT count(*) FROM categories WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            Channels: await CountAsync(connection, transaction, "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            ProtectedLocators: await CountAsync(connection, transaction, "SELECT count(*) FROM protected_locators WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            Favorites: await CountAsync(connection, transaction, "SELECT count(*) FROM favorites WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false),
            SyncRuns: await CountAsync(connection, transaction, "SELECT count(*) FROM sync_runs WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false));
    }

    private static async Task<bool> VerifySiblingCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId siblingSourceId,
        CancellationToken cancellationToken)
    {
        SourceBindingRow? sibling = await ReadSourceBindingAsync(
            connection,
            transaction,
            siblingSourceId,
            cancellationToken).ConfigureAwait(false);
        if (sibling is null || sibling.Status != ContentSourceStatus.Ready || sibling.SnapshotId is null)
        {
            return false;
        }

        return await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;",
            cancellationToken,
            ("$snapshot", sibling.SnapshotId.Value.Value.ToString("N"))).ConfigureAwait(false) == 50_000;
    }

    private static async Task<bool> VerifyCompletedTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference, protected_delete_completed
            FROM source_deletion_tombstones
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", context.TargetSourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        bool matches = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetString(0) == context.ConfigurationId.Value.ToString("N") &&
            reader.GetInt64(1) == (long)SourceKind.RemotePlaylist &&
            reader.GetString(2) == context.ConfigurationReference &&
            reader.GetInt64(3) == 1;
        return matches && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyPendingTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference, protected_delete_completed
            FROM source_deletion_tombstones
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", context.TargetSourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        bool matches = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetString(0) == context.ConfigurationId.Value.ToString("N") &&
            reader.GetInt64(1) == (long)SourceKind.RemotePlaylist &&
            reader.GetString(2) == context.ConfigurationReference &&
            reader.GetInt64(3) == 0;
        return matches && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyConfigurationRecordAsync(
        SeedContext context,
        bool expectPresent,
        CancellationToken cancellationToken)
    {
        SecretStoreReadResult read = await context.SecretStore.ReadLocatorAsync(
            context.TargetSourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(context.ConfigurationId),
            context.ConfigurationLocatorReference,
            cancellationToken).ConfigureAwait(false);
        if (!expectPresent)
        {
            read.Lease?.Dispose();
            return !read.IsSuccess &&
                read.Lease is null &&
                read.Failure == SecretStoreFailure.ProtectedRecordUnavailable;
        }

        if (!read.IsSuccess)
        {
            read.Lease?.Dispose();
            return false;
        }

        using SecretLease lease = read.Lease!;
        byte[] actualDigest = SHA256.HashData(lease.Value.Span);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                context.ExpectedConfigurationDigest,
                actualDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] BuildPlaylist(Uri mediaUriA, Uri mediaUriB) => Encoding.UTF8.GetBytes(
        string.Concat(
            "#EXTM3U\n",
            "#EXTINF:-1 tvg-id=\"m12-protected-a\" group-title=\"Synthetic\",",
            PlaybackChannelAName,
            "\n",
            mediaUriA.AbsoluteUri,
            "\n",
            "#EXTINF:-1 tvg-id=\"m12-protected-b\" group-title=\"Synthetic\",",
            PlaybackChannelBName,
            "\n",
            mediaUriB.AbsoluteUri,
            "\n"));

    private static byte[] BuildOnboardingPlaylist() => Encoding.UTF8.GetBytes(
        string.Concat(
            "#EXTM3U\n",
            "#EXTINF:-1 tvg-id=\"m16-onboarding-a\" group-title=\"Synthetic\",",
            PlaybackChannelAName,
            "\n",
            MediaRouteA,
            "\n",
            "#EXTINF:-1 tvg-id=\"m16-onboarding-b\" group-title=\"Synthetic\",",
            PlaybackChannelBName,
            "\n",
            MediaRouteB,
            "\n"));

    private static byte[] LoadValidatedFixture(string fixtureRoot)
    {
        string manifestPath = Path.Combine(fixtureRoot, FixtureManifestName);
        string fixturePath = Path.Combine(fixtureRoot, FixtureFileName);
        EnsureNoReparsePoints(manifestPath);
        EnsureNoReparsePoints(fixturePath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = document.RootElement;
        if (!string.Equals(root.GetProperty("FixtureId").GetString(), FixtureId, StringComparison.Ordinal) ||
            !string.Equals(
                root.GetProperty("Rights").GetProperty("License").GetString(),
                FixtureLicense,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The playback fixture manifest is invalid.");
        }

        JsonElement[] matches = root.GetProperty("Files")
            .EnumerateArray()
            .Where(file => string.Equals(
                file.GetProperty("Path").GetString(),
                FixtureFileName,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The playback fixture entry is invalid.");
        }

        byte[] fixture = File.ReadAllBytes(fixturePath);
        string expectedHash = matches[0].GetProperty("Sha256").GetString() ?? string.Empty;
        long expectedSize = matches[0].GetProperty("SizeBytes").GetInt64();
        byte[] actualHash = SHA256.HashData(fixture);
        try
        {
            byte[] expectedHashBytes = Convert.FromHexString(expectedHash);
            try
            {
                if (fixture.LongLength != expectedSize ||
                    !CryptographicOperations.FixedTimeEquals(actualHash, expectedHashBytes))
                {
                    throw new InvalidDataException("The playback fixture integrity check failed.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedHashBytes);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(fixture);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }

        return fixture;
    }

    private static HarnessPaths ValidatePaths(
        string catalogDatabasePath,
        string protectedStorePath,
        string fixtureRoot,
        string controlDirectory)
    {
        string catalogPath = NormalizeAbsolutePath(catalogDatabasePath);
        string storePath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(protectedStorePath));
        string fixturePath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(fixtureRoot));
        string controlPath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(controlDirectory));

        DirectoryInfo? catalogV2 = Directory.GetParent(catalogPath);
        DirectoryInfo? catalog = catalogV2?.Parent;
        DirectoryInfo? catalogLocalCache = catalog?.Parent;
        DirectoryInfo storeV2 = new(storePath);
        DirectoryInfo? protectedStore = storeV2.Parent;
        DirectoryInfo? storeLocalCache = protectedStore?.Parent;
        if (!File.Exists(catalogPath) ||
            !string.Equals(Path.GetFileName(catalogPath), "catalog.db", StringComparison.Ordinal) ||
            !string.Equals(catalogV2?.Name, "v2", StringComparison.Ordinal) ||
            !string.Equals(catalog?.Name, "Catalog", StringComparison.Ordinal) ||
            !Directory.Exists(storePath) ||
            !string.Equals(storeV2.Name, "v2", StringComparison.Ordinal) ||
            !string.Equals(protectedStore?.Name, "ProtectedStore", StringComparison.Ordinal) ||
            catalogLocalCache is null || storeLocalCache is null ||
            !string.Equals(catalogLocalCache.Name, "LocalCache", StringComparison.Ordinal) ||
            !string.Equals(
                catalogLocalCache.FullName,
                storeLocalCache.FullName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The disposable package paths are invalid.");
        }

        if (!Directory.Exists(fixturePath) ||
            !string.Equals(Path.GetFileName(fixturePath), "tier-a", StringComparison.Ordinal) ||
            !Directory.Exists(controlPath) ||
            Path.GetFileName(controlPath).Length != 32 ||
            Path.GetFileName(controlPath).Any(character => !Uri.IsHexDigit(character)) ||
            Directory.EnumerateFileSystemEntries(controlPath).Any())
        {
            throw new IOException("The acceptance input paths are invalid.");
        }

        EnsureNoReparsePoints(catalogPath);
        EnsureNoReparsePoints(storePath);
        EnsureNoReparsePoints(fixturePath);
        EnsureNoReparsePoints(controlPath);
        return new HarnessPaths(
            catalogPath,
            storePath,
            fixturePath,
            controlPath,
            Path.Combine(controlPath, ReadyTicketName),
            Path.Combine(controlPath, ResultTicketName),
            Path.Combine(controlPath, StopSignalName),
            Path.Combine(controlPath, CancelVerificationSignalName),
            Path.Combine(controlPath, CancelVerificationTicketName),
            Path.Combine(controlPath, DialogCloseVerificationSignalName),
            Path.Combine(controlPath, DialogCloseVerificationTicketName),
            Path.Combine(controlPath, DeletionFaultArmSignalName),
            Path.Combine(controlPath, DeletionFaultReadyTicketName),
            Path.Combine(controlPath, PendingVerificationSignalName),
            Path.Combine(controlPath, PendingVerificationTicketName),
            Path.Combine(controlPath, StreamFaultArmSignalName),
            Path.Combine(controlPath, StreamFaultReadyTicketName),
            Path.Combine(controlPath, StreamEndSignalName),
            Path.Combine(controlPath, StreamEndResultTicketName),
            Path.Combine(controlPath, StreamRestoreSignalName),
            Path.Combine(controlPath, StreamRestoreResultTicketName),
            Path.Combine(controlPath, StreamEndForCancelSignalName),
            Path.Combine(controlPath, StreamCancelReadyTicketName),
            Path.Combine(controlPath, StreamCancelVerificationSignalName),
            Path.Combine(controlPath, StreamCancelResultTicketName),
            Path.Combine(controlPath, PublicCertificateName));
    }

    private static OnboardingPaths ValidateOnboardingPaths(
        string fixtureRoot,
        string controlDirectory,
        string pipeName)
    {
        string fixturePath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(fixtureRoot));
        string controlPath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(controlDirectory));
        if (!Directory.Exists(fixturePath) ||
            !string.Equals(Path.GetFileName(fixturePath), "tier-a", StringComparison.Ordinal) ||
            !Directory.Exists(controlPath) ||
            Path.GetFileName(controlPath).Length != 32 ||
            Path.GetFileName(controlPath).Any(character => !Uri.IsHexDigit(character)) ||
            Directory.EnumerateFileSystemEntries(controlPath).Any() ||
            pipeName.Length != "iptvsuite-onboarding-".Length + 32 ||
            !pipeName.StartsWith("iptvsuite-onboarding-", StringComparison.Ordinal) ||
            pipeName["iptvsuite-onboarding-".Length..].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new IOException("The clean-install onboarding inputs are invalid.");
        }

        EnsureNoReparsePoints(fixturePath);
        EnsureNoReparsePoints(controlPath);
        return new OnboardingPaths(
            fixturePath,
            controlPath,
            pipeName,
            Path.Combine(controlPath, ReadyTicketName),
            Path.Combine(controlPath, ResultTicketName),
            Path.Combine(controlPath, StopSignalName),
            Path.Combine(controlPath, PublicCertificateName));
    }

    private static string NormalizeAbsolutePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new IOException("An absolute acceptance path is required.");
        }

        return Path.GetFullPath(value);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(path)
            : new DirectoryInfo(path);
        for (int depth = 0; current is not null && depth < 128; depth++)
        {
            current.Refresh();
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Acceptance paths cannot contain a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        if (current is not null)
        {
            throw new IOException("The acceptance path depth is invalid.");
        }
    }

    private static void WritePublicCertificate(
        X509Certificate2 certificate,
        string destinationPath)
    {
        byte[] publicCertificate = certificate.Export(X509ContentType.Cert);
        try
        {
            using var stream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(publicCertificate);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicCertificate);
        }
    }

    private static async Task<ControlledFixtureStreamSnapshot> WaitForControlledStreamSnapshotAsync(
        HarnessPaths paths,
        ControlledFixtureStreamControl control,
        IReadOnlyCollection<string> allowedNames,
        Func<ControlledFixtureStreamSnapshot, bool> predicate,
        CancellationToken cancellationToken,
        bool stopSignalIsExpected = false)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(predicate);
        string[] allowedWithStop = allowedNames.Contains(StopSignalName, StringComparer.Ordinal)
            ? [.. allowedNames]
            : [.. allowedNames, StopSignalName];
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < PhaseTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedWithStop);
            bool stopSignalObserved = TryValidateSignal(paths.StopSignalPath);
            if (stopSignalObserved != stopSignalIsExpected)
            {
                throw new InvalidDataException(
                    stopSignalIsExpected
                        ? "The acceptance protocol stop signal was missing during final stream verification."
                        : "The acceptance protocol stopped during stream verification.");
            }

            ControlledFixtureStreamSnapshot snapshot = control.Snapshot;
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The bounded controlled-stream phase timed out.");
    }

    private static async Task<StreamCancelResultTicket> VerifyNoLaterStreamOpenAsync(
        HarnessPaths paths,
        LocalHttpFixtureServer server,
        ControlledFixtureStreamControl control,
        int requestCountAtReady,
        IReadOnlyCollection<string> allowedNames,
        CancellationToken cancellationToken)
    {
        await WaitForControlledStreamSnapshotAsync(
            paths,
            control,
            allowedNames,
            snapshot =>
                snapshot.Mode == ControlledFixtureStreamMode.Holding &&
                IsExactFaultStreamAccounting(snapshot),
            cancellationToken).ConfigureAwait(false);
        if (server.RequestCount != requestCountAtReady || server.FailureCount != 0)
        {
            throw new InvalidDataException("A request escaped the exact reconnect-cancel boundary.");
        }

        string[] allowedWithStop = allowedNames.Contains(StopSignalName, StringComparer.Ordinal)
            ? [.. allowedNames]
            : [.. allowedNames, StopSignalName];
        Stopwatch observation = Stopwatch.StartNew();
        while (observation.Elapsed < NoLaterStreamOpenObservation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedWithStop);
            if (TryValidateSignal(paths.StopSignalPath))
            {
                throw new InvalidDataException("The acceptance protocol stopped during no-later-open verification.");
            }

            ControlledFixtureStreamSnapshot current = control.Snapshot;
            if (current.Mode != ControlledFixtureStreamMode.Holding ||
                !IsExactFaultStreamAccounting(current) ||
                server.RequestCount != requestCountAtReady ||
                server.FailureCount != 0)
            {
                throw new InvalidDataException("Playback reopened after reconnect cancellation.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        if (observation.Elapsed < NoLaterStreamOpenObservation ||
            server.RequestCount != requestCountAtReady ||
            server.FailureCount != 0)
        {
            throw new InvalidDataException("The no-later-open observation is incomplete.");
        }

        ControlledFixtureStreamSnapshot final = control.Snapshot;
        if (!IsExactFaultStreamFinalSnapshot(final))
        {
            throw new InvalidDataException("The controlled fault stream did not remain held exactly.");
        }

        return new StreamCancelResultTicket(
            IsVerified: true,
            IsHolding: true,
            ObservationMilliseconds: observation.ElapsedMilliseconds,
            RequestCountAtReady: requestCountAtReady,
            RequestCountAfterObservation: server.RequestCount,
            LastAssignedRequestOrdinal: final.LastAssignedRequestOrdinal,
            ActiveRequestOrdinal: final.ActiveRequestOrdinal,
            CurrentHeldRequestCount: final.CurrentHeldRequestCount,
            PeakHeldRequestCount: final.PeakHeldRequestCount,
            PeakActiveRequestCount: final.PeakActiveRequestCount,
            OverlapViolationCount: final.OverlapViolationCount,
            ExpectedCompletionCount: final.ExpectedCompletionCount,
            LastExpectedCompletionOrdinal: final.LastExpectedCompletionOrdinal,
            ExpectedAbortCount: final.ExpectedAbortCount,
            LastExpectedAbortOrdinal: final.LastExpectedAbortOrdinal,
            ExpectedRejectCount: final.ExpectedRejectCount,
            LastExpectedRejectOrdinal: final.LastExpectedRejectOrdinal,
            ClientDetachCount: final.ClientDetachCount,
            LastClientDetachOrdinal: final.LastClientDetachOrdinal,
            DisabledFallbackCount: final.DisabledFallbackCount,
            LastDisabledFallbackOrdinal: final.LastDisabledFallbackOrdinal,
            CapacityRejectCount: final.CapacityRejectCount,
            UnexpectedFailureCount: final.UnexpectedFailureCount,
            LastUnexpectedFailureOrdinal: final.LastUnexpectedFailureOrdinal);
    }

    private static bool IsExactFaultStreamFinalSnapshot(
        ControlledFixtureStreamSnapshot snapshot) =>
        snapshot.Mode == ControlledFixtureStreamMode.Holding &&
        IsExactFaultStreamAccounting(snapshot);

    private static bool IsExactFaultStreamAccounting(
        ControlledFixtureStreamSnapshot snapshot) =>
        snapshot.LastAssignedRequestOrdinal == 3 &&
        snapshot.ActiveRequestOrdinal == 0 &&
        snapshot.CurrentHeldRequestCount == 0 &&
        snapshot.PeakHeldRequestCount == 1 &&
        snapshot.PeakActiveRequestCount == 1 &&
        snapshot.OverlapViolationCount == 0 &&
        snapshot.ExpectedCompletionCount == 2 &&
        snapshot.LastExpectedCompletionOrdinal == 2 &&
        snapshot.ExpectedAbortCount == 0 &&
        snapshot.LastExpectedAbortOrdinal == 0 &&
        snapshot.ExpectedRejectCount == 0 &&
        snapshot.LastExpectedRejectOrdinal == 0 &&
        snapshot.ClientDetachCount == 1 &&
        snapshot.LastClientDetachOrdinal == 3 &&
        snapshot.DisabledFallbackCount == 0 &&
        snapshot.LastDisabledFallbackOrdinal == 0 &&
        snapshot.CapacityRejectCount == 0 &&
        snapshot.UnexpectedFailureCount == 0 &&
        snapshot.LastUnexpectedFailureOrdinal == 0;

    private static async Task<bool> WaitForPhaseSignalAsync(
        HarnessPaths paths,
        string phaseSignalPath,
        IReadOnlyCollection<string> allowedNames,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + PhaseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedNames);
            if (TryValidateSignal(paths.StopSignalPath))
            {
                return false;
            }

            if (TryValidateSignal(phaseSignalPath))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The bounded acceptance phase timed out.");
    }

    private static async Task WaitForFinalStopSignalAsync(
        HarnessPaths paths,
        IReadOnlyCollection<string> allowedNames,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + PhaseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedNames);
            if (TryValidateSignal(paths.StopSignalPath))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The bounded acceptance stop phase timed out.");
    }

    private static async Task WaitForOnboardingStopSignalAsync(
        OnboardingPaths paths,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + PhaseTimeout;
        string[] allowedNames =
        [
            ReadyTicketName,
            PublicCertificateName,
            StopSignalName,
        ];
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedNames);
            if (TryValidateSignal(paths.StopSignalPath))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The bounded onboarding stop phase timed out.");
    }

    private static bool TryValidateSignal(string signalPath)
    {
        if (Directory.Exists(signalPath))
        {
            throw new IOException("The acceptance signal is invalid.");
        }

        if (!File.Exists(signalPath))
        {
            return false;
        }

        var signal = new FileInfo(signalPath);
        signal.Refresh();
        if ((signal.Attributes & FileAttributes.ReparsePoint) != 0 || signal.Length != 0)
        {
            throw new IOException("The acceptance signal is invalid.");
        }

        return true;
    }

    private static void AssertAllowedControlEntries(
        string controlDirectory,
        IReadOnlyCollection<string> allowedNames)
    {
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        foreach (string entryPath in Directory.EnumerateFileSystemEntries(controlDirectory))
        {
            var entry = new FileInfo(entryPath);
            entry.Refresh();
            if (!entry.Exists ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !allowed.Contains(entry.Name))
            {
                throw new IOException("The acceptance control directory is invalid.");
            }
        }
    }

    private static void WriteJsonAtomically<T>(T value, string destinationPath)
    {
        string temporaryPath = destinationPath + ".tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, TicketJsonOptions);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static string NormalizeThumbprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The loopback certificate thumbprint is invalid.");
        }

        return value.ToUpperInvariant();
    }

    private sealed record HarnessPaths(
        string CatalogDatabasePath,
        string ProtectedStorePath,
        string FixtureRoot,
        string ControlDirectory,
        string ReadyTicketPath,
        string ResultTicketPath,
        string StopSignalPath,
        string CancelVerificationSignalPath,
        string CancelVerificationTicketPath,
        string DialogCloseVerificationSignalPath,
        string DialogCloseVerificationTicketPath,
        string DeletionFaultArmSignalPath,
        string DeletionFaultReadyTicketPath,
        string PendingVerificationSignalPath,
        string PendingVerificationTicketPath,
        string StreamFaultArmSignalPath,
        string StreamFaultReadyTicketPath,
        string StreamEndSignalPath,
        string StreamEndResultTicketPath,
        string StreamRestoreSignalPath,
        string StreamRestoreResultTicketPath,
        string StreamEndForCancelSignalPath,
        string StreamCancelReadyTicketPath,
        string StreamCancelVerificationSignalPath,
        string StreamCancelResultTicketPath,
        string PublicCertificatePath);

    private sealed record OnboardingPaths(
        string FixtureRoot,
        string ControlDirectory,
        string PipeName,
        string ReadyTicketPath,
        string ResultTicketPath,
        string StopSignalPath,
        string PublicCertificatePath);

    private sealed record SeedBaseline(
        string ConfigurationReference,
        SnapshotId SnapshotId,
        TargetGraphCounts TargetGraph);

    private sealed record SourceBindingRow(
        string ConfigurationId,
        long SourceKind,
        string ConfigurationReference,
        ContentSourceStatus Status,
        SnapshotId? SnapshotId);

    private sealed record TargetGraphCounts(
        long Snapshots,
        long SnapshotKeys,
        long Categories,
        long Channels,
        long ProtectedLocators,
        long Favorites,
        long SyncRuns);

    private sealed class SeedContext : IDisposable
    {
        public SeedContext(
            SourceId targetSourceId,
            SourceId siblingSourceId,
            SourceConfigurationId configurationId,
            ProtectedLocatorReference configurationLocatorReference,
            string configurationReference,
            SnapshotId targetSnapshotId,
            ChannelId[] targetChannelIds,
            TargetGraphCounts targetGraph,
            ISecretStore secretStore,
            string configurationRecordFileName,
            byte[] expectedConfigurationDigest)
        {
            TargetSourceId = targetSourceId;
            SiblingSourceId = siblingSourceId;
            ConfigurationId = configurationId;
            ConfigurationLocatorReference = configurationLocatorReference;
            ConfigurationReference = configurationReference;
            TargetSnapshotId = targetSnapshotId;
            TargetChannelIds = targetChannelIds;
            TargetGraph = targetGraph;
            SecretStore = secretStore;
            ConfigurationRecordFileName = configurationRecordFileName;
            ExpectedConfigurationDigest = expectedConfigurationDigest;
        }

        public SourceId TargetSourceId { get; }

        public SourceId SiblingSourceId { get; }

        public SourceConfigurationId ConfigurationId { get; }

        public ProtectedLocatorReference ConfigurationLocatorReference { get; }

        public string ConfigurationReference { get; }

        public SnapshotId TargetSnapshotId { get; }

        public ChannelId[] TargetChannelIds { get; }

        public TargetGraphCounts TargetGraph { get; }

        public ISecretStore SecretStore { get; }

        public string ConfigurationRecordFileName { get; }

        public byte[] ExpectedConfigurationDigest { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(ExpectedConfigurationDigest);
    }

    private sealed record ReadyTicket(
        bool IsReady,
        bool SeedCompleted,
        string CertificateThumbprint);

    private sealed record OnboardingReadyTicket(
        bool IsReady,
        string CertificateThumbprint);

    private sealed record OnboardingResultTicket(
        bool ReadyPublished,
        bool LocatorTransferred,
        bool StopObserved,
        bool StoppedGracefully,
        string? CertificateThumbprint,
        int RequestCount,
        int CompletedResponseCount,
        int FailureCount,
        int PlaylistRequestCount,
        int MediaRequestCount);

    private sealed record ResultTicket(
        bool ReadyPublished,
        bool SeedCompleted,
        bool StopObserved,
        bool StoppedGracefully,
        string? CertificateThumbprint,
        int RequestCount,
        int CompletedResponseCount,
        long CompletedBodyBytes,
        int FailureCount,
        int ChannelARequestCount,
        int ChannelBRequestCount,
        bool CancelNoMutationVerified,
        bool DialogCloseNoMutationVerified,
        bool PendingDeletionVerified,
        bool PendingTargetCatalogPreserved,
        bool PendingConfigurationRecordPreserved,
        bool PendingTombstoneBindingVerified,
        bool PendingSiblingCatalogRetained,
        bool DeletionFaultReleased,
        bool TargetCatalogDeleted,
        bool TargetProtectedRecordsDeleted,
        bool TombstoneBindingCompleted,
        bool SiblingCatalogRetained,
        bool StreamRecoveryVerified,
        bool StreamCancelVerified,
        bool StreamNoLaterOpenVerified,
        long StreamNoLaterOpenObservationMilliseconds,
        int StreamNoLaterOpenRequestCountAtReady,
        int StreamNoLaterOpenRequestCountAfterObservation,
        long NormalStreamLastAssignedRequestOrdinal,
        long NormalStreamActiveRequestOrdinal,
        int NormalStreamCurrentHeldRequestCount,
        int NormalStreamPeakHeldRequestCount,
        int NormalStreamPeakActiveRequestCount,
        int NormalStreamOverlapViolationCount,
        int NormalStreamExpectedCompletionCount,
        long NormalStreamLastExpectedCompletionOrdinal,
        int NormalStreamExpectedAbortCount,
        long NormalStreamLastExpectedAbortOrdinal,
        int NormalStreamExpectedRejectCount,
        long NormalStreamLastExpectedRejectOrdinal,
        int NormalStreamClientDetachCount,
        long NormalStreamLastClientDetachOrdinal,
        int NormalStreamDisabledFallbackCount,
        long NormalStreamLastDisabledFallbackOrdinal,
        int NormalStreamCapacityRejectCount,
        int NormalStreamUnexpectedFailureCount,
        long NormalStreamLastUnexpectedFailureOrdinal,
        bool FaultStreamHolding,
        long FaultStreamLastAssignedRequestOrdinal,
        long FaultStreamActiveRequestOrdinal,
        int FaultStreamCurrentHeldRequestCount,
        int FaultStreamPeakHeldRequestCount,
        int FaultStreamPeakActiveRequestCount,
        int FaultStreamOverlapViolationCount,
        int FaultStreamExpectedCompletionCount,
        long FaultStreamLastExpectedCompletionOrdinal,
        int FaultStreamExpectedAbortCount,
        long FaultStreamLastExpectedAbortOrdinal,
        int FaultStreamExpectedRejectCount,
        long FaultStreamLastExpectedRejectOrdinal,
        int FaultStreamClientDetachCount,
        long FaultStreamLastClientDetachOrdinal,
        int FaultStreamDisabledFallbackCount,
        long FaultStreamLastDisabledFallbackOrdinal,
        int FaultStreamCapacityRejectCount,
        int FaultStreamUnexpectedFailureCount,
        long FaultStreamLastUnexpectedFailureOrdinal);

    private sealed record PreservationOracleResult(
        bool IsVerified,
        bool TargetCatalogPreserved,
        bool ConfigurationRecordPreserved,
        bool NoDeletionTombstone,
        bool SiblingCatalogRetained);

    private sealed record DeletionFaultReadyTicket(bool IsReady);

    private sealed record StreamFaultReadyTicket(
        bool IsReady,
        int MaximumRequestOrdinals);

    private sealed record StreamEndResultTicket(
        bool IsVerified,
        long LastAssignedRequestOrdinal,
        long ActiveRequestOrdinal,
        int CurrentHeldRequestCount,
        int ExpectedCompletionCount,
        long LastExpectedCompletionOrdinal);

    private sealed record StreamRestoreResultTicket(
        bool IsVerified,
        long LastAssignedRequestOrdinal,
        long ActiveRequestOrdinal,
        int CurrentHeldRequestCount,
        int ExpectedCompletionCount,
        long LastExpectedCompletionOrdinal);

    private sealed record StreamCancelReadyTicket(
        bool IsVerified,
        long LastAssignedRequestOrdinal,
        long ActiveRequestOrdinal,
        int CurrentHeldRequestCount,
        int ExpectedCompletionCount,
        long LastExpectedCompletionOrdinal,
        int RequestCountAtReady);

    private sealed record StreamCancelResultTicket(
        bool IsVerified,
        bool IsHolding,
        long ObservationMilliseconds,
        int RequestCountAtReady,
        int RequestCountAfterObservation,
        long LastAssignedRequestOrdinal,
        long ActiveRequestOrdinal,
        int CurrentHeldRequestCount,
        int PeakHeldRequestCount,
        int PeakActiveRequestCount,
        int OverlapViolationCount,
        int ExpectedCompletionCount,
        long LastExpectedCompletionOrdinal,
        int ExpectedAbortCount,
        long LastExpectedAbortOrdinal,
        int ExpectedRejectCount,
        long LastExpectedRejectOrdinal,
        int ClientDetachCount,
        long LastClientDetachOrdinal,
        int DisabledFallbackCount,
        long LastDisabledFallbackOrdinal,
        int CapacityRejectCount,
        int UnexpectedFailureCount,
        long LastUnexpectedFailureOrdinal);

    private sealed record PendingVerificationTicket(
        bool IsVerified,
        bool TargetCatalogPreserved,
        bool ConfigurationRecordPreserved,
        bool TombstoneBindingPending,
        bool SiblingCatalogRetained,
        bool DeletionFaultReleased);

    private sealed record PendingOracleResult(
        bool IsVerified,
        bool TargetCatalogPreserved,
        bool ConfigurationRecordPreserved,
        bool TombstoneBindingPending,
        bool SiblingCatalogRetained);

    private sealed record DeletionOracleResult(
        bool IsVerified,
        bool TargetCatalogDeleted,
        bool TargetProtectedRecordsDeleted,
        bool TombstoneBindingCompleted,
        bool SiblingCatalogRetained);
}
