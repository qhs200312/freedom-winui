namespace ServiceLib.Services;

public sealed record NowhereBridge(ProfileItem Node, ProfileItem SocksNode)
{
    public string VectorUri => NowhereFmt.ToVectorUri(Node, SocksNode.Port, SocksNode.Username, SocksNode.Password);

    public static NowhereBridge Create(ProfileItem node, IEnumerable<int> occupiedPorts, int inboundPort = 10808)
    {
        var used = occupiedPorts.ToHashSet();
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(20000, 65000);
            if (used.Contains(port) || (port >= inboundPort && port <= inboundPort + 32)
                || !Utils.IsTcpPortAvailable(port, IPAddress.Loopback)
                || !Utils.IsUdpPortAvailable(port, IPAddress.Loopback)) continue;
            return new(JsonUtils.DeepCopy(node), new ProfileItem
            {
                IndexId = node.IndexId, Remarks = node.Remarks, Subid = node.Subid, IsSub = node.IsSub,
                ConfigType = EConfigType.SOCKS, CoreType = node.CoreType,
                Address = Global.Loopback, Port = port,
                Username = "nowhere", Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
            });
        }
        throw new IOException("Unable to allocate a local TCP/UDP port for Nowhere.");
    }
}
