using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Shared entry point for the 🔁 buttons. Lets the user pick which diagram type to
/// open for a given function/method (Programmablaufplan now, Struktogramm later),
/// so all call sites behave identically.
/// </summary>
public static class DiagramLauncher
{
    public static void ChooseAndOpen(Window owner, string projFolder, string key, string title, string? themePath)
    {
        var dlg = new Window
        {
            Title                 = "Diagram",
            Width                 = 360,
            Height                = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = owner,
            ResizeMode            = ResizeMode.NoResize
        };
        if (!string.IsNullOrWhiteSpace(themePath))
        {
            try { var d = OxsuitLoader.Load(themePath); if (d is not null) dlg.Resources.MergedDictionaries.Add(d); } catch { }
        }
        dlg.SetResourceReference(Control.BackgroundProperty, "ContentBgBrush");
        dlg.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(dlg);

        var stack = new StackPanel { Margin = new Thickness(16) };
        dlg.Content = stack;

        var hdr = new TextBlock
        {
            Text       = $"Sketch the flow of:\n{title}",
            FontSize   = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 12)
        };
        hdr.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        stack.Children.Add(hdr);

        bool papExists = FlowChartService.Exists(projFolder, key);

        var papBtn = MakeBtn(papExists ? "🔁 Programmablaufplan (exists)" : "🔁 Programmablaufplan");
        papBtn.Click += (_, _) =>
        {
            dlg.Close();
            new FlowChartWindow(projFolder, key, title, themePath) { Owner = owner }.Show();
        };
        stack.Children.Add(papBtn);

        bool nsExists = StructogramService.Exists(projFolder, key);
        var nsBtn = MakeBtn(nsExists ? "▦ Struktogramm (exists)" : "▦ Struktogramm");
        nsBtn.Margin = new Thickness(0, 6, 0, 0);
        nsBtn.ToolTip = "Nassi-Shneiderman structogram editor (DIN 66261)";
        nsBtn.Click += (_, _) =>
        {
            dlg.Close();
            new StructogramWindow(projFolder, key, title, themePath) { Owner = owner }.Show();
        };
        stack.Children.Add(nsBtn);

        dlg.ShowDialog();
    }

    private static Button MakeBtn(string label)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        b.SetResourceReference(Control.StyleProperty,      "ModernButton");
        b.SetResourceReference(Control.BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(Control.ForegroundProperty, "SidebarTextBrush");
        return b;
    }
}
