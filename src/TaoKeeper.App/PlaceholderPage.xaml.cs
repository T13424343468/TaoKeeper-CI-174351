using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TaoKeeper.App;

/// <summary>占位页 code-behind（文案由主窗导航时填入；见 PlaceholderPage.xaml 注释）。</summary>
public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var title = e.Parameter as string ?? "页面";
        PageTitle.Text = title;
        Hint.Text = "该入口的页面没有落地，请从左侧导航重新进入。";
    }
}
