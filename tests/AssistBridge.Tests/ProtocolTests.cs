using System.Text.Json.Nodes;
using AssistBridge.Core;

namespace AssistBridge.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData("nvdaremote://nvdaremote.com?key=abc123&mode=master", "nvdaremote.com", 6837, "abc123", ConnectionMode.Leader)]
    [InlineData("nvdaremote://example.org:1234/?key=k%20y&mode=slave", "example.org", 1234, "k y", ConnectionMode.Follower)]
    [InlineData("nvdaremote://[::1]:7000?key=z&mode=slave", "::1", 7000, "z", ConnectionMode.Follower)]
    [InlineData("NVDAREMOTE://host?key=a+b&mode=MASTER", "host", 6837, "a b", ConnectionMode.Leader)]
    public void ParsesNvdaLinks(string url, string host, int port, string key, ConnectionMode mode)
    {
        var info = ConnectionInfo.FromUrl(url);
        Assert.Equal(host, info.Host);
        Assert.Equal(port, info.Port);
        Assert.Equal(key, info.Key);
        Assert.Equal(mode, info.Mode);
    }

    [Theory]
    [InlineData("http://x?key=a&mode=master")]
    [InlineData("nvdaremote://host?mode=master")]
    [InlineData("nvdaremote://host?key=a")]
    [InlineData("nvdaremote://host?key=a&mode=boss")]
    public void RejectsInvalidLinks(string url) => Assert.Throws<FormatException>(() => ConnectionInfo.FromUrl(url));

    [Fact]
    public void BuildsLinksLikeNvda()
    {
        // NVDA: ParseResult(scheme, netloc, "", "", urlencode({key, mode}), "").geturl()
        var info = new ConnectionInfo("nvdaremote.com", 6837, "abc", ConnectionMode.Leader);
        Assert.Equal("nvdaremote://nvdaremote.com?key=abc&mode=master", info.GetUrl());
        Assert.Equal("nvdaremote://nvdaremote.com?key=abc&mode=slave", info.GetUrlToConnect());
        var v6 = new ConnectionInfo("::1", 7000, "k", ConnectionMode.Follower);
        Assert.Equal("nvdaremote://[::1]:7000?key=k&mode=master", v6.GetUrlToConnect());
        Assert.Equal(info, ConnectionInfo.FromUrl(info.GetUrl()));
    }

    [Theory]
    [InlineData("nvdaremote.com", "nvdaremote.com", 6837)]
    [InlineData("example.org:1234", "example.org", 1234)]
    [InlineData("[2001:db8::1]:99", "2001:db8::1", 99)]
    [InlineData("10.0.0.5", "10.0.0.5", 6837)]
    public void ParsesAddresses(string address, string host, int port)
    {
        Assert.Equal((host, port), Protocol.AddressToHostPort(address));
    }

    [Fact]
    public void FormatsAddressesLikeNvda()
    {
        Assert.Equal("nvdaremote.com", Protocol.HostPortToAddress("nvdaremote.com", 6837));
        Assert.Equal("host:1", Protocol.HostPortToAddress("host", 1));
        Assert.Equal("[::1]:5", Protocol.HostPortToAddress("::1", 5));
    }

    [Fact]
    public void LocalKeysAreSevenDigits()
    {
        for (var i = 0; i < 100; i++)
        {
            var key = Protocol.GenerateLocalKey();
            Assert.Matches("^[1-8][0-8]{6}$", key);
        }
    }

    [Fact]
    public void ExtractsSpokenTextFromSerialisedSequences()
    {
        // As produced by NVDA's SpeechCommandJSONEncoder: commands are [className, fields].
        var seq = JsonNode.Parse("""["Desktop", ["EndUtteranceCommand", {}], "list", ["PitchCommand", {"offset": 30}], " A ", ["IndexCommand", {"index": 4}]]""")!.AsArray();
        Assert.Equal("Desktop list A", SessionController.SpeechText(seq));
    }

    [Fact]
    public void MachineKeysAreEncryptedAtRest()
    {
        var dir = TestUtil.TempDirectory("settings");
        var store = new SettingsStore(dir);
        store.Current.Machines.Add(new Machine { Name = "Home", Host = "nvdaremote.com", Key = "secret-key" });
        store.Save();
        var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Assert.DoesNotContain("secret-key", json);
        Assert.Contains("dpapi:", json);
        var reloaded = new SettingsStore(dir);
        Assert.Equal("secret-key", reloaded.Current.Machines.Single().Key);
    }

    [Fact]
    public void MotdIsShownOncePerServer()
    {
        var store = new SettingsStore(TestUtil.TempDirectory("motd"));
        Assert.True(store.ShouldDisplayMotd("h", 1, "hello"));
        Assert.False(store.ShouldDisplayMotd("h", 1, "hello"));
        Assert.True(store.ShouldDisplayMotd("h", 2, "hello"));
        Assert.True(store.ShouldDisplayMotd("h", 1, "changed"));
    }
}
