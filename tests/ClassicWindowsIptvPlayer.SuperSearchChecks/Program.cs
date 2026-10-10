using System.Collections;
using System.Reflection;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

var catalog = new List<Channel>
{
    new() { Name = "Morning News", Group = "Local", MediaKind = MediaKind.Live },
    new() { Name = "Evening News", Group = "Local", MediaKind = MediaKind.Live },
    new() { Name = "Weather", Group = "Newsroom", MediaKind = MediaKind.Live },
    new() { Name = "News Movie", Group = "Cinema", MediaKind = MediaKind.Movie },
    new() { Name = "News Series", Group = "Drama", MediaKind = MediaKind.Series },
    new() { Name = "Unrelated", Group = "Other", MediaKind = MediaKind.Movie }
};
var method = typeof(MainWindow).GetMethod("BuildSuperSearchResult", BindingFlags.NonPublic | BindingFlags.Static)!;
var organization = new Dictionary<string, GroupOrganization>();
var index = new MediaSearchIndex(catalog);
List<object> Sections(MediaKind preferred, string query)
{
    var result = method.Invoke(null, [catalog, index, query, preferred, organization, CancellationToken.None])!;
    return ((IEnumerable)result.GetType().GetProperty("SearchSections")!.GetValue(result)!).Cast<object>().ToList();
}
object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var sections = Sections(MediaKind.Live, "news");
Check(sections.Count == 3, "Search must include all media types.");
Check((MediaKind)Property(sections[0], "Kind") == MediaKind.Live &&
      (MediaKind)Property(sections[1], "Kind") == MediaKind.Movie &&
      (MediaKind)Property(sections[2], "Kind") == MediaKind.Series,
    "Live search must rank Live, Movies, Series.");
var liveFolders = ((IEnumerable)Property(sections[0], "Folders")).Cast<object>().ToList();
Check(liveFolders.Count == 2 && (string)Property(liveFolders[0], "Name") == "Local" &&
      (int)Property(liveFolders[0], "Count") == 2 &&
      (string)Property(liveFolders[1], "Name") == "Newsroom",
    "Matching parent folders and their counts must appear before items.");
var liveItems = ((IEnumerable)Property(sections[0], "Channels")).Cast<Channel>().ToList();
Check(liveItems.Count == 3 && liveItems[0].Name == "Morning News" &&
      liveItems[1].Name == "Evening News" && liveItems[2].Name == "Weather",
    "Items must be grouped under their matching folders.");
Check((MediaKind)Property(Sections(MediaKind.Movie, "news")[0], "Kind") == MediaKind.Movie,
    "The selected media type must appear first.");
Check(Sections(MediaKind.Series, "no matching title").Count == 0, "Unmatched query must be empty.");
Console.WriteLine("Super search checks passed.");
