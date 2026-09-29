using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;

internal static class Program
{
    static MainWindow window=null!;
    static string root="";
    static object? Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(window);
    static object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(window,args);
    static void Check(bool value,string name){if(!value)throw new Exception(name);File.AppendAllText(Path.Combine(root,"evidence.txt"),"PASS "+name+"\n");}
    static string Status=>((TextBlock)window.FindName("StatusText")).Text;
    static async Task WaitPlaying(){for(int i=0;i<80;i++){if(Field("_mediaPlayer") is MediaPlayer {IsPlaying:true})return;await Task.Delay(100);}throw new Exception("native playback did not start: "+Status);}
    static async Task WaitVisibility(FrameworkElement element,Visibility expected){for(int i=0;i<40;i++){if(element.Visibility==expected)return;await Task.Delay(50);}throw new Exception($"expected {element.Name} visibility {expected}, got {element.Visibility}");}
    [STAThread] static void Main(string[] args)
    {
        if(!args.Contains("--child"))
        {
            root=Path.Combine(Path.GetTempPath(),"cyrus-catchup-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            foreach(var source in Directory.GetFiles(AppContext.BaseDirectory,"*",SearchOption.AllDirectories))
            {var rel=Path.GetRelativePath(AppContext.BaseDirectory,source);if(rel.StartsWith("cache\\")||rel.StartsWith("logs\\")||rel.StartsWith("accounts."))continue;var target=Path.Combine(root,rel);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(source,target);}
            Console.WriteLine(root);
            using var child=Process.Start(new ProcessStartInfo(Path.Combine(root,Path.GetFileName(Environment.ProcessPath!)),"--child"){WorkingDirectory=root,WindowStyle=ProcessWindowStyle.Hidden});
            if(!child!.WaitForExit(120000)){child.Kill(true);Console.WriteLine("FAIL fixture deadline");Environment.ExitCode=1;return;}Environment.ExitCode=child.ExitCode;var evidence=Path.Combine(root,"evidence.txt");Console.WriteLine(File.Exists(evidence)?File.ReadAllText(evidence):"FAIL fixture exited before checks");return;
        }
        root=AppContext.BaseDirectory;GenerateAvi.Run(Path.Combine(root,"fixture.avi"));
        using var server=new Fixture(File.ReadAllBytes(Path.Combine(root,"fixture.avi")));
        var store=new ConfigStore();var state=store.Load();var saved=state.EnsureSelectedAccount();
        var account=new AccountSettings{ServerUrl=server.Origin,Username="fixture",Password="invented"};saved.Settings=account.Clone();saved.Name="Catch-up fixture";saved.LastPlaylistUpdatedUtc=DateTime.UtcNow;
        state.Account=account.Clone();state.CheckForUpdatesOnStartup=false;state.RemoteControlEnabled=false;store.Save(state);
        var channel=new Channel{Id="fixture-live",Name="Fixture Live",Group="Fixture",EpgId="guide",Url=new Uri(Path.Combine(root,"fixture.avi")).AbsoluteUri,MediaKind=MediaKind.Live,CatchupMode=CatchupMode.Xtream,ArchiveDays=2,ArchiveStreamId="42"};
        store.SaveChannelCache(saved.Id,new[]{channel});
        var app=new App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();
        var startup=typeof(App).GetMethod("OnStartup",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(object),typeof(StartupEventArgs)},null)!;app.Startup-=(StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler),app);
        window=new MainWindow(new LoginResult{Account=account,AccountId=saved.Id,UpdatePlaylist=false});app.MainWindow=window;
        window.ContentRendered+=async(_,_)=>{try{await Run(server,channel);File.AppendAllText(Path.Combine(root,"evidence.txt"),"ALL PASS\n");}catch(Exception ex){File.AppendAllText(Path.Combine(root,"evidence.txt"),"FAIL "+ex+"\n");Environment.ExitCode=1;}finally{
            // Let LibVLC retire its native media/player threads before WPF tears down the HWND.
            try { Call("StopPlayback"); } catch { }
            await Task.Delay(750);
            try { window.Close(); } catch { }
            await Task.Delay(250);
            app.Shutdown();
        }};
        window.Show();app.Run();
    }
    static async Task Run(Fixture server,Channel channel)
    {
        await Task.Delay(2000);
        var now=DateTimeOffset.UtcNow;var programme=new EpgProgramme("guide","Past fixture","Generated local video","Test",now.AddMinutes(-15),now.AddMinutes(-5));
        var sourceUrl=channel.Url;var identity=ItemIdentity.For(channel);
        Call("PlayChannel",channel,null);await WaitPlaying();
        Check(((TextBlock)window.FindName("TimeText")).Visibility==Visibility.Collapsed,"main player time text hidden during normal playback");
        await VerifyOpenRecentFilenameOnlyIndexEntry();
        server.IsPlaying=()=>Field("_mediaPlayer") is MediaPlayer{IsPlaying:true};
        var historyBefore=((AppState)Field("_state")!).Recent.Select(x=>x.Url).ToArray();
        await (Task)Call("StartCatchupAsync",channel,programme)!;await WaitPlaying();
        Check(server.ArchiveRequests>=2,"preflight and native archive HTTP requests");
        Check(!server.PlayingAtFirstProbe,"previous player retired before archive preflight");
        var context=Field("_catchupPlayback")!;var request=context.GetType().GetProperty("Request")!.GetValue(context)!;
        Check(!(bool)request.GetType().GetProperty("IsLive")!.GetValue(request)!,"archive tune uses VOD semantics");
        Check(channel.Url==sourceUrl&&ItemIdentity.For(channel)==identity,"original catalog URL and identity unchanged");
        Check(((AppState)Field("_state")!).Recent.Select(x=>x.Url).SequenceEqual(historyBefore),"archive does not insert archive URL into recent history");
        Check(((Button)window.FindName("GoLiveButton")).Visibility==Visibility.Visible,"Return Live control visible for archive");
        Call("TogglePlayPause");await Task.Delay(300);Check(Field("_mediaPlayer") is not MediaPlayer{IsPlaying:true},"archive pause");
        var timeText=(TextBlock)window.FindName("TimeText");await WaitVisibility(timeText,Visibility.Visible);
        Check(timeText.Visibility==Visibility.Visible,"main player time text visible while paused");
        Call("TogglePlayPause");await WaitPlaying();
        await WaitVisibility(timeText,Visibility.Collapsed);
        Check(timeText.Visibility==Visibility.Collapsed,"main player time text hidden after resume");
        Check(true,"archive resume");
        Call("GoLive_Click",null,new RoutedEventArgs());await WaitPlaying();Check(Field("_catchupPlayback")==null,"Return Live clears archive context");
        Check(channel.Url==sourceUrl,"Return Live retains original live source");
        Call("StopPlayback");await Task.Delay(300);
        server.Active=1;var hits=server.ArchiveRequests;await (Task)Call("StartCatchupAsync",channel,programme)!;Check(Status.Contains("allowance")&&server.ArchiveRequests==hits,$"occupied provider allowance prevents archive request (status={Status}; requests={hits}->{server.ArchiveRequests}; fixtureActive={server.Active})");server.Active=0;
        server.ArchiveStatus=410;await(Task)Call("StartCatchupAsync",channel,programme)!;Check(Status.Contains("no archive"),"HTTP410 reports unavailable recording");
        server.ArchiveStatus=500;await(Task)Call("StartCatchupAsync",channel,programme)!;Check(Status.Contains("does not establish")&&Status.Contains("expired"),"HTTP500 remains network failure rather than expiration");server.ArchiveStatus=200;
        var expired=programme with{Start=now.AddDays(-3),Stop=now.AddDays(-3).AddHours(1)};hits=server.ArchiveRequests;await(Task)Call("StartCatchupAsync",channel,expired)!;Check(Status.Contains("archive window")&&server.ArchiveRequests==hits,"expired programme rejects without network");
        var unsupported=new Channel{Id="unsupported",Name="Unsupported",Url=sourceUrl};await(Task)Call("StartCatchupAsync",unsupported,programme)!;Check(Status.Contains("does not advertise"),"unsupported channel explains missing archive");
        server.DelayProfile=true;var pending=(Task)Call("StartCatchupAsync",channel,programme)!;await Task.Delay(150);Call("StopPlayback");await pending.WaitAsync(TimeSpan.FromSeconds(3));await Task.Delay(300);Check(Field("_catchupPlayback")==null&&Field("_mediaPlayer") is not MediaPlayer{IsPlaying:true},"Stop cancels pending profile and does not tune later");server.DelayProfile=false;
        ((AppState)Field("_state")!).EpgEnabled=true;Call("UpdateEpgEnabledUi");
        typeof(MainWindow).GetField("_browseNow",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,programme);
        Call("UpdateCatchupButtons",channel);window.UpdateLayout();var startOver=(Button)window.FindName("BrowseStartOverButton");Check(startOver.Visibility==Visibility.Visible&&startOver.IsEnabled&&startOver.ActualHeight>0,"browse Start Over action realized and available");
        typeof(MainWindow).GetField("_browseNow",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,expired);Call("UpdateCatchupButtons",channel);Check(!startOver.IsEnabled,"browse expired archive action disabled");Call("UpdateCatchupButtons",unsupported);Check(startOver.Visibility==Visibility.Collapsed,"browse unsupported archive action hidden");
        await Details(channel,programme,true);await Details(channel,expired,false);await Details(unsupported,programme,null);
    }
    static async Task VerifyOpenRecentFilenameOnlyIndexEntry()
    {
        var recordingFolder=Path.Combine(root,"configured-recordings");Directory.CreateDirectory(recordingFolder);
        const string fileName="filename-only-fixture.ts";var expectedPath=Path.Combine(recordingFolder,fileName);
        await File.WriteAllBytesAsync(expectedPath,[1,2,3,4]);
        var accountId=((AppState)Field("_state")!).SelectedAccountId;
        var index=new RecordingIndex();
        index.Entries.Add(new RecordingEntry
        {
            Id="filename-only-fixture",AccountId=accountId,FileName=fileName,
            RequestedStartUtc=DateTimeOffset.UtcNow,ByteSize=4,Outcome=ClassicWindowsIptvPlayer.Core.RecordingOutcome.Completed
        });
        typeof(MainWindow).GetField("_recordingIndex",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,index);
        typeof(MainWindow).GetField("_recordingFolder",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,recordingFolder);
        Call("MorePlaybackMenu_Opened",window.FindName("MorePlaybackButton"),new RoutedEventArgs());
        var button=(Button)window.FindName("MorePlaybackButton");
        var openRecent=button.ContextMenu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="Open recent recording");
        Check(openRecent.IsEnabled,"More → Open recent enabled for existing filename-only recording entry");
        Check(string.Equals(openRecent.Tag as string,expectedPath,StringComparison.OrdinalIgnoreCase),"More → Open recent resolves filename using configured recordings folder");
    }
    static async Task Details(Channel channel,EpgProgramme programme,bool? expected)
    {
        var type=typeof(MainWindow).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.ProgrammeDetailsWindow")!;
        var dialog=(Window)Activator.CreateInstance(type,channel,programme,(Action)(()=>{}),(Action)(()=>{}))!;dialog.Owner=window;dialog.Show();await Task.Delay(100);dialog.UpdateLayout();
        var buttons=Descendants(dialog).OfType<Button>().Where(b=>b.Name=="WatchArchiveButton").ToArray();Check(expected is null?buttons.Length==0:buttons.Length==1&&buttons[0].IsEnabled==expected,"rendered details archive action: "+(expected?.ToString()??"unsupported hidden"));
        Check(dialog.ActualWidth>0&&dialog.ActualHeight>0,"details WPF layout realized");dialog.Close();
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject node){for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(node,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    sealed class Fixture:IDisposable
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);readonly byte[] video;readonly CancellationTokenSource cts=new();public string Origin{get;}public int Active,ArchiveStatus=200,ArchiveRequests;public bool DelayProfile,PlayingAtFirstProbe;public Func<bool>? IsPlaying;
        public Fixture(byte[] data){video=data;listener.Start();Origin=$"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";_=Loop();}
        async Task Loop(){while(!cts.IsCancellationRequested){try{var client=await listener.AcceptTcpClientAsync(cts.Token);_=Respond(client);}catch{break;}}}
        async Task Respond(TcpClient client)
        {
            using(client)try
            {
                var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);var line=await reader.ReadLineAsync(cts.Token);if(line is null)return;
                var path=line.Split(' ')[1].Split('?')[0];while(!string.IsNullOrEmpty(await reader.ReadLineAsync(cts.Token))){}
                byte[] body=[];string mime="text/plain";int status=200;
                if(path=="/player_api.php"){if(DelayProfile)await Task.Delay(5000,cts.Token);body=Encoding.UTF8.GetBytes($"{{\"user_info\":{{\"status\":\"Active\",\"max_connections\":\"1\",\"active_cons\":\"{Active}\"}},\"server_info\":{{\"timezone\":\"UTC\"}}}}");mime="application/json";}
                else if(path.StartsWith("/timeshift/")){if(Interlocked.Increment(ref ArchiveRequests)==1)PlayingAtFirstProbe=IsPlaying?.Invoke()==true;status=ArchiveStatus;if(status==200){body=video;mime="video/x-msvideo";}}
                else status=404;
                var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(header,cts.Token);await stream.WriteAsync(body,cts.Token);
            }catch{}
        }
        public void Dispose(){cts.Cancel();listener.Stop();cts.Dispose();}
    }
}
