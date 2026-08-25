using System.Reflection;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaybackReconnectPolicyTests
{
    [TestMethod]
    public void DefaultsExposeTheExactBoundedSchedule()
    {
        var options = new PlaybackReconnectPolicyOptions();

        Assert.AreEqual(3, options.MaximumAttempts);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.TotalBudget);
        CollectionAssert.AreEqual(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
            },
            options.BaseDelays.ToArray());
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), options.MaximumJitter);
    }

    [TestMethod]
    public void ExactDelayIncludesOnlyTheInjectedBoundedJitter()
    {
        var policy = new PlaybackReconnectPolicy();
        DomainError failure = DomainError.Create(DomainErrorCode.StreamInterrupted);
        TimeSpan jitter = TimeSpan.FromMilliseconds(125);

        for (int completed = 0; completed < 3; completed++)
        {
            PlaybackReconnectDecision decision = policy.Evaluate(
                failure,
                completed,
                elapsed: TimeSpan.Zero,
                jitter);

            Assert.AreEqual(PlaybackReconnectDecisionKind.RetryAfterDelay, decision.Kind);
            Assert.AreEqual(completed + 1, decision.NextAttemptNumber);
            Assert.AreEqual(policy.Options.BaseDelays[completed] + jitter, decision.Delay);
            Assert.IsNull(decision.TerminalErrorCode);
        }
    }

    [TestMethod]
    public void JitterAcceptsInclusiveBoundsAndRejectsValuesOutsideThem()
    {
        var policy = new PlaybackReconnectPolicy();
        DomainError failure = DomainError.Create(DomainErrorCode.StreamInterrupted);

        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            policy.Evaluate(failure, 0, TimeSpan.Zero, TimeSpan.Zero).Delay);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(1250),
            policy.Evaluate(
                failure,
                0,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(250)).Delay);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => policy.Evaluate(
            failure,
            0,
            TimeSpan.Zero,
            TimeSpan.FromTicks(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => policy.Evaluate(
            failure,
            0,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250) + TimeSpan.FromTicks(1)));
    }

    [TestMethod]
    public void NeverAndManualFailuresDoNotAutomaticallyRetry()
    {
        var policy = new PlaybackReconnectPolicy();

        PlaybackReconnectDecision never = policy.Evaluate(
            DomainError.Create(DomainErrorCode.AuthenticationRejected),
            0,
            TimeSpan.Zero,
            TimeSpan.Zero);
        PlaybackReconnectDecision manual = policy.Evaluate(
            DomainError.Create(DomainErrorCode.PlaybackStartFailed),
            0,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.AreEqual(PlaybackReconnectDecisionKind.DoNotRetry, never.Kind);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, never.TerminalErrorCode);
        Assert.AreEqual(PlaybackReconnectDecisionKind.DoNotRetry, manual.Kind);
        Assert.AreEqual(DomainErrorCode.PlaybackStartFailed, manual.TerminalErrorCode);
    }

    [TestMethod]
    public void AttemptCapFailsClosedAsReconnectExhausted()
    {
        var policy = new PlaybackReconnectPolicy();

        PlaybackReconnectDecision decision = policy.Evaluate(
            DomainError.Create(DomainErrorCode.StreamInterrupted),
            completedAttemptCount: 3,
            elapsed: TimeSpan.FromSeconds(1),
            injectedJitter: TimeSpan.Zero);

        AssertExhausted(decision);
    }

    [TestMethod]
    public void DelayMustLeavePositiveAttemptTimeInsideTheTotalBoundary()
    {
        var policy = new PlaybackReconnectPolicy();
        DomainError failure = DomainError.Create(DomainErrorCode.StreamInterrupted);

        PlaybackReconnectDecision justBelowBoundary = policy.Evaluate(
            failure,
            completedAttemptCount: 0,
            elapsed: TimeSpan.FromSeconds(29) - TimeSpan.FromTicks(1),
            injectedJitter: TimeSpan.Zero);
        PlaybackReconnectDecision exactBoundary = policy.Evaluate(
            failure,
            completedAttemptCount: 0,
            elapsed: TimeSpan.FromSeconds(29),
            injectedJitter: TimeSpan.Zero);
        PlaybackReconnectDecision crossedBoundary = policy.Evaluate(
            failure,
            completedAttemptCount: 0,
            elapsed: TimeSpan.FromSeconds(29) + TimeSpan.FromTicks(1),
            injectedJitter: TimeSpan.Zero);

        Assert.AreEqual(PlaybackReconnectDecisionKind.RetryAfterDelay, justBelowBoundary.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(1), justBelowBoundary.Delay);
        AssertExhausted(exactBoundary);
        AssertExhausted(crossedBoundary);
    }

    [TestMethod]
    public void InvalidOptionsFailFast()
    {
        TimeSpan[] exactSchedule =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
        ];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(0, TimeSpan.FromSeconds(30), [], TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(4, TimeSpan.FromSeconds(30), exactSchedule, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(3, TimeSpan.Zero, exactSchedule, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(
                3,
                TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1),
                exactSchedule,
                TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new PlaybackReconnectPolicyOptions(3, TimeSpan.FromSeconds(30), null!, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PlaybackReconnectPolicyOptions(
                3,
                TimeSpan.FromSeconds(30),
                exactSchedule[..2],
                TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(
                3,
                TimeSpan.FromSeconds(30),
                [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)],
                TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(
                3,
                TimeSpan.FromSeconds(30),
                exactSchedule,
                TimeSpan.FromTicks(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlaybackReconnectPolicyOptions(
                3,
                TimeSpan.FromSeconds(30),
                exactSchedule,
                TimeSpan.FromMilliseconds(250) + TimeSpan.FromTicks(1)));
    }

    [TestMethod]
    public void InvalidPolicyInputsFailFast()
    {
        var policy = new PlaybackReconnectPolicy();
        DomainError failure = DomainError.Create(DomainErrorCode.StreamInterrupted);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            policy.Evaluate(failure, -1, TimeSpan.Zero, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            policy.Evaluate(failure, 4, TimeSpan.Zero, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            policy.Evaluate(failure, 0, TimeSpan.FromTicks(-1), TimeSpan.Zero));
    }

    [TestMethod]
    public void UnknownCodeAndInvalidRetryabilityFailClosedWithoutLeakingContext()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("RECONNECT-POLICY");
        var policy = new PlaybackReconnectPolicy();
        DomainError invalidRetryability = CreateDomainError(
            DomainErrorCode.StreamInterrupted,
            (DomainRetryability)int.MaxValue,
            sensitive);
        DomainError mismatchedRetryability = CreateDomainError(
            DomainErrorCode.AuthenticationRejected,
            DomainRetryability.BoundedTransient,
            "Errors.Authentication.Rejected");
        DomainError mismatchedResourceKey = CreateDomainError(
            DomainErrorCode.StreamInterrupted,
            DomainRetryability.BoundedTransient,
            sensitive);
        DomainError unknownCode = CreateDomainError(
            (DomainErrorCode)int.MaxValue,
            DomainRetryability.BoundedTransient,
            sensitive);

        PlaybackReconnectDecision invalidRetryabilityDecision = policy.Evaluate(
            invalidRetryability,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero);
        PlaybackReconnectDecision unknownCodeDecision = policy.Evaluate(
            unknownCode,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero);
        PlaybackReconnectDecision mismatchedRetryabilityDecision = policy.Evaluate(
            mismatchedRetryability,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero);
        PlaybackReconnectDecision mismatchedResourceKeyDecision = policy.Evaluate(
            mismatchedResourceKey,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.AreEqual(
            PlaybackReconnectDecisionKind.DoNotRetry,
            invalidRetryabilityDecision.Kind);
        Assert.AreEqual(
            DomainErrorCode.DomainInvariantViolation,
            invalidRetryabilityDecision.TerminalErrorCode);
        Assert.AreEqual(PlaybackReconnectDecisionKind.DoNotRetry, unknownCodeDecision.Kind);
        Assert.AreEqual(
            DomainErrorCode.DomainInvariantViolation,
            unknownCodeDecision.TerminalErrorCode);
        Assert.AreEqual(
            DomainErrorCode.DomainInvariantViolation,
            mismatchedRetryabilityDecision.TerminalErrorCode);
        Assert.AreEqual(
            DomainErrorCode.DomainInvariantViolation,
            mismatchedResourceKeyDecision.TerminalErrorCode);
        SecurityTestAssertions.DoesNotContainSensitive(
            string.Join(
                '|',
                invalidRetryabilityDecision,
                unknownCodeDecision,
                mismatchedRetryabilityDecision,
                mismatchedResourceKeyDecision,
                JsonSerializer.Serialize(invalidRetryabilityDecision),
                JsonSerializer.Serialize(unknownCodeDecision),
                JsonSerializer.Serialize(mismatchedRetryabilityDecision),
                JsonSerializer.Serialize(mismatchedResourceKeyDecision)),
            sensitive);
    }

    [TestMethod]
    public void PublicSurfaceAndSerializationContainOnlyBoundedDecisionFacts()
    {
        var policy = new PlaybackReconnectPolicy();
        PlaybackReconnectDecision decision = policy.Evaluate(
            DomainError.Create(DomainErrorCode.StreamInterrupted),
            1,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25));

        string observable = string.Join(
            '|',
            policy,
            policy.Options,
            decision,
            JsonSerializer.Serialize(policy.Options),
            JsonSerializer.Serialize(decision));

        StringAssert.Contains(observable, "RetryAfterDelay");
        Assert.IsFalse(observable.Contains(nameof(Exception), StringComparison.Ordinal));
        Assert.IsFalse(observable.Contains("Uri", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(typeof(PlaybackReconnectPolicy)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(string) ||
                parameter.ParameterType == typeof(Uri) ||
                typeof(Exception).IsAssignableFrom(parameter.ParameterType)));
    }

    [TestMethod]
    public void DecisionConstructorRejectsEveryInvalidInvariantShape()
    {
        ConstructorInfo constructor = GetDecisionConstructor();
        object?[][] invalidArguments =
        [
            [(PlaybackReconnectDecisionKind)int.MaxValue, 0, TimeSpan.Zero, null],
            [PlaybackReconnectDecisionKind.DoNotRetry, 0, TimeSpan.Zero, null],
            [PlaybackReconnectDecisionKind.DoNotRetry, 0, TimeSpan.Zero, (DomainErrorCode)int.MaxValue],
            [PlaybackReconnectDecisionKind.DoNotRetry, 1, TimeSpan.Zero, DomainErrorCode.StreamInterrupted],
            [PlaybackReconnectDecisionKind.DoNotRetry, 0, TimeSpan.FromTicks(1), DomainErrorCode.StreamInterrupted],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 0, TimeSpan.FromSeconds(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 4, TimeSpan.FromSeconds(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 1, TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 1, TimeSpan.FromMilliseconds(1250) + TimeSpan.FromTicks(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 2, TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 2, TimeSpan.FromMilliseconds(2250) + TimeSpan.FromTicks(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 3, TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 3, TimeSpan.FromMilliseconds(4250) + TimeSpan.FromTicks(1), null],
            [PlaybackReconnectDecisionKind.RetryAfterDelay, 1, TimeSpan.FromSeconds(1), DomainErrorCode.StreamInterrupted],
            [PlaybackReconnectDecisionKind.Exhausted, 1, TimeSpan.Zero, DomainErrorCode.ReconnectExhausted],
            [PlaybackReconnectDecisionKind.Exhausted, 0, TimeSpan.FromTicks(1), DomainErrorCode.ReconnectExhausted],
            [PlaybackReconnectDecisionKind.Exhausted, 0, TimeSpan.Zero, DomainErrorCode.StreamInterrupted],
        ];

        foreach (object?[] arguments in invalidArguments)
        {
            TargetInvocationException exception = Assert.ThrowsExactly<TargetInvocationException>(
                () => constructor.Invoke(arguments));
            Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
        }

        (int Attempt, TimeSpan BaseDelay)[] validRetryBounds =
        [
            (1, TimeSpan.FromSeconds(1)),
            (2, TimeSpan.FromSeconds(2)),
            (3, TimeSpan.FromSeconds(4)),
        ];
        foreach ((int attempt, TimeSpan baseDelay) in validRetryBounds)
        {
            var lower = (PlaybackReconnectDecision)constructor.Invoke(
                [PlaybackReconnectDecisionKind.RetryAfterDelay, attempt, baseDelay, null]);
            var upper = (PlaybackReconnectDecision)constructor.Invoke(
                [
                    PlaybackReconnectDecisionKind.RetryAfterDelay,
                    attempt,
                    baseDelay + PlaybackReconnectPolicyOptions.MaximumAllowedJitter,
                    null,
                ]);

            Assert.AreEqual(baseDelay, lower.Delay);
            Assert.AreEqual(
                baseDelay + PlaybackReconnectPolicyOptions.MaximumAllowedJitter,
                upper.Delay);
        }
    }

    private static void AssertExhausted(PlaybackReconnectDecision decision)
    {
        Assert.AreEqual(PlaybackReconnectDecisionKind.Exhausted, decision.Kind);
        Assert.AreEqual(0, decision.NextAttemptNumber);
        Assert.AreEqual(TimeSpan.Zero, decision.Delay);
        Assert.AreEqual(DomainErrorCode.ReconnectExhausted, decision.TerminalErrorCode);
    }

    private static DomainError CreateDomainError(
        DomainErrorCode code,
        DomainRetryability retryability,
        string resourceKey)
    {
        ConstructorInfo constructor = typeof(DomainError).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(DomainErrorCode) &&
                    parameters[1].ParameterType == typeof(DomainRetryability) &&
                    parameters[2].ParameterType == typeof(string);
            });
        return (DomainError)constructor.Invoke([code, retryability, resourceKey]);
    }

    private static ConstructorInfo GetDecisionConstructor() =>
        typeof(PlaybackReconnectDecision).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(PlaybackReconnectDecisionKind) &&
                    parameters[1].ParameterType == typeof(int) &&
                    parameters[2].ParameterType == typeof(TimeSpan) &&
                    parameters[3].ParameterType == typeof(DomainErrorCode?);
            });
}
