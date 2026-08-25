using System.Reflection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Viewer.Tests;

public sealed class TestFactoryGuardTests
{
    [Fact]
    public void AllFixtures_UseTelltaleTestFactory()
    {
        var assembly = typeof(TestFactoryGuardTests).Assembly;
        var waFactory = typeof(WebApplicationFactory<Program>);
        var testFactory = typeof(TelltaleTestFactory);

        var violations = assembly.GetTypes()
            .SelectMany(t => t.GetInterfaces(), (t, iface) => (Type: t, Interface: iface))
            .Where(x => x.Interface.IsGenericType
                     && x.Interface.GetGenericTypeDefinition() == typeof(IClassFixture<>))
            .Select(x => (x.Type, FixtureArg: x.Interface.GetGenericArguments()[0]))
            .Where(x => waFactory.IsAssignableFrom(x.FixtureArg)
                     && !testFactory.IsAssignableFrom(x.FixtureArg))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Test classes must use TelltaleTestFactory (or a subclass) rather than "
            + "WebApplicationFactory<Program> directly, so every test runs against an "
            + "isolated database:\n"
            + string.Join("\n", violations.Select(v =>
                $"  {v.Type.Name} uses IClassFixture<{v.FixtureArg.Name}>")));
    }
}
