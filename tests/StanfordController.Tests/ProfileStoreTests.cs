using StanfordController.Core.Profiles;

namespace StanfordController.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void SavedProfilesLoadBack()
    {
        var store = new ProfileStore(Path.Combine(_directory, "nested", "instruments.json"));
        Assert.Empty(store.Load());

        var profile = new InstrumentProfile { Name = "Laser lab", Host = "192.168.1.50", Location = "Room 101", Notes = "Rb timebase" };
        store.Save([profile]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal("Laser lab", loaded.Name);
        Assert.Equal("192.168.1.50", loaded.Host);
        Assert.Equal(5025, loaded.Port);
        Assert.Equal("Room 101", loaded.Location);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void ValidationRequiresAUniqueNameAndAValidHost()
    {
        var existing = new InstrumentProfile { Name = "Laser lab", Host = "10.0.0.2" };

        var duplicate = new InstrumentProfile { Name = " laser LAB ", Host = "10.0.0.3" };
        Assert.Contains(duplicate.Validate([existing]), p => p.Contains("already called"));

        var badHost = new InstrumentProfile { Name = "Other", Host = "not a host!" };
        Assert.Contains(badHost.Validate([existing]), p => p.Contains("not a valid"));

        var good = new InstrumentProfile { Name = "Other", Host = "dg645-02.lab.local" };
        Assert.Empty(good.Validate([existing]));

        // Editing a profile must not clash with itself.
        Assert.Empty(existing.Validate([existing]));
    }

    [Fact]
    public void SimulatorProfilesNeedNoHost()
    {
        var demo = new InstrumentProfile { Name = "Demo", IsSimulator = true };
        Assert.Empty(demo.Validate([]));
        Assert.Equal("Simulated instrument", demo.Address);
    }

    [Fact]
    public void NonDefaultPortIsShownInTheAddress()
    {
        Assert.Equal("10.0.0.5", new InstrumentProfile { Host = "10.0.0.5" }.Address);
        Assert.Equal("10.0.0.5:6000", new InstrumentProfile { Host = "10.0.0.5", Port = 6000 }.Address);
    }
}
