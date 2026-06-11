using System.Text;
using Attractor.Core.Catalog;

namespace Attractor.Core.Tests;

public class MameListXmlParserTests
{
    private const string OldDialect = """
        <?xml version="1.0"?>
        <!DOCTYPE mame [ <!ELEMENT mame (game+)> ]>
        <mame build="0.147 (Sep 17 2012)">
          <game name="galaga" sourcefile="galaga.c">
            <description>Galaga (Namco rev. B)</description>
            <year>1981</year>
            <manufacturer>Namco</manufacturer>
            <display tag="screen" type="raster" rotate="90" width="288" height="224" refresh="60.6" />
            <driver status="good" emulation="good" />
          </game>
          <game name="neogeo" isbios="yes">
            <description>Neo-Geo</description>
            <driver status="good" />
          </game>
          <game name="hng64" >
            <description>Hyper NeoGeo 64 Bios</description>
            <driver status="preliminary" />
          </game>
        </mame>
        """;

    private const string NewDialect = """
        <?xml version="1.0"?>
        <mame build="0.288 (mame0288)">
          <machine name="1942" sourcefile="capcom/1942.cpp" cloneof="">
            <description>1942 (Revision B)</description>
            <year>1984</year>
            <manufacturer>Capcom</manufacturer>
            <display tag="screen" type="raster" rotate="270" width="256" height="224" />
            <driver status="imperfect" emulation="good" />
          </machine>
          <machine name="ym2151" isdevice="yes" runnable="no">
            <description>YM2151 OPM</description>
          </machine>
        </mame>
        """;

    private static async Task<List<MachineInfo>> ParseAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = new List<MachineInfo>();
        await foreach (var m in MameListXmlParser.ParseAsync(stream))
            result.Add(m);
        return result;
    }

    [Fact]
    public async Task Parses_the_pre_0162_game_dialect_with_doctype()
    {
        var machines = await ParseAsync(OldDialect);
        Assert.Equal(3, machines.Count);

        var galaga = machines[0];
        Assert.Equal("galaga", galaga.Name);
        Assert.Equal("Galaga (Namco rev. B)", galaga.Description);
        Assert.Equal("1981", galaga.Year);
        Assert.Equal("Namco", galaga.Manufacturer);
        Assert.Equal(90, galaga.Rotate);
        Assert.Equal(DriverStatus.Good, galaga.Driver);
        Assert.False(galaga.IsBios);
        Assert.True(galaga.Runnable);

        Assert.True(machines[1].IsBios);
        Assert.Equal(DriverStatus.Preliminary, machines[2].Driver);
    }

    [Fact]
    public async Task Parses_the_modern_machine_dialect()
    {
        var machines = await ParseAsync(NewDialect);
        Assert.Equal(2, machines.Count);

        var game = machines[0];
        Assert.Equal("1942", game.Name);
        Assert.Equal(DriverStatus.Imperfect, game.Driver);
        Assert.Equal(270, game.Rotate);

        var device = machines[1];
        Assert.True(device.IsDevice);
        Assert.False(device.Runnable);
    }
}
