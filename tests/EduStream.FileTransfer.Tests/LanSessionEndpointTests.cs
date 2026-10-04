using EduStream.Core.Collaboration;
using EduStream.Core.Network;

namespace EduStream.FileTransfer.Tests;

public sealed class LanSessionEndpointTests
{
    [Fact]
    public void DefaultSessionAndSecurePorts_Are5000And5001()
    {
        var endpoint = LanSessionEndpoint.Create("192.168.0.10");
        Assert.Equal("192.168.0.10", endpoint.Address.ToString());
        Assert.Equal(5000, endpoint.Port);
        Assert.Equal(5001, CollaborationPorts.ForSession(endpoint.Port));
    }

    [Theory]
    [InlineData("127.0.0.1", 1)]
    [InlineData("::1", 65534)]
    [InlineData("192.168.1.20", 5000)]
    public void ValidEndpoint_PreservesAddressAndPort(string host, int port)
    {
        var endpoint = LanSessionEndpoint.Create(host, port);
        Assert.Equal(host, endpoint.Address.ToString());
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("", 5000)]
    [InlineData("not-an-ip", 5000)]
    [InlineData("0.0.0.0", 5000)]
    [InlineData("::", 5000)]
    [InlineData("255.255.255.255", 5000)]
    [InlineData("224.0.0.1", 5000)]
    [InlineData("ff02::1", 5000)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", -1)]
    [InlineData("127.0.0.1", 65535)]
    public void InvalidEndpoint_IsRejectedWithCommonError(string host, int port)
    {
        var error = Assert.Throws<CollaborationException>(() => LanSessionEndpoint.Create(host, port));
        Assert.Equal(CollaborationError.InvalidRequest, error.Code);
    }
}
