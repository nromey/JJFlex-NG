using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for Menus (Menus.vb).
/// Radio menu navigation: bank selection, menu item list, value editing.
/// Supports menu types: OnOff, Enumerated, NumberRange, NumberRangeOff0, Text, SubMenus.
///
/// All menu operations use delegates — no direct AllRadios or RigControl references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class MenusDialog : JJFlexDialog
{
    private bool _realChange;

    #region Menu Item Types

    /// <summary>Menu value type.</summary>
    public enum MenuType
    {
        OnOff,
        Enumerated,
        NumberRange,
        NumberRangeOff0,
        Text
    }

    /// <summary>Describes a single menu item for display.</summary>
    public class MenuItemInfo
    {
        public string Display { get; set; } = "";
        public MenuType Type { get; set; }
        public object? Value { get; set; }
        public int Low { get; set; }
        public int High { get; set; }
        public string[]? EnumerantDescriptions { get; set; }
        public object[]? EnumerantValues { get; set; }
        public bool HasSubMenus { get; set; }
        public int SubMenuCount { get; set; }
    }

    #endregion

    #region Delegates

    /// <summary>Gets the number of menu banks. 0 = no bank selector needed.</summary>
    public Func<int>? GetMenuBankCount { get; set; }

    /// <summary>Gets/sets the current menu bank index.</summary>
    public Func<int>? GetMenuBank { get; set; }
    public Action<int>? SetMenuBank { get; set; }

    /// <summary>Gets the list of menu items for a given bank.</summary>
    public Func<int, List<MenuItemInfo>>? GetMenuItems { get; set; }

    /// <summary>
    /// Applies a menu value change.
    /// Parameters: bank index, menu item index, new value.
    /// </summary>
    public Action<int, int, object>? ApplyMenuChange { get; set; }

    /// <summary>
    /// Opens a submenu dialog for the given menu item.
    /// Parameters: bank index, menu item index.
    /// </summary>
    public Action<int, int>? OpenSubMenu { get; set; }

    /// <summary>Returns true if menus are ready.</summary>
    public Func<bool>? AreMenusReady { get; set; }

    #endregion

    private int _menuBank;
    private List<MenuItemInfo>?[]? _menuLists;

    public MenusDialog()
    {
        InitializeComponent();
        Loaded += MenusDialog_Loaded;
    }

    private void MenusDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (AreMenusReady?.Invoke() != true)
        {
            MessageBox.Show("The menus aren't setup yet.", "Menus",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        int bankCount = GetMenuBankCount?.Invoke() ?? 0;

        // Show/hide bank selector
        if (bankCount > 1)
        {
            BankPanel.Visibility = Visibility.Visible;
            for (int i = 0; i < bankCount; i++)
            {
                BankCombo.Items.Add(((char)('A' + i)).ToString());
            }
        }
        else
        {
            BankPanel.Visibility = Visibility.Collapsed;
        }

        int maxBanks = Math.Max(1, bankCount);
        _menuLists = new List<MenuItemInfo>?[maxBanks];
        _menuBank = GetMenuBank?.Invoke() ?? 0;

        LoadBank(_menuBank);

        if (bankCount > 1 && _menuBank < BankCombo.Items.Count)
            BankCombo.SelectedIndex = _menuBank;

        MenuListBox.Focus();
    }

    private void LoadBank(int bank)
    {
        _realChange = false;

        if (_menuLists != null && _menuLists[bank] == null)
        {
            _menuLists[bank] = GetMenuItems?.Invoke(bank) ?? new List<MenuItemInfo>();
        }

        MenuListBox.Items.Clear();
        var items = _menuLists?[bank];
        if (items != null)
        {
            foreach (var item in items)
            {
                MenuListBox.Items.Add(item.Display);
            }
        }

        if (MenuListBox.Items.Count > 0)
            MenuListBox.SelectedIndex = 0;

        _realChange = true;
    }

    private MenuItemInfo? GetSelectedMenuItem()
    {
        int idx = MenuListBox.SelectedIndex;
        if (idx < 0 || _menuLists == null || _menuLists[_menuBank] == null) return null;
        var list = _menuLists[_menuBank]!;
        if (idx >= list.Count) return null;
        return list[idx];
    }

    private void ShowMenuValue(MenuItemInfo item)
    {
        _realChange = false;
        MenuValueCombo.Visibility = Visibility.Collapsed;
        MenuValueText.Visibility = Visibility.Collapsed;

        if (item.HasSubMenus)
        {
            MenuValueText.Text = $"{item.SubMenuCount} submenus";
            MenuValueText.IsReadOnly = true;
            MenuValueText.Visibility = Visibility.Visible;
        }
        else
        {
            switch (item.Type)
            {
                case MenuType.Text:
                    MenuValueText.Text = item.Value?.ToString() ?? "";
                    MenuValueText.IsReadOnly = false;
                    MenuValueText.Visibility = Visibility.Visible;
                    break;

                case MenuType.OnOff:
                    MenuValueCombo.Items.Clear();
                    MenuValueCombo.Items.Add("Off");
                    MenuValueCombo.Items.Add("On");
                    int onOffVal = Convert.ToInt32(item.Value ?? 0);
                    MenuValueCombo.SelectedIndex = Math.Max(0, Math.Min(1, onOffVal));
                    MenuValueCombo.Visibility = Visibility.Visible;
                    break;

                case MenuType.Enumerated:
                    MenuValueCombo.Items.Clear();
                    MenuValueCombo.SelectedIndex = -1;
                    if (item.EnumerantDescriptions != null && item.EnumerantValues != null)
                    {
                        for (int i = 0; i < item.EnumerantDescriptions.Length; i++)
                        {
                            MenuValueCombo.Items.Add(item.EnumerantDescriptions[i]);
                            if (item.EnumerantValues[i]?.Equals(item.Value) == true)
                                MenuValueCombo.SelectedIndex = i;
                        }
                    }
                    MenuValueCombo.Visibility = Visibility.Visible;
                    break;

                case MenuType.NumberRange:
                    MenuValueCombo.Items.Clear();
                    for (int i = item.Low; i <= item.High; i++)
                        MenuValueCombo.Items.Add(i.ToString());
                    int nrVal = Convert.ToInt32(item.Value ?? item.Low);
                    MenuValueCombo.SelectedIndex = Math.Max(0, Math.Min(item.High - item.Low, nrVal - item.Low));
                    MenuValueCombo.Visibility = Visibility.Visible;
                    break;

                case MenuType.NumberRangeOff0:
                    MenuValueCombo.Items.Clear();
                    MenuValueCombo.Items.Add("Off");
                    for (int i = 1; i <= item.High; i++)
                        MenuValueCombo.Items.Add(i.ToString());
                    int nr0Val = Convert.ToInt32(item.Value ?? 0);
                    MenuValueCombo.SelectedIndex = Math.Max(0, Math.Min(item.High, nr0Val));
                    MenuValueCombo.Visibility = Visibility.Visible;
                    break;
            }
        }

        _realChange = true;
    }

    #region Event Handlers

    private void BankCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BankCombo.SelectedIndex < 0) return;
        int newBank = BankCombo.SelectedIndex;
        if (newBank == _menuBank) return;
        SetMenuBank?.Invoke(newBank);
        _menuBank = newBank;
        LoadBank(_menuBank);
    }

    private void MenuListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = GetSelectedMenuItem();
        if (item == null) return;
        ShowMenuValue(item);
    }

    private void MenuValueCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_realChange) return;
        int listIdx = MenuListBox.SelectedIndex;
        int valueIdx = MenuValueCombo.SelectedIndex;
        if (listIdx < 0 || valueIdx < 0) return;

        var item = GetSelectedMenuItem();
        if (item == null) return;

        object newValue;
        if (item.Type == MenuType.Enumerated && item.EnumerantValues != null && valueIdx < item.EnumerantValues.Length)
            newValue = item.EnumerantValues[valueIdx];
        else
            newValue = valueIdx + item.Low;

        ApplyMenuChange?.Invoke(_menuBank, listIdx, newValue);
    }

    private void MenuValueText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_realChange) return;
        int listIdx = MenuListBox.SelectedIndex;
        if (listIdx < 0) return;

        var item = GetSelectedMenuItem();
        if (item == null || item.HasSubMenus) return;
        if (item.Type == MenuType.Text)
        {
            ApplyMenuChange?.Invoke(_menuBank, listIdx, MenuValueText.Text);
        }
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        // Apply any pending text change
        if (MenuValueText.Visibility == Visibility.Visible && _realChange)
        {
            int listIdx = MenuListBox.SelectedIndex;
            var item = GetSelectedMenuItem();
            if (listIdx >= 0 && item?.Type == MenuType.Text)
            {
                ApplyMenuChange?.Invoke(_menuBank, listIdx, MenuValueText.Text);
            }
        }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion
}
