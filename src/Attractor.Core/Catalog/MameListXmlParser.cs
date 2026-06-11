using System.Xml;

namespace Attractor.Core.Catalog;

/// <summary>
/// Streams -listxml output into MachineInfo records. The XML is huge (124 MB
/// on 0.147, far larger on current MAME) so this never builds a DOM. Accepts
/// both element dialects: &lt;game&gt; (pre-0.162) and &lt;machine&gt;.
/// </summary>
public static class MameListXmlParser
{
    public static async IAsyncEnumerable<MachineInfo> ParseAsync(
        Stream xml,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(xml, settings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || (reader.Name != "machine" && reader.Name != "game"))
                continue;

            var name = reader.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            bool isBios = reader.GetAttribute("isbios") == "yes";
            bool isDevice = reader.GetAttribute("isdevice") == "yes";
            bool isMechanical = reader.GetAttribute("ismechanical") == "yes";
            bool runnable = reader.GetAttribute("runnable") != "no";
            var cloneOf = reader.GetAttribute("cloneof");

            string? description = null, year = null, manufacturer = null;
            var driver = DriverStatus.Unknown;
            int rotate = 0;
            bool sawDisplay = false;

            if (!reader.IsEmptyElement)
            {
                int depth = reader.Depth;
                // ReadElementContentAsStringAsync leaves the reader positioned
                // on the node AFTER the element it consumed, so the next loop
                // iteration must NOT advance again or it skips a sibling.
                bool advance = true;
                while (true)
                {
                    if (advance && !await reader.ReadAsync().ConfigureAwait(false))
                        break;
                    advance = true;

                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                        break;
                    if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
                        continue;

                    switch (reader.Name)
                    {
                        case "description":
                            description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            advance = false;
                            break;
                        case "year":
                            year = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            advance = false;
                            break;
                        case "manufacturer":
                            manufacturer = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            advance = false;
                            break;
                        case "display" when !sawDisplay:
                            sawDisplay = true;
                            _ = int.TryParse(reader.GetAttribute("rotate"), out rotate);
                            break;
                        case "driver":
                            driver = reader.GetAttribute("status") switch
                            {
                                "good" => DriverStatus.Good,
                                "imperfect" => DriverStatus.Imperfect,
                                "preliminary" => DriverStatus.Preliminary,
                                _ => DriverStatus.Unknown,
                            };
                            break;
                    }
                }
            }

            yield return new MachineInfo(
                name, description, year, manufacturer, isBios, isDevice, runnable, cloneOf, driver, rotate, isMechanical);
        }
    }
}
