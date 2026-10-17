using System.Globalization;
using OpenVideoSmartShare.Library;

namespace OpenVideoSmartShare.Tests;

public class LibraryTests
{
    [Theory]
    [InlineData(".mkv", "video/x-mkv")]
    [InlineData("MP4", "video/mp4")]
    [InlineData("mov", "video/mp4")]
    [InlineData("ts", "video/mpeg")]
    [InlineData("avi", "video/avi")]
    [InlineData("srt", null)]
    [InlineData("dat", null)]
    public void MapsVideoExtensionsToMimeTypes(string extension, string? mime)
    {
        Assert.Equal(mime, MediaTypes.GetVideoMime(extension));
    }

    [Fact]
    public void FindsSubtitleWithSameBaseName()
    {
        var found = SubtitleFinder.Find(@"Z:\Film\Movie.2024.mkv", ["Movie.2024.mkv", "Movie.2024.srt", "Other.srt"]);
        Assert.Equal(@"Z:\Film\Movie.2024.srt", found);
    }

    [Fact]
    public void PrefersExactNameThenPreferredLanguageTag()
    {
        var tags = SubtitleFinder.LanguageTags(CultureInfo.GetCultureInfo("it-IT"));
        Assert.Contains("ita", tags);

        var tagged = SubtitleFinder.Find(@"C:\v\Movie.mkv", ["Movie.eng.srt", "Movie.ita.srt"], tags);
        Assert.Equal(@"C:\v\Movie.ita.srt", tagged);

        var exact = SubtitleFinder.Find(@"C:\v\Movie.mkv", ["Movie.ita.srt", "Movie.smi"], tags);
        Assert.Equal(@"C:\v\Movie.smi", exact);
    }

    [Fact]
    public void IgnoresVobSubAndUnrelatedFiles()
    {
        Assert.Null(SubtitleFinder.Find(@"C:\v\Movie.mkv", ["Movie.idx", "Movie.sub", "Movie2.srt", "Movie.txt"]));
    }

    [Theory]
    [InlineData("Z:", @"Z:\")]
    [InlineData(@"Z:\", @"Z:\")]
    [InlineData(@"C:\Video\", @"C:\Video")]
    [InlineData(@"\\nas\share\film\", @"\\nas\share\film")]
    public void NormalizesRoots(string input, string expected)
    {
        Assert.Equal(expected, MediaLibrary.NormalizeRoot(input));
    }

    [Fact]
    public void BuildsTreeWithAllVideosAndOnlyFoldersContainingVideos()
    {
        var root = VideoItem.MakeId('d', @"Z:\");
        var series = VideoItem.MakeId('d', @"Z:\Series");
        var empty = VideoItem.MakeId('d', @"Z:\Empty");
        var items = new[] { Video(@"Z:\Series\B.mkv", series), Video(@"Z:\A.mp4", root) };
        var snapshot = LibrarySnapshot.Build(items, [(root, "0", @"Z:\"), (series, root, "Series"), (empty, root, "Empty")], 7, [], DateTime.Now);

        Assert.Equal(["A", "B"], snapshot.Items.Select(i => i.Title));
        var top = snapshot.Containers["0"];
        Assert.Equal(LibrarySnapshot.AllVideosId, top.Containers[0].Id);
        Assert.Equal(root, top.Containers[1].Id);
        Assert.False(snapshot.Containers.ContainsKey(empty));
        Assert.Equal(2, snapshot.Containers[root].ChildCount);
        Assert.Equal(2, snapshot.Containers[LibrarySnapshot.AllVideosId].Items.Count);
    }

    internal static VideoItem Video(string path, string parentId, string? subtitle = null) => new()
    {
        Id = VideoItem.MakeId('v', path),
        FullPath = path,
        ParentId = parentId,
        MimeType = MediaTypes.GetVideoMime(Path.GetExtension(path))!,
        Size = 1234,
        Modified = new DateTime(2026, 5, 8, 22, 57, 0),
        ExternalSubtitles = subtitle == null ? [] : [subtitle],
    };
}
