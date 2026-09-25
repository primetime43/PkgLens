using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private void InitializeOperationMenus()
    {
        this.FindControl<MenuItem>("FileMenu")!.ItemsSource = BuildOperationMenu(OperationMenu.File);
        this.FindControl<MenuItem>("EditMenu")!.ItemsSource = BuildOperationMenu(OperationMenu.Edit);
        this.FindControl<MenuItem>("ToolsMenu")!.ItemsSource = BuildOperationMenu(OperationMenu.Tools);
    }

    private IReadOnlyList<object> BuildOperationMenu(OperationMenu menu)
    {
        var result = new List<object>();
        var entries = OperationCatalog.MenuItems(menu).ToArray();
        int[] sections = entries.Select(entry => entry.Location.Section)
            .Concat(OperationCatalog.MenuGroups.Where(group => group.Menu == menu).Select(group => group.Section))
            .Distinct()
            .Order()
            .ToArray();

        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            if (sectionIndex > 0)
                result.Add(new Separator());

            int section = sections[sectionIndex];
            var sectionItems = new List<(int Order, object Item)>();
            sectionItems.AddRange(entries
                .Where(entry => entry.Location.Section == section && entry.Location.Group is null)
                .Select(entry => (entry.Location.Order, (object)CreateOperationMenuItem(entry.Definition))));

            foreach (OperationMenuGroup group in OperationCatalog.MenuGroups
                         .Where(group => group.Menu == menu && group.Section == section))
            {
                var children = entries
                    .Where(entry => entry.Location.Group == group.Id)
                    .OrderBy(entry => entry.Location.Order)
                    .Select(entry => (object)CreateOperationMenuItem(entry.Definition))
                    .ToArray();
                sectionItems.Add((group.Order, new MenuItem { Header = group.Header, ItemsSource = children }));
            }

            result.AddRange(sectionItems.OrderBy(item => item.Order).Select(item => item.Item));
        }

        return result;
    }

    private MenuItem CreateOperationMenuItem(OperationDefinition definition)
    {
        var item = new MenuItem
        {
            Header = definition.MenuHeader,
            Tag = definition.Id,
        };
        ToolTip.SetTip(item, definition.Tooltip);
        item.Click += OnCatalogOperationClick;

        if (OperationCatalog.MenuBindingPath(definition.MenuEligibility) is { } bindingPath)
            item.Bind(MenuItem.IsEnabledProperty, new Binding(bindingPath) { FallbackValue = false });

        return item;
    }

    private void OnCatalogOperationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: OperationId operation })
            return;

        switch (operation)
        {
            case OperationId.OpenPackage: OnOpenClick(sender, e); break;
            case OperationId.ClosePackage: OnCloseClick(sender, e); break;
            case OperationId.PackFolder: OnGoPackClick(sender, e); break;
            case OperationId.ComparePackages: OnComparePackagesClick(sender, e); break;
            case OperationId.ConvertCfw: OnConvertCfwClick(sender, e); break;
            case OperationId.ExportPs1Classic: OnExportPs1ClassicClick(sender, e); break;
            case OperationId.ExportPs2Classic: OnExportPs2ClassicClick(sender, e); break;
            case OperationId.ExportPsp: OnExportPspClick(sender, e); break;
            case OperationId.ExportVita: OnExportVitaClick(sender, e); break;
            case OperationId.BrowsePsarc: OnBrowsePsarcClick(sender, e); break;
            case OperationId.ExtractAll: OnExtractAllClick(sender, e); break;
            case OperationId.DecryptPackageContents: OnDecryptPackageContentsClick(sender, e); break;
            case OperationId.EdatTools: OnEdatToolsClick(sender, e); break;
            case OperationId.SavePackageAs: OnSaveAsClick(sender, e); break;
            case OperationId.Exit: OnExitClick(sender, e); break;
            case OperationId.EditSfo: OnEditSfoClick(sender, e); break;
            case OperationId.ReplaceSelectedFile: OnReplaceClick(sender, e); break;
            case OperationId.ExtractSelectedFile: OnExtractClick(sender, e); break;
            case OperationId.ViewSelectedFile: OnViewClick(sender, e); break;
            case OperationId.AnalyzeFirmware: OnFirmwareAnalysisClick(sender, e); break;
            case OperationId.AnalyzeFirmwareFolder: OnFirmwareFolderAnalysisClick(sender, e); break;
            case OperationId.PatchFirmware: OnGoFirmwarePatchClick(sender, e); break;
            case OperationId.BytePatch: OnGoBytePatchClick(sender, e); break;
            case OperationId.VerifyPackage: OnVerifyClick(sender, e); break;
            case OperationId.PackageInfo: OnInfoClick(sender, e); break;
            case OperationId.FolderInfo: OnFolderInfoClick(sender, e); break;
            case OperationId.KeyLicenseAudit: OnKeyLicenseAuditClick(sender, e); break;
            case OperationId.ScanLibrary: OnScanFolderClick(sender, e); break;
            case OperationId.BatchCenter: OnBatchCenterClick(sender, e); break;
            case OperationId.PackageOrganizer: OnPackageOrganizerClick(sender, e); break;
            case OperationId.ImportOverrideKey: OnSetKeyClick(sender, e); break;
            case OperationId.KeysFolder: OnKeysClick(sender, e); break;
            case OperationId.RapLibrary: OnManageRapsClick(sender, e); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Operation is not a menu command.");
        }
    }
}
