using System.Buffers.Binary;
using System.Security.Cryptography;
using IptvSuite.Testing;

namespace IptvSuite.ProtectedCatalogSpike;

/// <summary>
/// Reproduces the historical synthetic M4 workload. It is test-only and never accepts production input.
/// </summary>
internal static class DeterministicPayloadGenerator
{
    internal const int PayloadByteLength = 256;

    private static readonly byte[] CanaryBytes = CreateCanaryBytes();

    private static ReadOnlySpan<byte> InvalidDomainPrefix =>
        "https://synthetic-m4-spike.invalid/protected-value/"u8;

    internal static void Fill(
        Span<byte> destination,
        SpikeSpecification specification,
        int recordCount,
        int iteration,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (destination.Length != PayloadByteLength)
        {
            throw new ArgumentException("The fixed spike payload length is required.", nameof(destination));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iteration);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, recordCount);

        Span<byte> input = stackalloc byte[sizeof(int) * 6];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        int offset = 0;
        int counter = 0;

        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(input, specification.Seed);
            BinaryPrimitives.WriteInt32LittleEndian(input[sizeof(int)..], specification.AlgorithmVersion);
            BinaryPrimitives.WriteInt32LittleEndian(input[(sizeof(int) * 2)..], recordCount);
            BinaryPrimitives.WriteInt32LittleEndian(input[(sizeof(int) * 3)..], iteration);
            BinaryPrimitives.WriteInt32LittleEndian(input[(sizeof(int) * 4)..], ordinal);

            while (offset < destination.Length)
            {
                BinaryPrimitives.WriteInt32LittleEndian(input[(sizeof(int) * 5)..], counter++);
                SHA256.HashData(input, digest);
                int count = Math.Min(digest.Length, destination.Length - offset);
                digest[..count].CopyTo(destination[offset..]);
                offset += count;
            }

            InvalidDomainPrefix.CopyTo(destination);
            CanaryBytes.CopyTo(destination[InvalidDomainPrefix.Length..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static byte[] CreateCanaryBytes()
    {
        TestCanary canary = TestCanary.Create("M4-SPIKE", "PAYLOAD-V1");
        using var stream = new MemoryStream(capacity: 128);
        canary.WriteTo(stream, TestCanaryEncoding.Utf8);
        return stream.ToArray();
    }
}
