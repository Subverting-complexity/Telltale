using Telltale.Collector;

namespace Collector.Tests;

public class NativeSamplerTests
{
    [Fact]
    public void Sample_ReturnsProcesses()
    {
        var sampler = new NativeSampler();
        var results = sampler.Sample();
        Assert.NotEmpty(results);
        Assert.Contains(results, p => p.Pid == Environment.ProcessId);
    }

    [Fact]
    public void Sample_IncludesProcessNames()
    {
        var sampler = new NativeSampler();
        var results = sampler.Sample();
        var self = results.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.NotNull(self);
        Assert.False(string.IsNullOrEmpty(self.Name));
    }
}
