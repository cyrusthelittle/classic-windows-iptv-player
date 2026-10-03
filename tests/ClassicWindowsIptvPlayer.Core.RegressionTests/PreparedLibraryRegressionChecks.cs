using System.Diagnostics;
using ClassicWindowsIptvPlayer.Core;
internal static class PreparedLibraryRegressionChecks
{
    public static int Run(bool performance = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var count in performance ? new[] { 50_000, 150_000 } : new[] { 64 })
            {
                var store = new ConfigStore(root);
                var source = Enumerable.Range(0, count).Select(i => new Channel { Id = i.ToString(), Name = $"Fixture station {i:D6}", Group = $"Group {i % 100:D3}", Url = $"http://127.0.0.1/invented/{i}", MediaKind = (MediaKind)(i % 3) }).ToList();
                store.SaveChannelCache("fixture", source);
                source = null!;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var clock = Stopwatch.StartNew();
                var loaded = store.LoadChannelCache("fixture", (stage, elapsed) => Console.WriteLine($"STAGE {count} {stage}={elapsed.TotalMilliseconds:F1}ms"));
                var read = clock.Elapsed.TotalMilliseconds;
                var organized = LibraryOrganization.Apply(loaded, new AccountLibraryState());
                var organization = clock.Elapsed.TotalMilliseconds - read;
                var index = new MediaSearchIndex(organized);
                var indexing = clock.Elapsed.TotalMilliseconds - read - organization;
                var visible = index.SearchAll("", null, CancellationToken.None).ToList();
                var filtering = clock.Elapsed.TotalMilliseconds - read - organization - indexing;
                if (visible.Count != count) throw new Exception("Catalog count changed.");
                Console.WriteLine($"FIXTURE {count}: cache={read:F1}ms organization={organization:F1}ms index={indexing:F1}ms filter={filtering:F1}ms total={clock.Elapsed.TotalMilliseconds:F1}ms allocated={(GC.GetTotalAllocatedBytes(true)-allocated)/1048576d:F1}MiB managed={GC.GetTotalMemory(false)/1048576d:F1}MiB");
                if (!ReferenceEquals(loaded[0], organized[0])) throw new Exception("Unmodified display duplicated a channel.");
                var organizationState = new AccountLibraryState();
                var revision = PreparedLibraryCache.Revision(new(), organizationState, store.ChannelCacheRevision("fixture"));
                var prepared = new PreparedLibrary(loaded, organized, index);
                var cache = new PreparedLibraryCache(prepared.EstimatedBytes * 2, 2);
                cache.Put("A", revision, prepared);
                cache.Put("B", revision, prepared);
                clock.Restart();
                var warm = cache.Get("A", revision) ?? throw new Exception("Warm library not retained.");
                var warmVisible = warm.Index.SearchAll("", null, CancellationToken.None).ToList();
                Console.WriteLine($"WARM {count}: prepared reuse+filter={clock.Elapsed.TotalMilliseconds:F1}ms retainedEstimate={cache.RetainedBytes/1048576d:F1}MiB");
                if (!ReferenceEquals(index, warm.Index) || warmVisible.Count != count) throw new Exception("Warm library was rebuilt.");
                cache.Put("C", revision, prepared);
                if (cache.Get("B", revision) is not null || cache.Count != 2) throw new Exception("LRU eviction failed.");
                if (cache.Get("A", revision + "changed") is not null) throw new Exception("Changed revision reused stale library.");
                var tiny = new PreparedLibraryCache(1); tiny.Put("A", revision, prepared);
                if (tiny.Count != 0 || tiny.RetainedBytes != 0) throw new Exception("Oversized catalog retained.");
                var defaultBudget = new PreparedLibraryCache();
                for (var account = 0; account < 4; account++) defaultBudget.Put(account.ToString(), revision, prepared);
                if (defaultBudget.Count > 3 || defaultBudget.RetainedBytes > 256L * 1024 * 1024)
                    throw new Exception("Production retained-memory budget exceeded.");
                organizationState.ChannelOrganization[ItemIdentity.For(loaded[0])] = new() { Name = "Renamed fixture" };
                var snapshot = PreparedLibraryCache.SnapshotOrganization(organizationState);
                organizationState.ChannelOrganization.Clear();
                if (snapshot.ChannelOrganization.Count != 1 || PreparedLibraryCache.Revision(new(), snapshot, store.ChannelCacheRevision("fixture")) == revision)
                    throw new Exception("Organization snapshot/revision failed.");
                if (PreparedLibraryCache.Revision(new() { Password = "invented-change" }, new(), store.ChannelCacheRevision("fixture")) == revision)
                    throw new Exception("Provider setting change did not invalidate revision.");
                store.SaveChannelCache("fixture", loaded);
                if (PreparedLibraryCache.Revision(new(), new(), store.ChannelCacheRevision("fixture")) == revision)
                    throw new Exception("Disk replacement did not invalidate revision.");
            }
            Console.WriteLine("PASS prepared library reuse, snapshots, provider/catalog/organization invalidation, LRU and byte budget");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
