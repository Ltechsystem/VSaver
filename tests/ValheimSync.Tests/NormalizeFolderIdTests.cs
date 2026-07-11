using ValheimSync.App.ViewModels;
using Xunit;

namespace ValheimSync.Tests;

public sealed class NormalizeFolderIdTests
{
    private const string Id = "1dgMEB7cihhT78u1xdyOQjARhsZukyERb";

    [Theory]
    [InlineData(Id, Id)]                                                            // bare id
    [InlineData("  " + Id + "  ", Id)]                                              // whitespace
    [InlineData("https://drive.google.com/drive/folders/" + Id, Id)]                // plain link
    [InlineData("https://drive.google.com/drive/folders/" + Id + "?hl=da", Id)]     // query string
    [InlineData("https://drive.google.com/drive/folders/" + Id + "/", Id)]          // trailing slash
    [InlineData("https://drive.google.com/drive/folders/" + Id + "#frag", Id)]      // fragment
    [InlineData("https://drive.google.com/drive/u/0/folders/" + Id + "?usp=sharing", Id)] // /u/0/ variant
    [InlineData("https://drive.google.com/file/d/" + Id + "/view?usp=sharing", Id)] // /d/ file link
    [InlineData("https://drive.google.com/open?id=" + Id + "&usp=drive_link", Id)]  // open?id= style
    [InlineData("HTTPS://DRIVE.GOOGLE.COM/DRIVE/FOLDERS/" + Id, Id)]                // marker case-insensitive
    public void ExtractsBareId_FromEveryLinkShape(string input, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.NormalizeFolderId(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInput_GivesEmpty(string input) =>
        Assert.Equal("", MainWindowViewModel.NormalizeFolderId(input));
}
