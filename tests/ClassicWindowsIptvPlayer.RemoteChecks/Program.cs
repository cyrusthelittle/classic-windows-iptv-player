using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;

internal static class Program
{
    static MainWindow window = null!;
    static ConfigStore store = null!;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static string root = "";
    static int port;
    static object Field(string name) => (typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) ?? typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window))!;
    static void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    static T Control<T>(string name) where T : class => (window.FindName(name) as T) ?? throw new Exception("Missing control " + name);
    static MenuItem Menu(params string[] path)
    {
        ItemsControl parent = Control<Menu>("AppMenu");
        MenuItem? found = null;
        foreach (var part in path)
        {
            found = parent.Items.OfType<MenuItem>().FirstOrDefault(item => string.Equals(item.Header?.ToString(), part, StringComparison.Ordinal));
            if (found is null) throw new Exception("Menu item not found: " + string.Join(" > ", path));
            parent = found;
        }
        return found!;
    }
    static IEnumerable<string> MenuHeaders(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item.Header?.ToString() ?? "";
            foreach (var nested in MenuHeaders(item)) yield return nested;
        }
    }
    static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); File.AppendAllText(Path.Combine(root,"evidence.txt"), "PASS " + name + "\n"); }
    static async Task Cmd(string name, string? value = null) { var response = await http.GetAsync($"http://127.0.0.1:{port}/cmd?name={name}" + (value is null ? "" : "&value=" + Uri.EscapeDataString(value))); response.EnsureSuccessStatusCode(); await Task.Delay(500); }
    [STAThread]
    static void Main(string[] args)
    {
        var interactive = args.Contains("--interactive");
        if (!args.Contains("--child"))
        {
            root = Path.Combine(Path.GetTempPath(),"cyrus-remote-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            foreach(var source in Directory.GetFiles(AppContext.BaseDirectory,"*",SearchOption.AllDirectories))
            {
                var relative=Path.GetRelativePath(AppContext.BaseDirectory,source);
                if(relative.StartsWith("cache\\") || relative.StartsWith("logs\\") || relative.StartsWith("accounts.")) continue;
                var target=Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source,target);
            }
            File.WriteAllText(Path.Combine(root,"fixture-path.txt"),root);
            Console.WriteLine("Isolated fixture: " + root);
            using var child=Process.Start(new ProcessStartInfo(Path.Combine(root,Path.GetFileName(Environment.ProcessPath!)),"--child" + (interactive ? " --interactive" : "")) { WorkingDirectory=root, WindowStyle=ProcessWindowStyle.Hidden });
            child!.WaitForExit(); Console.WriteLine(root); Environment.ExitCode=child.ExitCode; return;
        }
        root=AppContext.BaseDirectory;
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        store=new ConfigStore(); var state=store.Load();var saved=state.EnsureSelectedAccount();
        var account=saved.Settings.Clone();
        if (!args.Contains("--resume-fixture"))
        {
        GenerateAvi.Run(Path.Combine(root,"fixture.avi"));
        File.Copy(Path.Combine(root,"fixture.avi"),Path.Combine(root,"beta.avi"),true);File.Copy(Path.Combine(root,"fixture.avi"),Path.Combine(root,"movie.avi"),true);
        account=new AccountSettings{M3uUrl="http://127.0.0.1:1/fixture.m3u"};saved.Settings=account.Clone();saved.Name="Remote fixture";saved.LastPlaylistUpdatedUtc=DateTime.UtcNow;
        state.Account=account.Clone();state.CheckForUpdatesOnStartup=false;state.RemoteControlEnabled=true;state.RemoteControlPort=port;store.Save(state);
        var channels=new[]{
            new Channel{Name="Alpha",Group="News",Url=new Uri(Path.Combine(root,"fixture.avi")).AbsoluteUri,MediaKind=MediaKind.Live},
            new Channel{Name="Beta",Group="News",Url=new Uri(Path.Combine(root,"beta.avi")).AbsoluteUri,MediaKind=MediaKind.Live},
            new Channel{Name="Movie",Group="Movies",Url=new Uri(Path.Combine(root,"movie.avi")).AbsoluteUri,MediaKind=MediaKind.Movie}};
        state.FavoriteIds.Add(ItemIdentity.For(channels[0]));state.Recent.Add(new RecentItem{ItemKey=ItemIdentity.For(channels[1]),ChannelId=channels[1].Id,Name="Beta",Group="News",Url=channels[1].Url});store.Save(state);store.SaveChannelCache(saved.Id,channels);
        }
        else port=state.RemoteControlPort;
        var app=new App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();
        // The fixture supplies its own login/cache. Do not open the normal
        // startup account dialog or replace the fixture window during Run().
        var startup=typeof(App).GetMethod("OnStartup",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(object),typeof(StartupEventArgs)},null)!;
        app.Startup-=(StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler),app);
        var login=new LoginResult{Account=account,AccountId=saved.Id,UpdatePlaylist=false};
        window=new MainWindow(login);app.MainWindow=window;
        if (interactive)
        {
            app.ShutdownMode=ShutdownMode.OnMainWindowClose;
            var instructions="ISOLATED PHONE REMOTE FIXTURE\n" +
                "Only invented accounts and generated local silent video are loaded. No provider or update requests.\n" +
                "Open one of these addresses from a browser on this PC or a phone on the same trusted Wi-Fi:\n" +
                string.Join("\n",RemoteControlService.GetLocalUrls(port)) + "\n\n" +
                "Use Items / Live TV, search Beta, clear search, move Up/Down and OK. Check PC selection and playback.\n" +
                "Check Folders, A-Z, All media, Movies, Series (empty), Favorites (Alpha), Recent (Beta), and active filter feedback.\n" +
                "Check Back, previous/next, pause/resume, Stop, volume, mute, Channels and Full/Back.\n" +
                "At 320, 360 and 390 CSS-pixel widths check scrolling, labels and reachable controls.\n" +
                "Disable/re-enable the remote in PC settings and verify phone commands disconnect/reconnect.\n" +
                "Close the PC fixture to stop its listener. Automatic shutdown occurs after 30 minutes.\n" +
                "For persistence, relaunch this folder's RemoteChecks.exe --child --interactive --resume-fixture (same isolated settings).\n" +
                "Record browser/phone model, screen width, actual results and firewall/network limitations.\n";
            File.WriteAllText(Path.Combine(root,"PHONE-TEST.txt"),instructions);
            var stopTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMinutes(30)};
            stopTimer.Tick+=(_,_)=>{stopTimer.Stop();window.Close();};stopTimer.Start();
            window.Show();app.Run();return;
        }
        window.ContentRendered+=async (_,_)=>
        {
            try { await Run(); window.Close(); Check(!((RemoteControlService)Field("_remoteControlService")).IsRunning,"shutdown listener stopped"); await Refused();
                window=new MainWindow(login);app.MainWindow=window;window.Show();await Task.Delay(2000);
                Check((await http.GetStringAsync($"http://127.0.0.1:{port}/health"))=="OK","enabled after window restart");
                var restarted=(AppState)Field("_state");restarted.RemoteControlEnabled=false;store.Save(restarted);Call("ApplyRemoteControlState",false);window.Close();
                window=new MainWindow(login);app.MainWindow=window;window.Show();await Task.Delay(2000);await Refused();Check(!((RemoteControlService)Field("_remoteControlService")).IsRunning,"disabled after window restart");window.Close();
                File.AppendAllText(Path.Combine(root,"evidence.txt"),"ALL PASS\n"); }
            catch(Exception ex) { File.AppendAllText(Path.Combine(root,"evidence.txt"),"FAIL "+ex+"\n"); Environment.ExitCode=1;window.Close(); }
            finally { app.Shutdown(); }
        };
        window.Show();app.Run();
    }
    static async Task Refused()
    {
        try { using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,port); throw new Exception("port remains open"); } catch(SocketException) { Check(true,"port closed"); }
    }
    static async Task Run()
    {
        await Task.Delay(2000);
        var list=(ListBox)window.FindName("ChannelList");var search=(TextBox)window.FindName("SearchBox");
        Check((await http.GetStringAsync($"http://127.0.0.1:{port}/health"))=="OK","enabled on startup");
        File.WriteAllText(Path.Combine(root,"remote.html"),await http.GetStringAsync($"http://127.0.0.1:{port}/"));
        await Cmd("browse-items");await Cmd("media-live");Check(list.Items.Count==2,"live filter");
        await Cmd("search","Beta + & :");Check(search.Text=="Beta + & :","search encoding");
        await Cmd("search","Beta");Check(list.Items.Count==1,"search result");await Cmd("search","");Check(list.Items.Count==2,"clear search");
        list.SelectedIndex=-1;await Cmd("down");Check(list.SelectedIndex==0,"first Down selects first item");await Cmd("down");Check(list.SelectedIndex==1,"Down navigation");await Cmd("up");Check(list.SelectedIndex==0,"Up navigation");
        await Cmd("browse-folders");await Cmd("select");Check(Field("_activeFolder") is string,"folder selection");await Cmd("back");Check(typeof(MainWindow).GetField("_activeFolder",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)==null,"Back folder");
        await Cmd("browse-letters");Check((int)Field("_browseMode")==1,"letters filter");list.SelectedIndex=0;await Cmd("select");Check(Field("_activeLetter") is string,"letter selection");await Cmd("back");Check(Field("_activeLetter")==null,"Back letter");await Cmd("browse-items");await Cmd("media-movies");Check(list.Items.Count==1,"movie filter");await Cmd("media-series");Check(list.Items.Count==0,"series filter");await Cmd("media-all");Check(list.Items.Count==3,"all media filter");
        await Cmd("view-favorites");Check((int)Field("_viewMode")==1 && list.Items.Count==1,"favorites filter");await Cmd("view-recent");Check((int)Field("_viewMode")==2 && list.Items.Count==1,"recent filter");await Cmd("view-all");await Cmd("media-live");
        var remoteState=await http.GetStringAsync($"http://127.0.0.1:{port}/state");Check(remoteState.Contains("\"browseMode\":2") && remoteState.Contains("\"mediaKindMode\":1") && remoteState.Contains("\"viewMode\":0"),"state reflects PC filters");
        list.SelectedIndex=0;await Cmd("select");await Task.Delay(2000);var player=Field("_mediaPlayer") as MediaPlayer;Check(player?.IsPlaying==true,"local native playback");
        var remoteItem = Control<MenuItem>("RemoteControlMenuItem");
        var settingsMenu = Menu("Settings");
        settingsMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, settingsMenu));
        Check(remoteItem.IsChecked && ((AppState)Field("_state")).RemoteControlEnabled && ((RemoteControlService)Field("_remoteControlService")).IsRunning,
            "Phone remote checkmark reflects enabled listener when actual Settings menu opens");
        Click(remoteItem);
        Check(!remoteItem.IsChecked && !((AppState)Field("_state")).RemoteControlEnabled && !((RemoteControlService)Field("_remoteControlService")).IsRunning,
            "actual Phone remote menu click disables listener and clears checkmark");
        // The enable path intentionally opens a modal URL/instructions dialog.
        // Test the production menu-state refresh against a real listener without
        // attempting to automate that separate modal interaction here.
        var remoteAppState = (AppState)Field("_state");
        remoteAppState.RemoteControlEnabled=true;store.Save(remoteAppState);
        Call("ApplyRemoteControlState",false);
        settingsMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, settingsMenu));
        Check(remoteItem.IsChecked && remoteAppState.RemoteControlEnabled && ((RemoteControlService)Field("_remoteControlService")).IsRunning,
            "Phone remote menu checkmark updates on actual Settings open when listener is enabled");
        var activePlayer = Field("_mediaPlayer") as MediaPlayer;
        Check(activePlayer?.IsPlaying == true, "local native playback remains active");
        var appMenuHeaders = MenuHeaders(Control<Menu>("AppMenu")).ToArray();
        Check(!appMenuHeaders.Contains("Picture-in-Picture (PiP)") && !appMenuHeaders.Contains("Playback options", StringComparer.OrdinalIgnoreCase),
            "View and app menus no longer expose PiP or Playback options");
        var moreButton = Control<Button>("MorePlaybackButton");
        Call("MorePlayback_Click", moreButton, new RoutedEventArgs());
        var moreHeaders = MenuHeaders(moreButton.ContextMenu).ToArray();
        Check(!moreHeaders.Contains("Picture-in-Picture (PiP)") && !moreHeaders.Contains("Playback options", StringComparer.OrdinalIgnoreCase) &&
              !moreHeaders.Contains("Multi-view", StringComparer.OrdinalIgnoreCase),
            "More menu no longer exposes removed PiP, Playback Options, or Multi-view entries");
        Check(window.FindName("SchedulesButton") is null,
            "main toolbar no longer exposes the Schedules button");
        moreButton.ContextMenu.IsOpen = false;
        await Cmd("playpause");Check((Field("_mediaPlayer") as MediaPlayer)?.IsPlaying!=true,"pause");await Cmd("playpause");await Task.Delay(1000);Check((Field("_mediaPlayer") as MediaPlayer)?.IsPlaying==true,"resume");
        var state=(AppState)Field("_state");var volume=state.VolumeLevel;await Cmd("volume-down");Check(state.VolumeLevel==volume-5,"volume down");await Cmd("volume-up");Check(state.VolumeLevel==volume,"volume up");await Cmd("mute");Check(state.Muted,"mute");await Cmd("mute");
        var visible=(bool)Field("_channelsVisible");await Cmd("channels");Check((bool)Field("_channelsVisible")!=visible,"channels toggle");if (!(bool)Field("_channelsVisible")) {await Cmd("back");Check((bool)Field("_channelsVisible"),"Back restores channels");}
        await Cmd("fullscreen");Check((bool)Field("_isFullScreen"),"fullscreen");await Cmd("back");Check(!(bool)Field("_isFullScreen"),"Back exits fullscreen");
        await Cmd("next");Check((Field("_currentChannel") as Channel)?.Name=="Beta","next plays Beta");await Cmd("previous");Check((Field("_currentChannel") as Channel)?.Name=="Alpha","previous plays Alpha");await Cmd("stop");Check((Field("_mediaPlayer") as MediaPlayer)?.IsPlaying!=true,"stop");
        Check((await http.GetAsync($"http://127.0.0.1:{port}/cmd?name=invalid")).StatusCode==HttpStatusCode.BadRequest,"unknown command rejected");
        using(var client=new TcpClient()) { await client.ConnectAsync(IPAddress.Loopback,port);var stream=client.GetStream();await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET /hea"));await Task.Delay(50);await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("lth HTTP/1.1\r\nHost: localhost\r\n\r\n"));var buffer=new byte[1024];var count=await stream.ReadAsync(buffer);Check(System.Text.Encoding.ASCII.GetString(buffer,0,count).Contains("200 OK"),"fragmented request"); }
        using var failed=new RemoteControlService();
        try { failed.Start(port,_=>{},()=>default);throw new Exception("occupied bind accepted"); } catch(SocketException) { Check(!failed.IsRunning,"failed bind cleanup"); }
        using var idle=new TcpClient();await idle.ConnectAsync(IPAddress.Loopback,port);await idle.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET /cmd?name=volume-up"));
        state.RemoteControlEnabled=false;store.Save(state);Call("ApplyRemoteControlState",false);Check(!store.Load().RemoteControlEnabled,"disabled persisted");await Refused();
        Check(await idle.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2))==0,"idle partial client closed on disable");
        state.RemoteControlEnabled=true;store.Save(state);Call("ApplyRemoteControlState",false);Check(store.Load().RemoteControlEnabled,"enabled persisted");var before=state.VolumeLevel;Call("HandleRemoteCommand","volume-up",(int)Field("_remoteGeneration")-1);Check(state.VolumeLevel==before,"retired generation command ignored");await Cmd("volume-down");
        for(var i=0;i<10;i++){Call("ApplyRemoteControlState",false);Check((await http.GetStringAsync($"http://127.0.0.1:{port}/health"))=="OK","restart "+i);}
        // Exercise protected backup preferences through the actual window's
        // state/application path. File picker and confirmation dialogs are not
        // driven here; this does not establish interactive restore UI evidence.
        var enabledBackup=Path.Combine(root,"remote-enabled.zip");store.CreateBackup(enabledBackup);
        state.RemoteControlEnabled=false;store.Save(state);Call("ApplyRemoteControlState",false);
        var disabledBackup=Path.Combine(root,"remote-disabled.zip");store.CreateBackup(disabledBackup);
        store.RestoreBackup(enabledBackup);state=store.Load();
        typeof(MainWindow).GetField("_state",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,state);
        Call("ApplyRemoteControlState",false);
        Check(state.RemoteControlEnabled && state.RemoteControlPort==port,"protected backup restores enabled remote preferences");
        Check((await http.GetStringAsync($"http://127.0.0.1:{port}/health"))=="OK","restored enabled preferences start listener");
        store.RestoreBackup(disabledBackup);state=store.Load();
        typeof(MainWindow).GetField("_state",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,state);
        Call("ApplyRemoteControlState",false);
        Check(!state.RemoteControlEnabled,"protected backup restores disabled remote preferences");await Refused();
        Check(!((RemoteControlService)Field("_remoteControlService")).IsRunning,"restored disabled preferences stop listener");
        state.RemoteControlEnabled=true;store.Save(state);Call("ApplyRemoteControlState",false);
    }
}
