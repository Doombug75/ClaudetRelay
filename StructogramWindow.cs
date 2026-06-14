using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudetRelay.Models;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Nassi-Shneiderman structogram editor (DIN 66261). Blocks nest space-fillingly —
/// no free positioning, no arrows. The structure is edited via context menus and
/// re-rendered on every change.
/// </summary>
public class StructogramWindow : Window
{
    private readonly string _projFolder;
    private readonly string _key;
    private readonly string? _themePath;
    private StructogramData _data;

    private Border? _hostBorder;

    public StructogramWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        _data       = StructogramService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;

        Title                 = "▦  Struktogramm — " + (string.IsNullOrEmpty(title) ? "Untitled" : title);
        Width                 = 760;
        Height                = 620;
        MinWidth              = 420;
        MinHeight             = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
        {
            try { var d = OxsuitLoader.Load(themePath); if (d is not null) Resources.MergedDictionaries.Add(d); } catch { }
        }
        SetResourceReference(BackgroundProperty, "ContentBgBrush");
        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);
        Loaded            += (_, _) => Build();
    }

    private void Save() => StructogramService.Save(_projFolder, _key, _data);

    private void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        // Toolbar
        var bar = new Border { Padding = new Thickness(12, 8, 12, 8) };
        bar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(bar, 0); root.Children.Add(bar);
        var barRow = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = barRow;

        var hint = new TextBlock
        {
            Text = "Right-click any block to edit / insert / wrap. Use ＋ to add.",
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        barRow.Children.Add(hint);

        // Scrollable diagram host
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20)
        };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        _hostBorder = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, MinWidth = 360 };
        scroll.Content = _hostBorder;

        Rebuild();
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings(), scaleWindow: false);
    }

    private void Rebuild()
    {
        if (_hostBorder is null) return;
        _hostBorder.Child = RenderSequence(_data.Root, isRoot: true);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private FrameworkElement RenderSequence(List<NsBlock> seq, bool isRoot = false)
    {
        var sp = new StackPanel();
        if (seq.Count == 0)
        {
            sp.Children.Add(EmptyPlaceholder(seq));
            return sp;
        }
        foreach (var b in seq)
            sp.Children.Add(RenderBlock(b, seq));
        // trailing add affordance for root only (keeps nested boxes tight)
        if (isRoot) sp.Children.Add(AddRowButton(seq));
        return sp;
    }

    private FrameworkElement RenderBlock(NsBlock b, List<NsBlock> parent)
    {
        FrameworkElement inner = b.Kind switch
        {
            NsBlockKind.Statement => StatementBox(b),
            NsBlockKind.If        => IfBox(b),
            NsBlockKind.While     => LoopBox(b, preTest: true),
            NsBlockKind.DoWhile   => LoopBox(b, preTest: false),
            NsBlockKind.Case      => CaseBox(b),
            _                     => StatementBox(b)
        };

        var cell = new Border
        {
            BorderThickness = new Thickness(1),
            Child           = inner
        };
        cell.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        cell.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
        cell.MouseRightButtonDown += (_, e) => { ShowBlockMenu(b, parent); e.Handled = true; };
        return cell;
    }

    private FrameworkElement StatementBox(NsBlock b)
    {
        var t = LabelText(string.IsNullOrWhiteSpace(b.Text) ? "(statement)" : b.Text);
        t.Margin = new Thickness(8, 6, 8, 6);
        t.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) { EditText(b); e.Handled = true; } };
        return t;
    }

    private FrameworkElement IfBox(NsBlock b)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // condition header
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // branch labels
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // branches

        var cond = LabelText(string.IsNullOrWhiteSpace(b.Text) ? "(condition)" : b.Text);
        cond.TextAlignment = TextAlignment.Center;
        cond.Margin = new Thickness(8, 6, 8, 6);
        cond.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) { EditText(b); e.Handled = true; } };
        Grid.SetRow(cond, 0);
        grid.Children.Add(cond);

        // branch label row (T / F)
        var labelRow = new Grid();
        labelRow.ColumnDefinitions.Add(new ColumnDefinition());
        labelRow.ColumnDefinitions.Add(new ColumnDefinition());
        var tl = LabelText("true");  tl.FontSize = 10; tl.Opacity = 0.7; tl.HorizontalAlignment = HorizontalAlignment.Center;
        var fl = LabelText("false"); fl.FontSize = 10; fl.Opacity = 0.7; fl.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(fl, 1);
        labelRow.Children.Add(tl); labelRow.Children.Add(fl);
        Grid.SetRow(labelRow, 1);
        grid.Children.Add(TopBorder(labelRow));

        // two branch columns
        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var thenCol = RenderSequence(b.Body);
        var elseCol = RenderSequence(b.Else);
        Grid.SetColumn(thenCol, 0);
        var elseWrap = LeftBorder(elseCol); Grid.SetColumn(elseWrap, 1);
        cols.Children.Add(thenCol);
        cols.Children.Add(elseWrap);
        Grid.SetRow(cols, 2);
        grid.Children.Add(TopBorder(cols));

        return grid;
    }

    private FrameworkElement LoopBox(NsBlock b, bool preTest)
    {
        var outer = new StackPanel();
        var cond = LabelText(string.IsNullOrWhiteSpace(b.Text) ? (preTest ? "while (…)" : "do … while (…)") : b.Text);
        cond.Margin = new Thickness(8, 5, 8, 5);
        cond.FontStyle = FontStyles.Italic;
        cond.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) { EditText(b); e.Handled = true; } };

        var body = RenderSequence(b.Body);
        var bodyWrap = new Border
        {
            Child  = body,
            Margin = new Thickness(14, 0, 0, 0),  // inset = loop bracket
            BorderThickness = new Thickness(1, 1, 0, 1)
        };
        bodyWrap.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        if (preTest) { outer.Children.Add(cond); outer.Children.Add(bodyWrap); }
        else         { outer.Children.Add(bodyWrap); outer.Children.Add(TopBorder(cond)); }
        return outer;
    }

    private FrameworkElement CaseBox(NsBlock b)
    {
        var outer = new StackPanel();
        var head = LabelText(string.IsNullOrWhiteSpace(b.Text) ? "(selector)" : b.Text);
        head.TextAlignment = TextAlignment.Center;
        head.Margin = new Thickness(8, 5, 8, 5);
        head.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) { EditText(b); e.Handled = true; } };
        outer.Children.Add(head);

        var cols = new Grid();
        if (b.Arms.Count == 0) b.Arms.Add(new NsArm());
        for (int i = 0; i < b.Arms.Count; i++)
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < b.Arms.Count; i++)
        {
            var arm = b.Arms[i];
            var col = new StackPanel();
            var lbl = LabelText(string.IsNullOrWhiteSpace(arm.Label) ? "case" : arm.Label);
            lbl.FontSize = 10; lbl.Opacity = 0.8; lbl.TextAlignment = TextAlignment.Center; lbl.Margin = new Thickness(4, 3, 4, 3);
            var capArm = arm;
            lbl.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) { EditArmLabel(capArm); e.Handled = true; } };
            col.Children.Add(lbl);
            col.Children.Add(TopBorder(RenderSequence(arm.Body)));

            var colWrap = i == 0 ? (FrameworkElement)col : LeftBorder(col);
            Grid.SetColumn(colWrap, i);
            cols.Children.Add(colWrap);
        }
        outer.Children.Add(TopBorder(cols));
        return outer;
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    private void ShowBlockMenu(NsBlock b, List<NsBlock> parent)
    {
        var cm = new ContextMenu();

        var edit = new MenuItem { Header = "✎ Edit text…" };
        edit.Click += (_, _) => EditText(b);
        cm.Items.Add(edit);

        cm.Items.Add(new Separator());
        cm.Items.Add(InsertMenu("Insert above", parent, parent.IndexOf(b)));
        cm.Items.Add(InsertMenu("Insert below", parent, parent.IndexOf(b) + 1));

        // For containers, allow adding into their sub-sequences
        if (b.Kind is NsBlockKind.While or NsBlockKind.DoWhile)
            cm.Items.Add(InsertMenu("Add to loop body", b.Body, b.Body.Count));
        if (b.Kind == NsBlockKind.If)
        {
            cm.Items.Add(InsertMenu("Add to TRUE branch", b.Body, b.Body.Count));
            cm.Items.Add(InsertMenu("Add to FALSE branch", b.Else, b.Else.Count));
        }
        if (b.Kind == NsBlockKind.Case)
        {
            var addArm = new MenuItem { Header = "Add case arm" };
            addArm.Click += (_, _) => { b.Arms.Add(new NsArm()); Save(); Rebuild(); };
            cm.Items.Add(addArm);
        }

        cm.Items.Add(new Separator());
        var del = new MenuItem { Header = "✕ Delete block" };
        del.Click += (_, _) => { parent.Remove(b); Save(); Rebuild(); };
        cm.Items.Add(del);

        cm.IsOpen = true;
    }

    private MenuItem InsertMenu(string header, List<NsBlock> seq, int index)
    {
        var mi = new MenuItem { Header = header };
        void Add(string label, NsBlockKind kind)
        {
            var sub = new MenuItem { Header = label };
            sub.Click += (_, _) =>
            {
                var nb = new NsBlock { Kind = kind, Text = DefaultText(kind) };
                seq.Insert(Math.Clamp(index, 0, seq.Count), nb);
                Save(); Rebuild();
            };
            mi.Items.Add(sub);
        }
        Add("Statement", NsBlockKind.Statement);
        Add("If / Else", NsBlockKind.If);
        Add("While loop (pre-test)", NsBlockKind.While);
        Add("Do-While loop (post-test)", NsBlockKind.DoWhile);
        Add("Case (multi-way)", NsBlockKind.Case);
        return mi;
    }

    private Button AddRowButton(List<NsBlock> seq)
    {
        var b = new Button
        {
            Content = "＋", FontSize = 14, Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.Hand, ToolTip = "Add a block"
        };
        b.SetResourceReference(Button.StyleProperty,      "ModernButton");
        b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        b.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            // Build a flat menu of the five kinds
            void Add(string label, NsBlockKind kind)
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (_, _) => { seq.Add(new NsBlock { Kind = kind, Text = DefaultText(kind) }); Save(); Rebuild(); };
                cm.Items.Add(mi);
            }
            Add("Statement", NsBlockKind.Statement);
            Add("If / Else", NsBlockKind.If);
            Add("While loop (pre-test)", NsBlockKind.While);
            Add("Do-While loop (post-test)", NsBlockKind.DoWhile);
            Add("Case (multi-way)", NsBlockKind.Case);
            cm.IsOpen = true;
        };
        return b;
    }

    private FrameworkElement EmptyPlaceholder(List<NsBlock> seq)
    {
        var b = new Button
        {
            Content = "＋ add", FontSize = 11, Padding = new Thickness(6, 3, 6, 3),
            Cursor = Cursors.Hand, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        b.SetResourceReference(Button.StyleProperty,      "ModernButton");
        b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        b.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string label, NsBlockKind kind)
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (_, _) => { seq.Add(new NsBlock { Kind = kind, Text = DefaultText(kind) }); Save(); Rebuild(); };
                cm.Items.Add(mi);
            }
            Add("Statement", NsBlockKind.Statement);
            Add("If / Else", NsBlockKind.If);
            Add("While loop (pre-test)", NsBlockKind.While);
            Add("Do-While loop (post-test)", NsBlockKind.DoWhile);
            Add("Case (multi-way)", NsBlockKind.Case);
            cm.IsOpen = true;
        };
        return b;
    }

    private void EditText(NsBlock b)
    {
        var t = PromptText(b.Kind == NsBlockKind.Statement ? "Statement" : "Condition / expression", b.Text);
        if (t is null) return;
        b.Text = t; Save(); Rebuild();
    }

    private void EditArmLabel(NsArm arm)
    {
        var t = PromptText("Case label", arm.Label);
        if (t is null) return;
        arm.Label = t; Save(); Rebuild();
    }

    private static string DefaultText(NsBlockKind k) => k switch
    {
        NsBlockKind.If      => "condition",
        NsBlockKind.While   => "while (condition)",
        NsBlockKind.DoWhile => "do … while (condition)",
        NsBlockKind.Case    => "selector",
        _                   => "statement"
    };

    // ── Visual helpers ───────────────────────────────────────────────────────

    private TextBlock LabelText(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        return t;
    }

    private Border TopBorder(UIElement child)
    {
        var b = new Border { Child = child, BorderThickness = new Thickness(0, 1, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        return b;
    }

    private Border LeftBorder(UIElement child)
    {
        var b = new Border { Child = child, BorderThickness = new Thickness(1, 0, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        return b;
    }

    private string? PromptText(string title, string initial)
    {
        var dlg = new Window
        {
            Title = title, Width = 380, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize
        };
        if (!string.IsNullOrWhiteSpace(_themePath))
        { try { var d = OxsuitLoader.Load(_themePath); if (d is not null) dlg.Resources.MergedDictionaries.Add(d); } catch { } }
        dlg.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        dlg.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(dlg);

        var g = new Grid { Margin = new Thickness(14) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dlg.Content = g;

        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        box.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(box, 0); g.Children.Add(box);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 1); g.Children.Add(btnRow);
        string? result = null;
        var ok = MakeBtn("OK");
        ok.Click += (_, _) => { result = box.Text; dlg.DialogResult = true; };
        box.KeyDown += (_, e) => { if (e.Key == Key.Return) { result = box.Text; dlg.DialogResult = true; } };
        btnRow.Children.Add(ok);
        var cancel = MakeBtn("Cancel"); cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => dlg.DialogResult = false;
        btnRow.Children.Add(cancel);

        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? result : null;
    }

    private Button MakeBtn(string label)
    {
        var b = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), FontSize = 12, Margin = new Thickness(0, 0, 4, 0) };
        b.SetResourceReference(StyleProperty,      "ModernButton");
        b.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(ForegroundProperty, "SidebarTextBrush");
        return b;
    }
}
