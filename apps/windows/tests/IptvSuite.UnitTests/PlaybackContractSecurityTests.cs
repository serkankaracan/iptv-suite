using System.Reflection;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackContractSecurityTests
{
    [TestMethod]
    public void PublicPlaybackContractsExposeOnlySafeEngineNeutralTypes()
    {
        Type[] playbackTypes = typeof(IPlaybackEngine).Assembly
            .GetExportedTypes()
            .Where(type =>
                type.Namespace == typeof(IPlaybackEngine).Namespace &&
                (type.Name.StartsWith("Playback", StringComparison.Ordinal) ||
                    type == typeof(IPlaybackEngine)))
            .ToArray();
        string[] forbiddenTypeFragments =
        [
            "System.Uri",
            "System.ReadOnlyMemory",
            "SecretLease",
            "ProtectedLocatorReference",
            "Microsoft.UI",
            "Windows.Media",
            "MediaPlayer",
            "MediaSource",
            "NativePlaybackCompatibilitySpike",
        ];

        Assert.IsGreaterThan(0, playbackTypes.Length);
        foreach (Type playbackType in playbackTypes)
        {
            IEnumerable<Type> observableTypes = playbackType
                .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                .SelectMany(GetObservableTypes)
                .Append(playbackType)
                .SelectMany(ExpandType);
            string surface = string.Join('|', observableTypes.Select(type => type.FullName));
            foreach (string forbidden in forbiddenTypeFragments)
            {
                Assert.IsFalse(
                    surface.Contains(forbidden, StringComparison.Ordinal),
                    $"Public playback contract {playbackType.Name} exposes forbidden type {forbidden}.");
            }
        }
    }

    [TestMethod]
    public void PlaybackObservableSurfacesContainNoDiagnosticContext()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("PLAYBACK-CONTRACT");
        var selection = new PlaybackSelection(SourceId.Generate(), ChannelId.Generate());
        PlaybackEngineSnapshot snapshot = PlaybackEngineSnapshot.Closed();
        var eventArgs = new PlaybackEngineStateChangedEventArgs(snapshot);
        PlaybackEngineOperationResult result = PlaybackEngineOperationResult.Failed(
            DomainErrorCode.PlaybackStartFailed);
        PlaybackSessionId sessionId = CreateSessionId();
        PlaybackControlSnapshot controls = PlaybackControlSnapshot.Active(
            sessionId,
            PlaybackVolume.FromPercent(42),
            isMuted: true,
            PlaybackAspectMode.Fill);
        PlaybackTrackId trackId = PlaybackTrackId.Create(
            sessionId,
            PlaybackTrackKind.Audio,
            ordinal: 1);
        PlaybackTrackSnapshot tracks = PlaybackTrackSnapshot.Create(
            sessionId,
            PlaybackTrackCapabilities.AudioSelection,
            [new PlaybackTrackInfo(trackId, isSelected: true, isSelectable: true)]);
        DomainResult<PlaybackTrackSnapshot> trackResult = DomainResult.Success(tracks);

        string observable = string.Join(
            '|',
            selection,
            snapshot,
            eventArgs,
            result,
            controls,
            trackId,
            tracks,
            trackResult,
            JsonSerializer.Serialize(selection),
            JsonSerializer.Serialize(snapshot),
            JsonSerializer.Serialize(eventArgs),
            JsonSerializer.Serialize(result),
            JsonSerializer.Serialize(controls),
            JsonSerializer.Serialize(trackId),
            JsonSerializer.Serialize(tracks),
            JsonSerializer.Serialize(trackResult));

        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive);
        Assert.IsFalse(observable.Contains("Exception", StringComparison.Ordinal));
        Assert.IsFalse(observable.Contains("HResult", StringComparison.Ordinal));
        Assert.IsFalse(observable.Contains("Diagnostic", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReconnectSnapshotSurfaceAndSerializationContainOnlySafeBoundedFacts()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("RECONNECT-SNAPSHOT");
        PlaybackReconnectCorrelationId correlation = PlaybackReconnectCorrelationId.FromSequence(7);
        ConstructorInfo constructor = typeof(PlaybackReconnectSnapshot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 7);
        var waiting = (PlaybackReconnectSnapshot)constructor.Invoke(
            [
                PlaybackReconnectPhase.Waiting,
                correlation,
                2,
                3,
                TimeSpan.FromMilliseconds(2125),
                TimeSpan.FromSeconds(24),
                null,
            ]);
        var terminal = (PlaybackReconnectSnapshot)constructor.Invoke(
            [
                PlaybackReconnectPhase.Exhausted,
                correlation,
                3,
                3,
                TimeSpan.Zero,
                TimeSpan.Zero,
                DomainErrorCode.ReconnectExhausted,
            ]);
        var eventArgs = new PlaybackReconnectSnapshotChangedEventArgs(waiting);

        string observable = string.Join(
            '|',
            waiting,
            terminal,
            eventArgs,
            JsonSerializer.Serialize(waiting),
            JsonSerializer.Serialize(terminal),
            JsonSerializer.Serialize(eventArgs));

        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive);
        Type[] propertyTypes = typeof(PlaybackReconnectSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.PropertyType)
            .ToArray();
        Assert.IsFalse(propertyTypes.Contains(typeof(string)));
        Assert.IsFalse(propertyTypes.Contains(typeof(Uri)));
        Assert.IsFalse(propertyTypes.Any(type => typeof(Exception).IsAssignableFrom(type)));
        Assert.IsFalse(propertyTypes.Any(type => typeof(Delegate).IsAssignableFrom(type)));
        Assert.IsFalse(observable.Contains(nameof(Exception), StringComparison.Ordinal));
        Assert.IsFalse(observable.Contains("Uri", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PlaybackSnapshotFactoriesRejectContradictoryState()
    {
        PlaybackSessionId sessionId = CreateSessionId();
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackEngineSnapshot.Active(default, PlaybackState.Opening));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Closed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Failed));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackEngineSnapshot.Active(sessionId, (PlaybackState)int.MaxValue));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackEngineSnapshot.Failed(
                default,
                DomainError.Create(DomainErrorCode.PlaybackStartFailed)));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            PlaybackEngineSnapshot.Failed(sessionId, null!));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackEngineSnapshot.Closed(default));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            PlaybackEngineOperationResult.Failed(null!));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PlaybackSelection(default, ChannelId.Generate()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PlaybackSelection(SourceId.Generate(), default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackVolume.FromPercent(PlaybackVolume.MinimumPercent - 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackVolume.FromPercent(PlaybackVolume.MaximumPercent + 1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PlaybackControlSnapshot.Active(
                default,
                PlaybackVolume.FromPercent(100),
                isMuted: false,
                PlaybackAspectMode.Fit));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlaybackControlSnapshot.Active(
                sessionId,
                PlaybackVolume.FromPercent(100),
                isMuted: false,
                (PlaybackAspectMode)int.MaxValue));
    }

    private static IEnumerable<Type> GetObservableTypes(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        EventInfo eventInfo => [eventInfo.EventHandlerType!],
        MethodInfo method => method.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        _ => [],
    };

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is Type elementType)
        {
            foreach (Type expanded in ExpandType(elementType))
            {
                yield return expanded;
            }
        }

        foreach (Type genericArgument in type.GetGenericArguments())
        {
            foreach (Type expanded in ExpandType(genericArgument))
            {
                yield return expanded;
            }
        }
    }

    private static PlaybackSessionId CreateSessionId()
    {
        MethodInfo factory = typeof(PlaybackSessionId).GetMethod(
            "FromSequence",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (PlaybackSessionId)factory.Invoke(null, [1L])!;
    }
}
