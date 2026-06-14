using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Models;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Standalone editor for a single CodeEntity — name, type, namespace, inheritance,
/// fields, methods, enum values and flow ports. Used by both the code board window
/// and the code library. Persists the entity on Save and sets <see cref="Saved"/>.
/// </summary>
public class CodeEntityEditorDialog : Window
{
    private readonly string _projFolder;
    private readonly CodeEntity _entity;
    private readonly IReadOnlyDictionary<string, CodeEntity> _known;
    private readonly string? _themePath;

    /// <summary>True if the user saved (entity persisted to disk).</summary>
    public bool Saved { get; private set; }
    /// <summary>The entity type before editing (useful to detect a type change for callers).</summary>
    public CodeEntityType OldType { get; private set; }

    public CodeEntityEditorDialog(string projFolder, CodeEntity entity,
        IReadOnlyDictionary<string, CodeEntity> knownEntities, string? themePath)
    {
        _projFolder = projFolder;
        _entity     = entity;
        _known      = knownEntities;
        _themePath  = themePath;
        OldType     = entity.EntityType;

        Title                 = string.Format(Properties.Loc.S("CodeEdit_Title"), entity.Name);
        Width                 = 560;
        Height                = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.CanResize;

        if (!string.IsNullOrWhiteSpace(themePath))
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        SetResourceReference(BackgroundProperty, "ContentBgBrush");
        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);

