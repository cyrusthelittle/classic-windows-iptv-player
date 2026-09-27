using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicWindowsIptvPlayer.Windows;

// Search indexes programme references, then creates visual row data only for
// indices requested by WPF's virtualizing ListBox.
internal static class GuideSearchRows
{
    public static LazyList<T> Create<T>(EpgGuide guide, IReadOnlyList<Channel> channels, string query,
        Func<Channel, string?> mapping, int offsetMinutes, Func<Channel, EpgProgramme, T> createRow)
    {
        var references = new List<(Channel Channel, EpgProgramme Programme)>();
        foreach (var channel in channels)
            foreach (var programme in guide.SearchProgrammes(channel, query, mapping(channel), offsetMinutes))
                references.Add((channel, programme));
        references.Sort((left, right) => left.Programme.Start.CompareTo(right.Programme.Start));
        return new LazyList<T>(references.Count, index =>
        {
            var (channel, programme) = references[index];
            return createRow(channel, programme);
        });
    }
}
