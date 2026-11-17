using WyvernCenter.Core.Bridge;

namespace WyvernCenter.Tests;

public class PipeServerCheckTests
{
    [Fact]
    public void ServerIsServiceProcess_Accepted() => PipeServerCheck.EnsureTrusted(serverPid: 4242, servicePid: 4242);

    [Fact]
    public void ServerIsAnotherProcess_Rejected() =>
        Assert.Throws<UnauthorizedAccessException>(() => PipeServerCheck.EnsureTrusted(serverPid: 666, servicePid: 4242));

    [Theory]
    [InlineData(0u, 4242u)]
    [InlineData(4242u, 0u)]
    public void UnknownPid_Rejected(uint server, uint service) =>
        Assert.Throws<UnauthorizedAccessException>(() => PipeServerCheck.EnsureTrusted(server, service));
}
