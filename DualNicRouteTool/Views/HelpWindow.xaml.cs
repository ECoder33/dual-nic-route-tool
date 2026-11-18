using System.Windows;
using DualNicRouteTool.Services;

namespace DualNicRouteTool.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        DataPathText.Text =
            $"{AppEnvironment.AppName} v{AppEnvironment.Version}\n" +
            $"备份与设置保存在：{AppEnvironment.DataDirectory}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
