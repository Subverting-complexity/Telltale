using Telltale.Collector.Interop;

namespace Collector.Tests;

public class NtDefsTests
{
    [Fact]
    public void StructLayout_ValidatesCorrectly()
    {
        Assert.True(NtDefs.ValidateLayout());
    }
}
