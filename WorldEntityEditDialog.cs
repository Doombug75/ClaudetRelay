using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClaudetRelay.Services;
using Microsoft.Win32;

namespace ClaudetRelay;

/// <summary>
/// Themed, modal dialog for creating or editing any world entity.
/// Characters get a wider window with a portrait drag-drop zone and
/// a character-sheet-style header; other entity types use the compact layout.
/// </summary>
public class WorldEntityEditDialog : Window
{
    // ── Instance state (used by lambdas inside Build methods) ─────────────
    private string  _portraitFileName     = "";
    private string  _imageFileName        = "";   // location / lore attached image
    private string  _selectedFactionColor = "";
    private string? _themePath;

    // ── Static entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Shows the dialog modally. Returns true if the user saved.
    /// The entity is modified in place on confirm.
    /// </summary>
    public static bool Show(WorldEntity entity, string projFolder, bool isNew,
                            string? themePath, Window owner, ref bool editOpenFlag)
    {
        var dlg = new WorldEntityEditDialog(entity, projFolder, isNew, themePath, owner);
        editOpenFlag = true;
        var result = dlg.ShowDialog() == true;
        editOpenFlag = false;
        return result;
    }

    // ── Constructor ────────────────────────────────────────────────────────

    private WorldEntityEditDialog(WorldEntity entity, string projFolder,
                                  bool isNew, string? themePath, Window owner)
    {
        bool isCharacter = string.Equals(entity.EntityType, "Character",  StringComparison.OrdinalIgnoreCase);
        bool isFaction   = string.Equals(entity.EntityType, "Faction",    StringComparison.OrdinalIgnoreCase);
        bool isLocation  = string.Equals(entity.EntityType, "Location",   StringComparison.OrdinalIgnoreCase);
        bool isLore      = string.Equals(entity.EntityType, "Lore",       StringComparison.OrdinalIgnoreCase);

        // Initialise mutable state fields
        _portraitFileName     = entity.PortraitFileName;
        _imageFileName        = entity.ImageFileName;
        _selectedFactionColor = entity.FactionColor;
        _themePath            = themePath;

        Title                 = isNew ? $"New {entity.EntityType}" : $"Edit {entity.EntityType}";
        Width                 = isCharacter ? 660 : 520;
        Height                = isCharacter ? 820 : (isFaction ? 780 : (isLocation ? 680 : (isLore ? 720 : 620)));
        MinWidth              = 420;
        MinHeight             = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner                 = owner;
        ShowInTaskbar         = false;
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

        // ── Outer scroll ───────────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(isCharacter ? 22 : 24, isCharacter ? 18 : 20,
                                    isCharacter ? 22 : 24, 16)
        };
        Content = scroll;

        var root = new StackPanel();
        scroll.Content = root;

        var schema     = WorldEntitySchemas.For(entity.EntityType);
        var fieldBoxes = new Dictionary<string, TextBox>();

        // ── Character-sheet header ─────────────────────────────────────────
        TextBox nameBox;

        if (isCharacter)
        {
            // Header card: [name + type] | [portrait zone]
            var headerBorder = new Border
            {
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(16, 14, 16, 14),
                Margin          = new Thickness(0, 0, 0, 18)
            };
            headerBorder.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            headerBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            root.Children.Add(headerBorder);

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
            headerBorder.Child = headerGrid;

            // Left: big name field + type badge
            var nameSection = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            Grid.SetColumn(nameSection, 0);
            headerGrid.Children.Add(nameSection);

            var nameHint = new TextBlock { Text = "NAME", FontSize = 9, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(2, 0, 0, 4) };
            nameHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            nameSection.Children.Add(nameHint);

            var nameTbStyle = TryFindResource("ModernTextBox") as Style;
            nameBox = new TextBox
            {
                Text = entity.Name, FontSize = 18, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), Style = nameTbStyle,
                Padding = new Thickness(6, 4, 6, 4)
            };
            nameSection.Children.Add(nameBox);

            var typeBadge = new TextBlock
            {
                Text = entity.EntityType, FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(2, 6, 0, 0)
            };
            typeBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
            nameSection.Children.Add(typeBadge);

            // Right: portrait zone — pass a delegate so SetPortrait can read the live name field
            var portraitZone = BuildPortraitZone(projFolder, entity, () => nameBox.Text.Trim());
            Grid.SetColumn(portraitZone, 1);
            headerGrid.Children.Add(portraitZone);
        }
        else
        {
            // Standard compact name field
            var nameLbl = new TextBlock { Text = "Name", FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4) };
            nameLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(nameLbl);

            var nameTbStyle = TryFindResource("ModernTextBox") as Style;
            nameBox = new TextBox { Text = entity.Name, FontSize = 13, FontFamily = new FontFamily("Segoe UI"), Style = nameTbStyle };
            root.Children.Add(nameBox);
        }

        // ── Schema fields ──────────────────────────────────────────────────
        foreach (var (field, hint) in schema)
        {
            entity.Fields.TryGetValue(field, out var existing);
            fieldBoxes[field] = AddField(root, field, hint, existing ?? "", isMultiline: IsMultilineField(field));
        }

        // ── Location reference image ───────────────────────────────────────
        if (isLocation)
            BuildImageAttachmentZone(root, projFolder, entity, () => nameBox.Text.Trim());

        // ── Faction extras (color picker + member list + banner image) ────
        var workingMemberIds = entity.MemberIds.ToList();
        if (isFaction)
        {
            BuildFactionExtras(root, projFolder, entity, workingMemberIds);
            BuildImageAttachmentZone(root, projFolder, entity, () => nameBox.Text.Trim());
        }

        // ── Location: faction membership ──────────────────────────────────
        List<string>?       locationFactionIds = null;
        List<WorldEntity>?  allFactions        = null;
        if (isLocation)
        {
            allFactions        = WorldEntityService.List(projFolder, "Faction");
            locationFactionIds = allFactions
                .Where(f => f.MemberIds.Contains(entity.Id))
                .Select(f => f.Id)
                .ToList();
            BuildLocationFactionSection(root, projFolder, entity, allFactions, locationFactionIds);
        }

        // ── Lore: knowledge tags + factions + characters ───────────────────
        CheckBox? loreCommonCheck = null, loreHistoricalCheck = null;
        List<string>? loreFactionIds = null, loreMemberIds = null;
        if (isLore)
        {
            // Knowledge-type checkboxes
            var tagsLbl = new TextBlock { Text = "Knowledge type", FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 14, 0, 6) };
            tagsLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(tagsLbl);

            loreCommonCheck = new CheckBox
            {
                Content     = "Common knowledge  (everyone may have heard this)",
                IsChecked   = entity.Fields.TryGetValue("CommonKnowledge", out var ck) && ck == "true",
                FontSize    = 12, Margin = new Thickness(0, 0, 0, 6)
            };
            loreCommonCheck.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            root.Children.Add(loreCommonCheck);

            loreHistoricalCheck = new CheckBox
            {
                Content     = "Historical knowledge  (from the distant past)",
                IsChecked   = entity.Fields.TryGetValue("HistoricalKnowledge", out var hk) && hk == "true",
                FontSize    = 12, Margin = new Thickness(0, 0, 0, 4)
            };
            loreHistoricalCheck.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            root.Children.Add(loreHistoricalCheck);

            // Factions who share this lore
            loreFactionIds = entity.FactionIds.ToList();
            allFactions    = WorldEntityService.List(projFolder, "Faction");
            BuildLocationFactionSection(root, projFolder, entity, allFactions, loreFactionIds,
                heading: "Factions with this knowledge", addLabel: "＋ Add faction");

            // Characters who know this lore
            loreMemberIds = entity.MemberIds.ToList();
            BuildMemberSection(root, projFolder, "Characters who know this lore", "Character",
                loreMemberIds, "＋ Add character");
        }

        // ── Notes ──────────────────────────────────────────────────────────
        var notesBox = AddField(root, "Notes", "Freeform notes", entity.Notes, isMultiline: true);

        // ── Buttons ────────────────────────────────────────────────────────
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        root.Children.Add(btnRow);

        var cancelBtn = MakeBtn("Cancel", false); cancelBtn.Padding = new Thickness(16, 8, 16, 8);
        cancelBtn.Click += (_, _) => DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var saveBtn = MakeBtn(isNew ? "Create" : "Save", true);
        saveBtn.Padding = new Thickness(16, 8, 16, 8); saveBtn.Margin = new Thickness(8, 0, 0, 0);
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show("Name cannot be empty.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            entity.Name  = nameBox.Text.Trim();
            entity.Notes = notesBox.Text.Trim();
            entity.Fields.Clear();
            foreach (var (field, tb) in fieldBoxes)
                if (!string.IsNullOrWhiteSpace(tb.Text)) entity.Fields[field] = tb.Text.Trim();
            if (isFaction)   { entity.FactionColor = _selectedFactionColor; entity.MemberIds = workingMemberIds;
                               entity.ImageFileName = _imageFileName; }
            if (isCharacter)   entity.PortraitFileName = _portraitFileName;
            if (isLocation)
            {
                entity.ImageFileName = _imageFileName;
                if (allFactions is not null && locationFactionIds is not null)
                {
                    foreach (var fac in allFactions)
                    {
                        bool shouldBeMember = locationFactionIds.Contains(fac.Id);
                        bool isMember       = fac.MemberIds.Contains(entity.Id);
                        if (shouldBeMember && !isMember)
                            { fac.MemberIds.Add(entity.Id); WorldEntityService.Save(projFolder, fac); }
                        else if (!shouldBeMember && isMember)
                            { fac.MemberIds.Remove(entity.Id); WorldEntityService.Save(projFolder, fac); }
                    }
                }
            }
            if (isLore)
            {
                entity.MemberIds  = loreMemberIds  ?? [];
                entity.FactionIds = loreFactionIds ?? [];
                if (loreCommonCheck?.IsChecked == true)
                    entity.Fields["CommonKnowledge"] = "true";
                else
                    entity.Fields.Remove("CommonKnowledge");
                if (loreHistoricalCheck?.IsChecked == true)
                    entity.Fields["HistoricalKnowledge"] = "true";
                else
                    entity.Fields.Remove("HistoricalKnowledge");
            }
            DialogResult = true;
        };
        btnRow.Children.Add(saveBtn);

        nameBox.Focus();
        nameBox.SelectAll();
    }

    // ── Portrait zone ──────────────────────────────────────────────────────

    private Border BuildPortraitZone(string projFolder, WorldEntity entity, Func<string> getCurrentName)
    {

        // Outer border — the whole zone
        var zone = new Border
        {
            Width           = 136,
            Height          = 178,
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            Cursor          = Cursors.Hand,
            AllowDrop       = true,
            VerticalAlignment = VerticalAlignment.Top
        };
        zone.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");

        // Grid inside: placeholder OR image + remove button
        var inner = new Grid();
        zone.Child = inner;

        // Placeholder (shown when no portrait)
        var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var plIcon = new TextBlock { Text = "👤", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        var plText = new TextBlock { Text = "Drop portrait\nor click to browse", FontSize = 10, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 110 };
        plText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        placeholder.Children.Add(plIcon);
        placeholder.Children.Add(plText);
        inner.Children.Add(placeholder);

        // Image element (shown when portrait exists)
        var portraitImg = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };
        portraitImg.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        inner.Children.Add(portraitImg);

        // Overlay buttons (top-right corner, shown only when portrait is loaded)
        var btnOverlay = new StackPanel
        {
            Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0),
            Visibility = Visibility.Collapsed
        };
        inner.Children.Add(btnOverlay);

        Border MakeOverlayBtn(string icon, string tip)
        {
            var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(0, 0, 0, 3) };
            b.SetResourceReference(Border.BackgroundProperty, "AccentBgBrush");
            var t = new TextBlock { Text = icon, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            b.Child = t;
            return b;
        }

        var cropBtn   = MakeOverlayBtn("✂", "Crop portrait");
        var removeBtn = MakeOverlayBtn("×", "Remove portrait");
        btnOverlay.Children.Add(cropBtn);
        btnOverlay.Children.Add(removeBtn);

        // Helper: refresh the visual state
        void Refresh()
        {
            string fn    = _portraitFileName;
            bool hasImg  = !string.IsNullOrWhiteSpace(fn);
            string? fullPath = hasImg ? WorldEntityService.GetPortraitPath(projFolder, fn) : null;
            bool fileExists = fullPath is not null && File.Exists(fullPath);

            placeholder.Visibility = (!hasImg || !fileExists) ? Visibility.Visible : Visibility.Collapsed;
            portraitImg.Visibility = (hasImg && fileExists)   ? Visibility.Visible : Visibility.Collapsed;
            btnOverlay.Visibility  = (hasImg && fileExists)   ? Visibility.Visible : Visibility.Collapsed;
            zone.SetResourceReference(Border.BorderBrushProperty,
                (hasImg && fileExists) ? "AccentHighlightBrush" : "ControlBorderBrush");
            // Dashed style when empty
            if (!hasImg || !fileExists)
            {
                zone.BorderBrush = new DrawingBrush
                {
                    TileMode    = TileMode.Tile,
                    Viewport    = new Rect(0, 0, 10, 10),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Drawing     = new GeometryDrawing
                    {
                        Brush    = (Brush)(TryFindResource("ControlBorderBrush") ?? Brushes.Gray),
                        Geometry = new GeometryGroup
                        {
                            Children = { new RectangleGeometry(new Rect(0, 0, 5, 10)) }
                        }
                    }
                };
            }
            if (hasImg && fileExists)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource           = new Uri(fullPath!);
                    bmp.CacheOption         = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions       = BitmapCreateOptions.IgnoreImageCache;
                    bmp.DecodePixelWidth    = 280;
                    bmp.EndInit();
                    portraitImg.Source = bmp;
                }
                catch { portraitImg.Source = null; }
            }
            else { portraitImg.Source = null; }
        }

        // Helper: copy (or crop-then-save) a source image into the portraits folder
        void SetPortrait(string sourcePath, bool autoOfferCrop = true)
        {
            var ext       = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
            var validExts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff" };
            if (!validExts.Contains(ext))
            {
                MessageBox.Show("Unsupported image format.", "Portrait", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dir = WorldEntityService.GetPortraitsFolder(projFolder);
            Directory.CreateDirectory(dir);

            // ── Remove any existing portrait(s) for this entity (any extension) ──
            // Pattern: anything ending in _{entityId}.{ext}
            foreach (var old in Directory.GetFiles(dir, $"*_{entity.Id}.*")
                                         .Concat(Directory.GetFiles(dir, entity.Id + ".*")))
            {
                try { File.Delete(old); } catch { }
            }

            // Build a human-readable filename: SafeName_entityId.ext
            var currentName = getCurrentName();
            var safeName    = string.IsNullOrWhiteSpace(currentName)
                ? "portrait"
                : WorldEntityService.MakeSafeName(currentName);
            var fileName = $"{safeName}_{entity.Id}{ext}";
            var destPath = WorldEntityService.GetPortraitPath(projFolder, fileName);

            try { File.Copy(sourcePath, destPath, overwrite: true); }
            catch (Exception ex) { MessageBox.Show($"Could not copy portrait:\n{ex.Message}", "Portrait", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            _portraitFileName = fileName;

            // ── Auto-offer crop when proportions are off ───────────────────
            if (autoOfferCrop)
            {
                try
                {
                    var info = new System.Windows.Media.Imaging.BitmapImage();
                    info.BeginInit();
                    info.UriSource = new Uri(destPath);
                    info.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    info.EndInit();
                    double ratio = (double)info.PixelWidth / info.PixelHeight;
                    bool wrongRatio = Math.Abs(ratio - 3.0 / 4.0) > 0.15;
                    bool tooBig     = info.PixelWidth > 1000 || info.PixelHeight > 1000;

                    if (wrongRatio || tooBig) OpenCropDialog(destPath);
                }
                catch { /* ignore dimension check errors */ }
            }

            Refresh();
        }

        // Helper: open crop dialog and overwrite the saved portrait if confirmed
        void OpenCropDialog(string currentPath)
        {
            var cropped = PortraitCropDialog.Show(currentPath, Application.Current.MainWindow ?? this, _themePath);
            if (cropped is null) return;   // user cancelled — keep original

            // Save cropped bitmap over the existing file (keep same extension)
            try { PortraitCropDialog.SaveBitmap(cropped, currentPath); }
            catch (Exception ex) { MessageBox.Show($"Could not save cropped portrait:\n{ex.Message}", "Portrait", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // Initial display
        Refresh();

        // Click → file picker
        zone.MouseLeftButtonDown += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Portrait Image",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true) SetPortrait(dlg.FileName);
        };

        // Drag over → highlight
        zone.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                zone.SetResourceReference(Border.BorderBrushProperty, "AccentHighlightBrush");
                zone.BorderThickness = new Thickness(2.5);
                e.Effects = DragDropEffects.Copy;
            }
            e.Handled = true;
        };
        zone.DragLeave += (_, _) =>
        {
            zone.BorderThickness = new Thickness(2);
            Refresh();
        };
        zone.Drop += (_, e) =>
        {
            zone.BorderThickness = new Thickness(2);
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0) SetPortrait(files[0]);
            }
            e.Handled = true;
        };

        // Crop button
        cropBtn.MouseLeftButtonDown += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(_portraitFileName)) return;
            var path = WorldEntityService.GetPortraitPath(projFolder, _portraitFileName);
            if (!File.Exists(path)) return;
            OpenCropDialog(path);
            Refresh();
            e.Handled = true;
        };

        // Remove button
        removeBtn.MouseLeftButtonDown += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(_portraitFileName))
            {
                var dir2 = WorldEntityService.GetPortraitsFolder(projFolder);
                foreach (var old in Directory.GetFiles(dir2, $"*_{entity.Id}.*")
                                             .Concat(Directory.GetFiles(dir2, entity.Id + ".*")))
                    try { File.Delete(old); } catch { }
                _portraitFileName = "";
            }
            Refresh();
            e.Handled = true;
        };

        return zone;
    }

    // ── Location reference image zone ─────────────────────────────────────

    private void BuildImageAttachmentZone(StackPanel root, string projFolder,
                                           WorldEntity entity, Func<string> getCurrentName)
    {
        var hdrLbl = new TextBlock
        {
            Text = "REFERENCE IMAGE", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 16, 0, 6)
        };
        hdrLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(hdrLbl);

        // Outer drop zone
        var zone = new Border
        {
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
            MinHeight = 80, AllowDrop = true, Cursor = Cursors.Hand
        };
        zone.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        root.Children.Add(zone);

        // Inner stack: placeholder OR thumbnail + action row
        var inner = new StackPanel();
        zone.Child = inner;

        // ── Placeholder ────────────────────────────────────────────────────
        var placeholder = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 20)
        };
        var plIcon = new TextBlock { Text = "🖼️", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        var plText = new TextBlock
        {
            Text = "Drop an image here, or click to browse",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
        };
        plText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        placeholder.Children.Add(plIcon);
        placeholder.Children.Add(plText);
        inner.Children.Add(placeholder);

        // ── Thumbnail ──────────────────────────────────────────────────────
        var thumbImg = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform, MaxHeight = 200, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 8, 8, 6), Visibility = Visibility.Collapsed
        };
        thumbImg.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        inner.Children.Add(thumbImg);

        // ── Action row below thumbnail ──────────────────────────────────────
        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed
        };
        inner.Children.Add(actionRow);

        var fileNameLabel = new TextBlock
        {
            FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis
        };
        fileNameLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        actionRow.Children.Add(fileNameLabel);

        var openBtn = MakeBtn("🔗 Open", false);
        openBtn.FontSize = 10; openBtn.Padding = new Thickness(8, 3, 8, 3);
        actionRow.Children.Add(openBtn);

        var openWithBtn = MakeBtn("Open with…", false);
        openWithBtn.FontSize = 10; openWithBtn.Padding = new Thickness(8, 3, 8, 3);
        openWithBtn.Margin = new Thickness(4, 0, 0, 0);
        actionRow.Children.Add(openWithBtn);

        var removeBtn = MakeBtn("✕", false);
        removeBtn.FontSize = 10; removeBtn.Padding = new Thickness(6, 3, 6, 3);
        removeBtn.Margin = new Thickness(6, 0, 0, 0);
        removeBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
        removeBtn.ToolTip = "Remove attached image";
        actionRow.Children.Add(removeBtn);

        // Also a right-click context menu on the thumbnail
        var ctx = new ContextMenu();
        var ctxOpen     = new MenuItem { Header = "🔗  Open" };
        var ctxOpenWith = new MenuItem { Header = "Open with…" };
        var ctxRemove   = new MenuItem { Header = "✕  Remove" };
        ctx.Items.Add(ctxOpen);
        ctx.Items.Add(ctxOpenWith);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(ctxRemove);
        thumbImg.ContextMenu = ctx;

        // ── Helpers ────────────────────────────────────────────────────────
        void Refresh()
        {
            bool hasFile = !string.IsNullOrWhiteSpace(_imageFileName);
            string? fullPath = hasFile ? WorldEntityService.GetImagePath(projFolder, _imageFileName) : null;
            bool exists = fullPath is not null && File.Exists(fullPath);

            placeholder.Visibility = (!hasFile || !exists) ? Visibility.Visible  : Visibility.Collapsed;
            thumbImg.Visibility    = (hasFile && exists)   ? Visibility.Visible  : Visibility.Collapsed;
            actionRow.Visibility   = (hasFile && exists)   ? Visibility.Visible  : Visibility.Collapsed;
            zone.SetResourceReference(Border.BorderBrushProperty,
                (hasFile && exists) ? "AccentHighlightBrush" : "ControlBorderBrush");

            if (hasFile && exists)
            {
                fileNameLabel.Text = _imageFileName;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource        = new Uri(fullPath!);
                    bmp.CacheOption      = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions    = BitmapCreateOptions.IgnoreImageCache;
                    bmp.DecodePixelHeight = 400;   // load a downscaled thumbnail into memory
                    bmp.EndInit();
                    thumbImg.Source = bmp;
                }
                catch { thumbImg.Source = null; }
            }
            else { thumbImg.Source = null; }
        }

        void AttachImage(string sourcePath)
        {
            var ext       = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
            var validExts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".svg" };
            if (!validExts.Contains(ext))
            {
                MessageBox.Show("Unsupported image format.", "Attach Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dir = WorldEntityService.GetImagesFolder(projFolder);
            Directory.CreateDirectory(dir);

            // Remove any existing attached image for this entity
            foreach (var old in Directory.GetFiles(dir, $"*_{entity.Id}.*")
                                         .Concat(Directory.GetFiles(dir, entity.Id + ".*")))
                try { File.Delete(old); } catch { }

            var currentName = getCurrentName();
            var safeName    = string.IsNullOrWhiteSpace(currentName)
                ? "image"
                : WorldEntityService.MakeSafeName(currentName);
            var fileName = $"{safeName}_{entity.Id}{ext}";
            var destPath = WorldEntityService.GetImagePath(projFolder, fileName);

            try { File.Copy(sourcePath, destPath, overwrite: true); }
            catch (Exception ex) { MessageBox.Show($"Could not copy image:\n{ex.Message}", "Attach Image", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            _imageFileName = fileName;
            Refresh();
        }

        void OpenImage()
        {
            if (string.IsNullOrWhiteSpace(_imageFileName)) return;
            var path = WorldEntityService.GetImagePath(projFolder, _imageFileName);
            if (!File.Exists(path)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"Could not open image:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void OpenWith()
        {
            if (string.IsNullOrWhiteSpace(_imageFileName)) return;
            var path = WorldEntityService.GetImagePath(projFolder, _imageFileName);
            if (!File.Exists(path)) return;
            try
            {
                var openWithExe = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "openwith.exe");
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(openWithExe, $"\"{path}\"")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show($"Could not open 'Open with' dialog:\n{ex.Message}", "Open with", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // Wire events
        zone.MouseLeftButtonDown += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(_imageFileName))
            {
                // Empty: click to browse
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Attach Reference Image",
                    Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tiff;*.svg|All files|*.*"
                };
                if (dlg.ShowDialog() == true) AttachImage(dlg.FileName);
                e.Handled = true;
            }
            else
            {
                // Has image: single-click opens it
                OpenImage();
                e.Handled = true;
            }
        };

        zone.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                zone.SetResourceReference(Border.BorderBrushProperty, "AccentHighlightBrush");
                zone.BorderThickness = new Thickness(2.5);
                e.Effects = DragDropEffects.Copy;
            }
            e.Handled = true;
        };
        zone.DragLeave += (_, _) => { zone.BorderThickness = new Thickness(2); Refresh(); };
        zone.Drop += (_, e) =>
        {
            zone.BorderThickness = new Thickness(2);
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0) AttachImage(files[0]);
            }
            e.Handled = true;
        };

        openBtn.Click       += (_, _) => OpenImage();
        ctxOpen.Click       += (_, _) => OpenImage();
        openWithBtn.Click   += (_, _) => OpenWith();
        ctxOpenWith.Click   += (_, _) => OpenWith();
        removeBtn.Click     += (_, _) =>
        {
            var dir2 = WorldEntityService.GetImagesFolder(projFolder);
            foreach (var old in Directory.GetFiles(dir2, $"*_{entity.Id}.*")
                                         .Concat(Directory.GetFiles(dir2, entity.Id + ".*")))
                try { File.Delete(old); } catch { }
            _imageFileName = "";
            Refresh();
        };
        ctxRemove.Click += (_, _) => removeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

        Refresh();
    }

    // ── Faction extras ─────────────────────────────────────────────────────

    private void BuildFactionExtras(StackPanel root, string projFolder, WorldEntity entity,
        List<string> workingMemberIds)
    {

        // ── Colour picker ──────────────────────────────────────────────────
        var colorLbl = new TextBlock { Text = "Faction Colour", FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 14, 0, 6) };
        colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(colorLbl);

        var colorPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(colorPanel);
        var swatches = new List<Border>();

        void UpdateSwatchSel()
        {
            foreach (var sw in swatches)
            {
                bool sel = (string)sw.Tag == _selectedFactionColor;
                sw.BorderThickness = new Thickness(sel ? 3 : 0);
                sw.BorderBrush     = sel ? new SolidColorBrush(Colors.White) : Brushes.Transparent;
                sw.Width  = sel ? 30 : 28;
                sw.Height = sel ? 30 : 28;
            }
        }
        foreach (var hex in WorldEntitySchemas.FactionColorPalette)
        {
            var capturedHex = hex;
            Color col; try { col = (Color)ColorConverter.ConvertFromString(hex)!; } catch { col = Colors.Gray; }
            var sw = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(col), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Tag = hex };
            sw.MouseLeftButtonDown += (_, _) => { _selectedFactionColor = capturedHex; UpdateSwatchSel(); };
            swatches.Add(sw);
            colorPanel.Children.Add(sw);
        }
        UpdateSwatchSel();

        // ── Character Members ──────────────────────────────────────────────
        BuildMemberSection(root, projFolder, "Character Members", "Character",
            workingMemberIds, addLabel: "＋ Add character");

        // ── Location Members ───────────────────────────────────────────────
        BuildMemberSection(root, projFolder, "Location Members", "Location",
            workingMemberIds, addLabel: "＋ Add location");
    }

    /// <summary>Builds a chip-panel section for one entity type's members within a faction.</summary>
    private void BuildMemberSection(StackPanel root, string projFolder, string heading,
        string entityType, List<string> workingMemberIds, string addLabel)
    {
        var allOfType = WorldEntityService.List(projFolder, entityType);

        var lbl = new TextBlock { Text = heading, FontSize = 12, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 14, 0, 6) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(lbl);

        var chipsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(chipsPanel);

        void RefreshChips()
        {
            chipsPanel.Children.Clear();
            var byId = allOfType.ToDictionary(e => e.Id);
            foreach (var membId in workingMemberIds.ToList())
            {
                if (!byId.TryGetValue(membId, out var ent)) continue;
                var capturedId = membId;
                var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 4, 6, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
                chip.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
                chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                var row  = new StackPanel { Orientation = Orientation.Horizontal };
                var name = new TextBlock { Text = ent.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                name.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
                var rmv  = new TextBlock { Text = " ✕", FontSize = 10, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                rmv.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                rmv.MouseLeftButtonDown += (_, _) => { workingMemberIds.Remove(capturedId); RefreshChips(); };
                row.Children.Add(name); row.Children.Add(rmv);
                chip.Child = row;
                chipsPanel.Children.Add(chip);
            }
            if (allOfType.Any(e => !workingMemberIds.Contains(e.Id)))
            {
                var addChip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
                addChip.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
                addChip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                var addTb = new TextBlock { Text = addLabel, FontSize = 11 };
                addTb.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
                addChip.Child = addTb;
                addChip.MouseLeftButtonDown += (_, _) =>
                {
                    var picked = ShowEntityPicker(allOfType, workingMemberIds, $"Add {entityType}");
                    if (picked is not null && !workingMemberIds.Contains(picked.Id))
                        { workingMemberIds.Add(picked.Id); RefreshChips(); }
                };
                chipsPanel.Children.Add(addChip);
            }
        }
        RefreshChips();
    }

    /// <summary>Builds a faction-chip section. Used by Location (membership) and Lore (knowledge factions).</summary>
    private void BuildLocationFactionSection(StackPanel root, string projFolder, WorldEntity entity,
        List<WorldEntity> allFactions, List<string> locationFactionIds,
        string heading = "Faction Membership", string addLabel = "＋ Add to faction")
    {
        if (allFactions.Count == 0) return;

        var lbl = new TextBlock { Text = heading, FontSize = 12, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 14, 0, 6) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(lbl);

        var chipsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(chipsPanel);

        void RefreshChips()
        {
            chipsPanel.Children.Clear();
            var facById = allFactions.ToDictionary(f => f.Id);
            foreach (var fId in locationFactionIds.ToList())
            {
                if (!facById.TryGetValue(fId, out var fac)) continue;
                var capturedId = fId;
                // Colored chip: show faction color as a small dot
                Color? dotColor = null;
                if (!string.IsNullOrEmpty(fac.FactionColor))
                    try { dotColor = (Color)ColorConverter.ConvertFromString(fac.FactionColor)!; } catch { }

                var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
                chip.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
                chip.BorderBrush = dotColor.HasValue
                    ? new SolidColorBrush(Color.FromArgb(140, dotColor.Value.R, dotColor.Value.G, dotColor.Value.B))
                    : (Brush)(TryFindResource("ControlBorderBrush") ?? Brushes.Gray);
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (dotColor.HasValue)
                {
                    var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(dotColor.Value),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
                    row.Children.Add(dot);
                }
                var name = new TextBlock { Text = fac.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                name.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
                var rmv  = new TextBlock { Text = " ✕", FontSize = 10, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                rmv.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                rmv.MouseLeftButtonDown += (_, _) => { locationFactionIds.Remove(capturedId); RefreshChips(); };
                row.Children.Add(name); row.Children.Add(rmv);
                chip.Child = row;
                chipsPanel.Children.Add(chip);
            }
            if (allFactions.Any(f => !locationFactionIds.Contains(f.Id)))
            {
                var addChip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
                addChip.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
                addChip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                var addTb = new TextBlock { Text = addLabel, FontSize = 11 };
                addTb.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
                addChip.Child = addTb;
                addChip.MouseLeftButtonDown += (_, _) =>
                {
                    var picked = ShowEntityPicker(allFactions, locationFactionIds, "Assign Faction");
                    if (picked is not null && !locationFactionIds.Contains(picked.Id))
                        { locationFactionIds.Add(picked.Id); RefreshChips(); }
                };
                chipsPanel.Children.Add(addChip);
            }
        }
        RefreshChips();
    }

    // ── Generic entity picker ──────────────────────────────────────────────

    private WorldEntity? ShowEntityPicker(List<WorldEntity> entities, List<string> excludeIds, string title)
    {
        var eligible = entities.Where(e => !excludeIds.Contains(e.Id)).ToList();
        if (eligible.Count == 0) { MessageBox.Show("All entries are already assigned.", "Nothing to add", MessageBoxButton.OK, MessageBoxImage.Information); return null; }

        WorldEntity? result = null;
        var win = new Window { Title = title, Width = 320, Height = Math.Min(480, 80 + eligible.Count * 42), MinHeight = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize };
        // Inherit theme
        foreach (var rd in Resources.MergedDictionaries) win.Resources.MergedDictionaries.Add(rd);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");
        win.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(win);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        win.Content = scroll;
        var list = new StackPanel { Margin = new Thickness(12) };
        scroll.Content = list;
        foreach (var ch in eligible)
        {
            var cap = ch;
            var item = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            item.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            item.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var nb = new TextBlock { Text = ch.Name, FontSize = 13 };
            nb.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
            item.Child = nb;
            item.MouseLeftButtonDown += (_, _) => { result = cap; win.DialogResult = true; };
            list.Children.Add(item);
        }
        win.ShowDialog();
        return result;
    }

    // ── Field builder helpers ──────────────────────────────────────────────

    private TextBox AddField(StackPanel root, string label, string hint, string value, bool isMultiline)
    {
        var lbl = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 12, 0, 4) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(lbl);

        var style = TryFindResource("ModernTextBox") as Style;
        var tb = new TextBox
        {
            Text      = value, FontSize = 13, FontFamily = new FontFamily("Segoe UI"), Style = style,
            TextWrapping = isMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = isMultiline,
            MinHeight = isMultiline ? 72 : 0,
            MaxHeight = isMultiline ? 150 : double.PositiveInfinity,
            VerticalScrollBarVisibility = isMultiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
        if (!string.IsNullOrEmpty(hint)) tb.ToolTip = hint;
        root.Children.Add(tb);
        return tb;
    }

    private Button MakeBtn(string label, bool isPrimary)
    {
        var btn = new Button { Content = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"), Padding = new Thickness(16, 7, 16, 7), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        btn.SetResourceReference(Button.BackgroundProperty,  isPrimary ? "ControlHoverBrush"    : "ControlBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty,  isPrimary ? "AccentHighlightBrush" : "SidebarTextBrush");
        btn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        btn.MouseEnter += (_, _) => btn.Opacity = 0.80;
        btn.MouseLeave += (_, _) => btn.Opacity = 1.00;
        return btn;
    }

    private static bool IsMultilineField(string field) =>
        field is "Background" or "Description" or "Atmosphere" or "Significance" or "Notes"
               or "Goal" or "Arc" or "Voice";
}
