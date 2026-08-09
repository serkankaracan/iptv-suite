using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class QualityGateSentinelTests
{
    public const string ArmVariable = "IPTV_SUITE_ARM_QUALITY_GATE_SENTINEL";

    [TestMethod]
    public void PipelineStopsWhenSentinelIsExplicitlyArmed()
    {
        Assert.AreNotEqual(
            "1",
            Environment.GetEnvironmentVariable(ArmVariable),
            "The quality-gate sentinel was deliberately armed and must fail this test invocation.");
    }
}
