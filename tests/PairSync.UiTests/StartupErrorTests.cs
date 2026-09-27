using Avalonia.Controls;
using PairSync.Desktop;

namespace PairSync.UiTests;

public sealed class StartupErrorTests(HeadlessFixture ui)
{
    [Fact]
    public Task Startup_error_shows_reason_and_data_folder() => ui.RunAsync(() =>
    {
        var window = new StartupErrorWindow("The database was created by a newer version of PairSync.", @"C:\Users\x\AppData\Roaming\PairSync");
        window.Show();

        Assert.Equal("The database was created by a newer version of PairSync.", window.FindControl<TextBlock>("Message")!.Text);
        Assert.Equal(@"C:\Users\x\AppData\Roaming\PairSync", window.FindControl<TextBlock>("Folder")!.Text);
        Assert.Equal("Quit", window.FindControl<Button>("Quit")!.Content);
        window.Close();
    });
}