        BuildForm();
    }

    private void BuildForm()
    {
        var entity = _entity;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var form       = new StackPanel();
        formScroll.Content = form;
        Grid.SetRow(formScroll, 0); root.Children.Add(formScroll);

        // Name
        form.Children.Add(FieldLabel(Properties.Loc.S("Common_Name")));
        var nameBox = EditorTextBox(entity.Name);
        form.Children.Add(nameBox);

        // Type
        form.Children.Add(FieldLabel(Properties.Loc.S("CodeEdit_Type")));
        var typeCombo = EditorCombo();
        foreach (var et in Enum.GetValues<CodeEntityType>()) typeCombo.Items.Add(et);
        typeCombo.SelectedItem = entity.EntityType;
        form.Children.Add(typeCombo);

        // Namespace
        form.Children.Add(FieldLabel(Properties.Loc.S("CodeEdit_Namespace")));
        var nsBox = EditorTextBox(entity.Namespace);
        form.Children.Add(nsBox);

        // Description
        form.Children.Add(FieldLabel(Properties.Loc.S("CodeEdit_Description")));
        var descBox = EditorTextBox(entity.Description, multiLine: true);
        form.Children.Add(descBox);

        // Inheritance (Class/Struct)
        var inheritSection = new StackPanel();
        inheritSection.Children.Add(FieldLabel(Properties.Loc.S("CodeEdit_Inherits")));
        var baseCombo = EditorCombo();
        baseCombo.Items.Add(new ComboItem(Properties.Loc.S("Common_None"), ""));
        foreach (var e in AllEntitiesOfTypes(CodeEntityType.Class, CodeEntityType.Struct).Where(e => e.Id != entity.Id))
            baseCombo.Items.Add(new ComboItem(e.Name, e.Id));
        baseCombo.SelectedItem = baseCombo.Items.Cast<ComboItem>().FirstOrDefault(c => c.Id == entity.BaseClassId)
                                 ?? baseCombo.Items[0];
        inheritSection.Children.Add(baseCombo);

        inheritSection.Children.Add(FieldLabel(Properties.Loc.S("CodeEdit_Implements")));
        var implPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var implChecks = new List<(string Id, CheckBox Cb)>();
        foreach (var iface in AllEntitiesOfTypes(CodeEntityType.Interface))
        {
            var cb = new CheckBox
            {
                Content   = iface.Name,
                IsChecked = entity.ImplementsIds.Contains(iface.Id),
                Margin    = new Thickness(0, 1, 0, 1)
            };
            cb.SetResourceReference(CheckBox.ForegroundProperty, "SidebarTextBrush");
            implPanel.Children.Add(cb);
            implChecks.Add((iface.Id, cb));
        }
        if (implChecks.Count == 0)
            implPanel.Children.Add(new TextBlock { Text = Properties.Loc.S("CodeEdit_NoInterfaces"), Opacity = 0.5, FontSize = 10, FontStyle = FontStyles.Italic });
        inheritSection.Children.Add(implPanel);
        form.Children.Add(inheritSection);

        // Object: instance-of
        var instanceSection = new StackPanel();
        instanceSection.Children.Add(FieldLabel(Properties.Loc.S("CodeEdit_InstanceOf")));
        var instCombo = EditorCombo();
        instCombo.Items.Add(new ComboItem("(none)", ""));
        foreach (var e in AllEntitiesOfTypes(CodeEntityType.Class, CodeEntityType.Struct))
            instCombo.Items.Add(new ComboItem(e.Name, e.Id));
        instCombo.SelectedItem = instCombo.Items.Cast<ComboItem>().FirstOrDefault(c => c.Id == entity.InstanceOfId)
                                 ?? instCombo.Items[0];
        instanceSection.Children.Add(instCombo);
        form.Children.Add(instanceSection);

        // Fields editor (collapsible, collapsed by default for a clean overview)
        var workFields = entity.Fields.Select(f => new CodeField
        { Name = f.Name, DataType = f.DataType, Visibility = f.Visibility, IsStatic = f.IsStatic, DefaultValue = f.DefaultValue }).ToList();

        var fieldsSection = CollapsibleSection(Properties.Loc.S("CodeEdit_Fields"), startCollapsed: workFields.Count > 0,
            out var addFieldBtn, out var fieldStack, out var fieldCount);
        form.Children.Add(fieldsSection);

        void RebuildFieldRows()
        {
            fieldCount.Text = $"({workFields.Count})";
            fieldStack.Children.Clear();
            for (int i = 0; i < workFields.Count; i++)
            {
                var ci  = i;
                var f   = workFields[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                var visCombo = SmallCombo(64);
                foreach (var v in Enum.GetValues<CodeVisibility>()) visCombo.Items.Add(v);
                visCombo.SelectedItem = f.Visibility;
                visCombo.SelectionChanged += (_, _) => { if (visCombo.SelectedItem is CodeVisibility v) workFields[ci].Visibility = v; };
                row.Children.Add(visCombo);

                var nm = SmallBox(110, f.Name);
                nm.TextChanged += (_, _) => workFields[ci].Name = nm.Text;
                row.Children.Add(nm);

                var ty = SmallBox(90, f.DataType);
                ty.TextChanged += (_, _) => workFields[ci].DataType = ty.Text;
                row.Children.Add(ty);

                var stat = new CheckBox { Content = "static", IsChecked = f.IsStatic, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
                stat.SetResourceReference(CheckBox.ForegroundProperty, "SidebarTextBrush");
                stat.Checked   += (_, _) => workFields[ci].IsStatic = true;
                stat.Unchecked += (_, _) => workFields[ci].IsStatic = false;
                row.Children.Add(stat);

                var del = Btn("✕");
                del.Click += (_, _) => { workFields.RemoveAt(ci); RebuildFieldRows(); };
                row.Children.Add(del);

                fieldStack.Children.Add(row);
            }
        }
        RebuildFieldRows();
        addFieldBtn.Click += (_, _) => { workFields.Add(new CodeField()); RebuildFieldRows(); };

        // Methods editor (collapsible, collapsed by default for a clean overview)
        var workMethods = entity.Methods.Select(m => new CodeMethod
        {
            Id = m.Id, Name = m.Name, ReturnType = m.ReturnType, Visibility = m.Visibility, IsStatic = m.IsStatic,
            Parameters = m.Parameters.Select(p => new CodeParam { Name = p.Name, DataType = p.DataType, Convention = p.Convention }).ToList()
        }).ToList();

        var methodsSection = CollapsibleSection(Properties.Loc.S("CodeEdit_Methods"), startCollapsed: workMethods.Count > 0,
            out var addMethodBtn, out var methodStack, out var methodCount);
        form.Children.Add(methodsSection);

        void RebuildMethodRows()
        {
            methodCount.Text = $"({workMethods.Count})";
            methodStack.Children.Clear();
            for (int i = 0; i < workMethods.Count; i++)
            {
                var ci  = i;
                var m   = workMethods[i];
                var box = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(4),
                    Padding         = new Thickness(6),
                    Margin          = new Thickness(0, 2, 0, 2)
                };
                box.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                var mStack = new StackPanel();
                box.Child = mStack;

                var topRow = new StackPanel { Orientation = Orientation.Horizontal };
                var visCombo = SmallCombo(64);
                foreach (var v in Enum.GetValues<CodeVisibility>()) visCombo.Items.Add(v);
                visCombo.SelectedItem = m.Visibility;
                visCombo.SelectionChanged += (_, _) => { if (visCombo.SelectedItem is CodeVisibility v) workMethods[ci].Visibility = v; };
                topRow.Children.Add(visCombo);

                var nm = SmallBox(110, m.Name);
                nm.TextChanged += (_, _) => workMethods[ci].Name = nm.Text;
                topRow.Children.Add(nm);

                var retLbl = new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
                retLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
                topRow.Children.Add(retLbl);
                var ret = SmallBox(80, m.ReturnType);
                ret.TextChanged += (_, _) => workMethods[ci].ReturnType = ret.Text;
                topRow.Children.Add(ret);

                var stat = new CheckBox { Content = "static", IsChecked = m.IsStatic, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
                stat.SetResourceReference(CheckBox.ForegroundProperty, "SidebarTextBrush");
                stat.Checked   += (_, _) => workMethods[ci].IsStatic = true;
                stat.Unchecked += (_, _) => workMethods[ci].IsStatic = false;
                topRow.Children.Add(stat);

                var flowM = Btn("🔁");
                flowM.ToolTip = Properties.Loc.S("CodeEdit_MethodFlowTip");
                flowM.Click += (_, _) =>
                {
                    var key   = $"{entity.Id}#{m.Id}";
                    var title = $"{entity.Name}.{m.Name}";
                    DiagramLauncher.ChooseAndOpen(this, _projFolder, key, title, _themePath);
                };
                topRow.Children.Add(flowM);

                var delM = Btn("✕");
                delM.Click += (_, _) => { workMethods.RemoveAt(ci); RebuildMethodRows(); };
                topRow.Children.Add(delM);
                mStack.Children.Add(topRow);

                var paramStack = new StackPanel { Margin = new Thickness(16, 2, 0, 0) };
                void RebuildParams()
                {
                    paramStack.Children.Clear();
                    for (int j = 0; j < m.Parameters.Count; j++)
                    {
                        var cj = j;
                        var p  = m.Parameters[j];
                        var prow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                        var pConv = SmallCombo(74);
                        foreach (var c in Enum.GetValues<PassingConvention>()) pConv.Items.Add(c);
                        pConv.SelectedItem = p.Convention;
                        pConv.SelectionChanged += (_, _) => { if (pConv.SelectedItem is PassingConvention c) m.Parameters[cj].Convention = c; };
                        prow.Children.Add(pConv);
                        var pName = SmallBox(90, p.Name);
                        pName.TextChanged += (_, _) => m.Parameters[cj].Name = pName.Text;
                        prow.Children.Add(pName);
                        var pType = SmallBox(80, p.DataType);
                        pType.TextChanged += (_, _) => m.Parameters[cj].DataType = pType.Text;
                        prow.Children.Add(pType);
                        var delP = Btn("✕");
                        delP.Click += (_, _) => { m.Parameters.RemoveAt(cj); RebuildParams(); };
                        prow.Children.Add(delP);
                        paramStack.Children.Add(prow);
                    }
                    var addP = Btn(Properties.Loc.S("CodeEdit_AddParam"));
                    addP.Click += (_, _) => { m.Parameters.Add(new CodeParam()); RebuildParams(); };
                    paramStack.Children.Add(addP);
                }
                RebuildParams();
                mStack.Children.Add(paramStack);

                methodStack.Children.Add(box);
            }
        }
        RebuildMethodRows();
        addMethodBtn.Click += (_, _) => { workMethods.Add(new CodeMethod()); RebuildMethodRows(); };

        // Enum values editor (collapsible)
        var workEnum = new List<string>(entity.EnumValues);
        var enumSection = CollapsibleSection(Properties.Loc.S("CodeEdit_EnumValues"), startCollapsed: workEnum.Count > 0,
            out var addEnumBtn, out var enumStack, out var enumCount);
        form.Children.Add(enumSection);

        void RebuildEnumRows()
        {
            enumCount.Text = $"({workEnum.Count})";
            enumStack.Children.Clear();
            for (int i = 0; i < workEnum.Count; i++)
            {
                var ci  = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                var nm  = SmallBox(160, workEnum[i]);
                nm.TextChanged += (_, _) => workEnum[ci] = nm.Text;
                row.Children.Add(nm);
                var del = Btn("✕");
                del.Click += (_, _) => { workEnum.RemoveAt(ci); RebuildEnumRows(); };
                row.Children.Add(del);
                enumStack.Children.Add(row);
            }
        }
        RebuildEnumRows();
        addEnumBtn.Click += (_, _) => { workEnum.Add(Properties.Loc.S("CodeEdit_DefaultValue")); RebuildEnumRows(); };

        // Data ports editor (collapsible)
        var workPorts = entity.Ports.Select(p => new CodePort
        { Id = p.Id, Name = p.Name, DataType = p.DataType, Direction = p.Direction, Convention = p.Convention }).ToList();

        var portsSection = CollapsibleSection(Properties.Loc.S("CodeEdit_DataPorts"), startCollapsed: workPorts.Count > 0,
            out var addPortBtn, out var portStack, out var portCount);
        form.Children.Add(portsSection);

        void RebuildPortRows()
        {
            portCount.Text = $"({workPorts.Count})";
            portStack.Children.Clear();
            for (int i = 0; i < workPorts.Count; i++)
            {
                var ci   = i;
                var port = workPorts[i];
                var row  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                var dirCombo = SmallCombo(70);
                foreach (var d in Enum.GetValues<PortDirection>()) dirCombo.Items.Add(d);
                dirCombo.SelectedItem = port.Direction;
                dirCombo.SelectionChanged += (_, _) => { if (dirCombo.SelectedItem is PortDirection d) workPorts[ci].Direction = d; };
                row.Children.Add(dirCombo);

                var nm = SmallBox(100, port.Name);
                nm.TextChanged += (_, _) => workPorts[ci].Name = nm.Text;
                row.Children.Add(nm);

                var ty = SmallBox(80, port.DataType);
                ty.TextChanged += (_, _) => workPorts[ci].DataType = ty.Text;
                row.Children.Add(ty);

                var convCombo = SmallCombo(74);
                foreach (var c in Enum.GetValues<PassingConvention>()) convCombo.Items.Add(c);
                convCombo.SelectedItem = port.Convention;
                convCombo.SelectionChanged += (_, _) => { if (convCombo.SelectedItem is PassingConvention c) workPorts[ci].Convention = c; };
                row.Children.Add(convCombo);

                var del = Btn("✕");
                del.Click += (_, _) => { workPorts.RemoveAt(ci); RebuildPortRows(); };
                row.Children.Add(del);

                portStack.Children.Add(row);
            }
        }
        RebuildPortRows();
        addPortBtn.Click += (_, _) => { workPorts.Add(new CodePort()); RebuildPortRows(); };

        // Section visibility per type
        void UpdateSections()
        {
            var t = typeCombo.SelectedItem is CodeEntityType ct ? ct : entity.EntityType;
            bool isClassish = t is CodeEntityType.Class or CodeEntityType.Struct;
            inheritSection.Visibility  = isClassish ? Visibility.Visible : Visibility.Collapsed;
            instanceSection.Visibility = t == CodeEntityType.Object ? Visibility.Visible : Visibility.Collapsed;
            fieldsSection.Visibility   = isClassish ? Visibility.Visible : Visibility.Collapsed;
            methodsSection.Visibility  = (isClassish || t == CodeEntityType.Interface) ? Visibility.Visible : Visibility.Collapsed;
            enumSection.Visibility     = t == CodeEntityType.Enum ? Visibility.Visible : Visibility.Collapsed;
        }
        typeCombo.SelectionChanged += (_, _) => UpdateSections();
        UpdateSections();

        // Save / Cancel
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(bottomRow, 1); root.Children.Add(bottomRow);

        var saveBtn = Btn(Properties.Loc.S("Common_Save"));
        saveBtn.Click += (_, _) =>
        {
            OldType = entity.EntityType;

            entity.Name        = nameBox.Text.Trim();
            entity.EntityType  = typeCombo.SelectedItem is CodeEntityType et ? et : entity.EntityType;
            entity.Namespace   = nsBox.Text.Trim();
            entity.Description = descBox.Text.Trim();
            entity.BaseClassId = (baseCombo.SelectedItem as ComboItem)?.Id ?? "";
            entity.ImplementsIds = implChecks.Where(c => c.Cb.IsChecked == true).Select(c => c.Id).ToList();
            entity.InstanceOfId  = (instCombo.SelectedItem as ComboItem)?.Id ?? "";
            entity.Fields      = workFields;
            entity.Methods     = workMethods;
            entity.EnumValues  = workEnum.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            entity.Ports       = workPorts;

            if (OldType != entity.EntityType)
                CodeEntityService.Delete(_projFolder, OldType.ToString(), entity.Id);
            CodeEntityService.Save(_projFolder, entity.EntityType.ToString(), entity);

            Saved = true;
            DialogResult = true;
        };
        bottomRow.Children.Add(saveBtn);

        var cancelBtn = Btn(Properties.Loc.S("Common_Cancel"));
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        cancelBtn.Click += (_, _) => DialogResult = false;
        bottomRow.Children.Add(cancelBtn);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private sealed record ComboItem(string Name, string Id)
    {
        public override string ToString() => Name;
    }

    private List<CodeEntity> AllEntitiesOfTypes(params CodeEntityType[] types)
    {
        var set = types.ToHashSet();
        return _known.Values.Where(e => set.Contains(e.EntityType)).OrderBy(e => e.Name).ToList();
    }

    private TextBlock FieldLabel(string text)
    {
        var lbl = new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 2), FontSize = 12 };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        return lbl;
    }

    /// <summary>
    /// A collapsible editor section with a divider, a clickable header (arrow + title +
    /// item count), an "+ Add" button, and a toggleable content panel.
    /// </summary>
    private StackPanel CollapsibleSection(string title, bool startCollapsed,
        out Button addBtn, out StackPanel content, out TextBlock countLabel)
    {
        var section = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // optical divider between sections
        var divider = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        divider.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        section.Children.Add(divider);

        var hdr = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 4),
            Cursor      = System.Windows.Input.Cursors.Hand,
            Background  = System.Windows.Media.Brushes.Transparent  // make whole row hit-testable
        };

        var arrow = new TextBlock { Text = startCollapsed ? "▸" : "▾", Width = 16, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        hdr.Children.Add(arrow);

        var lbl = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        hdr.Children.Add(lbl);

        countLabel = new TextBlock { Text = "", Opacity = 0.6, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        countLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        hdr.Children.Add(countLabel);

        addBtn = Btn(Properties.Loc.S("Common_AddPlus"));
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        hdr.Children.Add(addBtn);

        content = new StackPanel
        {
            Margin     = new Thickness(0, 0, 0, 8),
            Visibility = startCollapsed ? Visibility.Collapsed : Visibility.Visible
        };

        var capContent = content;
        var capArrow   = arrow;
        void Toggle(bool show)
        {
            capContent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            capArrow.Text = show ? "▾" : "▸";
        }
        hdr.MouseLeftButtonDown += (_, _) => Toggle(capContent.Visibility != Visibility.Visible);
        addBtn.Click += (_, _) => Toggle(true);   // adding always reveals the section

        section.Children.Add(hdr);
        section.Children.Add(content);
        return section;
    }

    private TextBox EditorTextBox(string value, bool multiLine = false)
    {
        var box = new TextBox
        {
            Text          = value,
            AcceptsReturn = multiLine,
            TextWrapping  = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height        = multiLine ? 56 : double.NaN,
            VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
        box.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        return box;
    }

    private ComboBox EditorCombo()
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        c.SetResourceReference(StyleProperty, "ModernComboBox");
        return c;
    }

    private ComboBox SmallCombo(double width)
    {
        var c = new ComboBox { Width = width, Margin = new Thickness(0, 0, 4, 0) };
        c.SetResourceReference(StyleProperty, "ModernComboBox");
        return c;
    }

    private TextBox SmallBox(double width, string value)
    {
        var b = new TextBox { Width = width, Text = value, Margin = new Thickness(0, 0, 4, 0) };
        b.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        b.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        b.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        return b;
    }

    private Button Btn(string label)
    {
        var b = new Button { Content = label, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
        b.SetResourceReference(StyleProperty,            "ModernButton");
        b.SetResourceReference(BackgroundProperty,       "ControlBgBrush");
        b.SetResourceReference(ForegroundProperty,       "SidebarTextBrush");
        return b;
    }
}
