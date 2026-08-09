namespace IptvSuite.Domain;

public sealed class DomainResult<T>
{
    internal DomainResult(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        IsSuccess = true;
        Value = value;
    }

    internal DomainResult(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public DomainError? Error { get; }

    public override string ToString() => IsSuccess
        ? "[DOMAIN-RESULT:SUCCESS]"
        : $"[DOMAIN-RESULT:{Error!.Code}]";
}

public static class DomainResult
{
    public static DomainResult<T> Success<T>(T value) => new(value);

    public static DomainResult<T> Failure<T>(DomainErrorCode code) => new(DomainError.Create(code));

    public static DomainResult<T> Failure<T>(DomainError error) => new(error);
}
