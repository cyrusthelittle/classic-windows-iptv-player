using System.Net;
using System.Text;
using ClassicWindowsIptvPlayer.Core;

internal static class CatchupRegressionChecks
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
    static Channel Live() => new() { Name="Invented", EpgId="fixture", Url="http://fixture/live/u/p/42.ts", CatchupMode=CatchupMode.Xtream, ArchiveDays=7, ArchiveStreamId="42" };
    static AccountSettings Account() => new() { ServerUrl="http://fixture:8080", Username="invented u", Password="invented/p&" };
    static AccountProfile Profile(string zone="Europe/Berlin", string active="0",string max="1") => new() { Timezone=zone, ActiveConnections=active, MaxConnections=max };
    static EpgProgramme Programme(DateTimeOffset? start=null,DateTimeOffset? stop=null) => new("fixture","Invented programme","","",start ?? Now.AddHours(-2),stop ?? Now.AddHours(-1));
    static void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    public static async Task<int> RunAsync()
    {
        int total=0, failures=0;
        async Task Check(string name,Func<Task> run) { total++;try { await run();Console.WriteLine("PASS catchup: "+name); } catch(Exception e) { failures++;Console.WriteLine("FAIL catchup: "+name+" - "+e.Message); } }
        Task Done(Action action) { action();return Task.CompletedTask; }
        await Check("unsupported media, unknown capability and invalid archive metadata",()=>Done(()=>
        {
            foreach(var channel in new[]{new Channel(),new Channel{MediaKind=MediaKind.Movie,CatchupMode=CatchupMode.Xtream,ArchiveDays=7,ArchiveStreamId="42"},new Channel{CatchupMode=CatchupMode.Xtream,ArchiveDays=7,ArchiveStreamId="42/evil"},new Channel{CatchupMode=CatchupMode.Xtream,ArchiveDays=-1,ArchiveStreamId="42"}})
                Equal(CatchupFailure.Unsupported,Catchup.Evaluate(channel,Programme(),Now).Failure);
        }));
        await Check("past, current, future and invalid programme decisions",()=>Done(()=>
        {
            Equal(false,Catchup.Evaluate(Live(),Programme(),Now).IsStartOver);
            Equal(true,Catchup.Evaluate(Live(),Programme(Now.AddMinutes(-20),Now.AddMinutes(40)),Now).IsStartOver);
            Equal(CatchupFailure.Future,Catchup.Evaluate(Live(),Programme(Now,Now.AddHours(1)),Now).Failure);
            Equal(CatchupFailure.InvalidProgramme,Catchup.Evaluate(Live(),Programme(Now,Now),Now).Failure);
        }));
        await Check("retention inclusive boundary and partial expiry",()=>Done(()=>
        {
            Equal(true,Catchup.Evaluate(Live(),Programme(Now.AddDays(-7),Now.AddDays(-7).AddHours(1)),Now).IsAvailable);
            Equal(CatchupFailure.Expired,Catchup.Evaluate(Live(),Programme(Now.AddDays(-7).AddSeconds(-1),Now.AddDays(-7).AddHours(1)),Now).Failure);
        }));
        await Check("server timezone conversion and escaped credentials",()=>Done(()=>
        {
            var request=Catchup.CreateRequest(Live(),Programme(),Account(),Profile(),Now);
            Equal(true,request.IsAvailable);Equal(60,request.DurationMinutes);
            Equal("http://fixture:8080/timeshift/invented%20u/invented%2Fp%26/60/2026-09-28:12-00/42.ts",request.Url);
            Equal(Now.AddHours(-2),request.StartUtc);
        }));
        await Check("StartOver requests only recorded interval",()=>Done(()=>
        {
            var request=Catchup.CreateRequest(Live(),Programme(Now.AddMinutes(-20),Now.AddMinutes(40)),Account(),Profile(),Now);
            Equal(20,request.DurationMinutes);Equal(Now,request.EndUtc);
        }));
        await Check("archive credentials are redacted in URLs, loose paths and exceptions",()=>Done(()=>
        {
            var request=Catchup.CreateRequest(Live(),Programme(),Account(),Profile(),Now);
            foreach(var text in new[]{AppLogger.SanitizeUrl(request.Url),AppLogger.SanitizeText("failed path /timeshift/invented-user/invented-secret/60/2026-09-28:12-00/42.ts"),AppLogger.SanitizeException(new HttpRequestException(request.Url))})
            { Equal(false,text.Contains("invented%2Fp",StringComparison.OrdinalIgnoreCase));Equal(false,text.Contains("invented/p&"));Equal(false,text.Contains("invented-secret")); }
        }));
        await Check("minute precision covers seconds without truncating programme",()=>Done(()=>
        {
            var request=Catchup.CreateRequest(Live(),Programme(Now.AddHours(-2).AddSeconds(31),Now.AddHours(-1).AddSeconds(32)),Account(),Profile(),Now);
            Equal(61,request.DurationMinutes);Equal(Now.AddHours(-2),request.StartUtc);
        }));
        await Check("unknown timezone and DST repeated hour blocked",()=>Done(()=>
        {
            Equal(CatchupFailure.TimezoneUnknown,Catchup.CreateRequest(Live(),Programme(),Account(),Profile(""),Now).Failure);
            Equal(CatchupFailure.TimezoneUnknown,Catchup.CreateRequest(Live(),Programme(),Account(),Profile("invented/missing"),Now).Failure);
            var fall=DateTimeOffset.Parse("2026-10-25T03:30:00Z");
            Equal(CatchupFailure.AmbiguousTime,Catchup.CreateRequest(Live(),Programme(DateTimeOffset.Parse("2026-10-25T00:30:00Z"),DateTimeOffset.Parse("2026-10-25T01:00:00Z")),Account(),Profile(),fall).Failure);
        }));
        await Check("connection allowance and owned replacement",()=>Done(()=>
        {
            Equal(CatchupFailure.ConnectionLimit,Catchup.CreateRequest(Live(),Programme(),Account(),Profile(active:"1"),Now).Failure);
            Equal(true,Catchup.CreateRequest(Live(),Programme(),Account(),Profile(active:"1"),Now,true).IsAvailable);
            Equal(CatchupFailure.ConnectionLimit,Catchup.CreateRequest(Live(),Programme(),Account(),Profile(active:"2"),Now,true).Failure);
            foreach(var profile in new[]{Profile(active:""),Profile(max:""),Profile(max:"0"),Profile(active:"-1")})
                Equal(CatchupFailure.ConnectionUnknown,Catchup.CreateRequest(Live(),Programme(),Account(),profile,Now).Failure);
        }));
        await Check("get.php credentials resolved without retaining query/path",()=>Done(()=>
        {
            var account=new AccountSettings{M3uUrl="http://fixture:8080/get.php?username=fixture%20u&password=fixture%2Fp&type=m3u"};
            var request=Catchup.CreateRequest(Live(),Programme(),account,Profile("UTC"),Now);
            Equal("http://fixture:8080/timeshift/fixture%20u/fixture%2Fp/60/2026-09-28:10-00/42.ts",request.Url);
            Equal(CatchupFailure.InvalidAccount,Catchup.CreateRequest(Live(),Programme(),new AccountSettings{M3uUrl="http://fixture/plain.m3u"},Profile(),Now).Failure);
        }));
        await Check("archive capability preserved in protected cache and organization clone",()=>Done(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"cyrus-catchup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try { var store=new ConfigStore(root);store.SaveChannelCache("fixture",[Live()]);
                var cached=store.LoadChannelCache("fixture").Single();Equal(7,cached.ArchiveDays);Equal(CatchupMode.Xtream,cached.CatchupMode);Equal("42",cached.ArchiveStreamId);
                var display=LibraryOrganization.Apply([cached],new AccountLibraryState()).Single();Equal(true,Catchup.HasSupportedArchive(display));
            } finally { Directory.Delete(root,true); }
        }));
        await Check("M3U overlays advertised Xtream archives with one live catalog request",async()=>
        {
            var liveCalls=0;
            using var client=new HttpClient(new Fixture((request,_)=>
            {
                var url=request.RequestUri!.ToString();
                if(url.Contains("get.php"))return Task.FromResult(Reply("#EXTM3U\n#EXTINF:-1 tvg-id=\"fixture\",Invented\nhttp://fixture/live/u/p/42.ts\n"));
                if(url.Contains("action=get_live_streams")){liveCalls++;return Task.FromResult(Reply("[{\"stream_id\":42,\"tv_archive\":\"1\",\"tv_archive_duration\":7,\"info\":{\"unused\":1}}]"));}
                return Task.FromResult(Reply("[]"));
            }));
            var channels=(await new PlaylistService(client).LoadPlaylistResultAsync(Account(),CancellationToken.None)).Channels;
            Equal(1,liveCalls);Equal(true,Catchup.HasSupportedArchive(channels.Single()));Equal("42",channels.Single().ArchiveStreamId);
        });
        await Check("API fallback parses only explicit valid live archive metadata",async()=>
        {
            using var client=new HttpClient(new Fixture((request,_)=>
            {
                var url=request.RequestUri!.ToString();
                if(url.Contains("get.php"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if(url.Contains("action=get_live_streams"))return Task.FromResult(Reply("[{\"stream_id\":42,\"name\":\"Supported\",\"tv_archive\":1,\"tv_archive_duration\":\"7\"},{\"stream_id\":43,\"name\":\"No archive\",\"tv_archive\":0,\"tv_archive_duration\":7}]"));
                return Task.FromResult(Reply("[]"));
            }));
            var channels=(await new PlaylistService(client).LoadPlaylistResultAsync(Account(),CancellationToken.None)).Channels;
            Equal(true,Catchup.HasSupportedArchive(channels.Single(c=>c.Name=="Supported")));Equal(false,Catchup.HasSupportedArchive(channels.Single(c=>c.Name=="No archive")));
        });
        await Check("provider profile reads numeric counts and archive timezone",async()=>
        {
            using var client=new HttpClient(new Fixture((_,_)=>Task.FromResult(Reply("{\"user_info\":{\"active_cons\":1,\"max_connections\":\"2\",\"status\":\"Active\"},\"server_info\":{\"timezone\":\"Europe/Berlin\"}}"))));
            var profile=await new PlaylistService(client).FetchCatchupProfileAsync(Account(),CancellationToken.None);
            Equal("1",profile.ActiveConnections);Equal("2",profile.MaxConnections);Equal("Europe/Berlin",profile.Timezone);
        });
        await Check("XMLTV offset and guide correction are used exactly once",async()=>
        {
            using var xml=new MemoryStream(Encoding.UTF8.GetBytes("<tv><programme channel='fixture' start='20260928120000 +0200' stop='20260928130000 +0200'><title>Invented programme</title></programme></tv>"));
            var guide=await EpgService.ParseAsync(xml,[Live()],Now,CancellationToken.None);
            var programme=guide.GetProgrammes(Live(),Now.AddDays(-1),Now,offsetMinutes:30).Single();
            var request=Catchup.CreateRequest(Live(),programme,Account(),Profile(),Now);
            Equal(Now.AddHours(-2).AddMinutes(30),request.StartUtc);Equal(true,request.Url.Contains("2026-09-28:12-30"));
        });
        foreach(var (status,failure) in new[]{(HttpStatusCode.OK,CatchupStreamFailure.None),(HttpStatusCode.NotFound,CatchupStreamFailure.Unavailable),(HttpStatusCode.Gone,CatchupStreamFailure.Unavailable),(HttpStatusCode.Forbidden,CatchupStreamFailure.AccessDenied),(HttpStatusCode.ServiceUnavailable,CatchupStreamFailure.Network)})
            await Check("HTTP archive classification "+(int)status,async()=>
            {
                using var client=new HttpClient(new Fixture((_,_)=>Task.FromResult(new HttpResponseMessage(status))));
                var result=await Catchup.CheckStreamAsync(Catchup.CreateRequest(Live(),Programme(),Account(),Profile(),Now),client,CancellationToken.None);
                Equal(failure,result.Failure);Equal(status,result.StatusCode);Equal(status==HttpStatusCode.OK,result.IsAvailable);
            });
        await Check("transport failures never imply expiry and caller cancellation propagates",async()=>
        {
            using var failed=new HttpClient(new Fixture((_,_)=>throw new HttpRequestException("invented transport failure")));
            var request=Catchup.CreateRequest(Live(),Programme(),Account(),Profile(),Now);
            Equal(CatchupStreamFailure.Network,(await Catchup.CheckStreamAsync(request,failed,CancellationToken.None)).Failure);
            using var cancelled=new HttpClient(new Fixture(async (_,token)=>{await Task.Delay(Timeout.Infinite,token);return Reply("");}));
            using var cts=new CancellationTokenSource(30);
            try { await Catchup.CheckStreamAsync(request,cancelled,cts.Token);throw new Exception("Cancellation swallowed"); }catch(OperationCanceledException) { }
            Equal(true,Catchup.FailureMessage(Live(),Programme(),Now.AddDays(8)).Contains("archive window"));
        });
        Console.WriteLine($"Catchup checks: {total-failures}/{total} passed");return failures;
    }
    static HttpResponseMessage Reply(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body)};
    sealed class Fixture(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>handler(request,token); }
}
