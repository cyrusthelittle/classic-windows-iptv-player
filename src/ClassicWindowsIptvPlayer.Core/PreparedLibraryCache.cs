using System.Security.Cryptography;
using System.Text.Json;

namespace ClassicWindowsIptvPlayer.Core;

public sealed record PreparedLibrary(List<Channel> Source, List<Channel> Channels, MediaSearchIndex Index)
{
    public long EstimatedBytes { get; } = Estimate(Source);

    private static long Estimate(List<Channel> source)
    {
        long bytes = 0;
        foreach (var item in source)
            bytes += 1024L + 2L * (item.Id.Length + item.Name.Length * 3 + item.Group.Length * 3 +
                item.Logo.Length + item.EpgId.Length + item.Url.Length + item.RawInfo.Length +
                item.SeriesId.Length + item.Description.Length + item.Genre.Length + item.ArchiveStreamId.Length);
        return bytes;
    }
}

// UI-thread-owned LRU: active views share these references rather than copying catalogs.
// The conservative estimate bounds retained entries, not process working set or active loads.
public sealed class PreparedLibraryCache(long budgetBytes = 256L * 1024 * 1024, int maximumEntries = 3)
{
    private sealed record Entry(string AccountId, string Revision, PreparedLibrary Library, long Bytes);
    private readonly LinkedList<Entry> _entries = new();
    public long RetainedBytes { get; private set; }
    public int Count => _entries.Count;

    public static AccountLibraryState SnapshotOrganization(AccountLibraryState library) => new()
    {
        ChannelOrganization = library.ChannelOrganization.ToDictionary(p => p.Key,
            p => new ChannelOrganization { Name = p.Value.Name, Hidden = p.Value.Hidden, Order = p.Value.Order }, StringComparer.OrdinalIgnoreCase),
        GroupOrganization = library.GroupOrganization.ToDictionary(p => p.Key,
            p => new GroupOrganization { Name = p.Value.Name, Hidden = p.Value.Hidden, Order = p.Value.Order }, StringComparer.OrdinalIgnoreCase)
    };

    // Hash provider settings so the key neither retains nor logs plaintext credentials.
    public static string Revision(AccountSettings account, AccountLibraryState organization, string diskRevision) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            account.ServerUrl, account.M3uUrl, account.Username, account.Password,
            organization.ChannelOrganization, organization.GroupOrganization, Disk = diskRevision
        })));

    public PreparedLibrary? Get(string accountId, string revision)
    {
        var node = _entries.First;
        while (node is not null)
        {
            if (node.Value.AccountId == accountId)
            {
                if (node.Value.Revision != revision) { Remove(accountId); return null; }
                _entries.Remove(node);
                _entries.AddFirst(node);
                return node.Value.Library;
            }
            node = node.Next;
        }
        return null;
    }

    public void Put(string accountId, string revision, PreparedLibrary library)
    {
        Remove(accountId);
        var bytes = library.EstimatedBytes;
        if (bytes > budgetBytes || maximumEntries <= 0) return;
        while (_entries.Count >= maximumEntries || RetainedBytes + bytes > budgetBytes)
        {
            var last = _entries.Last!;
            RetainedBytes -= last.Value.Bytes;
            _entries.RemoveLast();
        }
        _entries.AddFirst(new Entry(accountId, revision, library, bytes));
        RetainedBytes += bytes;
    }

    public void Remove(string accountId)
    {
        var node = _entries.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.AccountId == accountId)
            {
                RetainedBytes -= node.Value.Bytes;
                _entries.Remove(node);
            }
            node = next;
        }
    }

    public void Clear() { _entries.Clear(); RetainedBytes = 0; }
}
