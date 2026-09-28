using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CS2AccountHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CS2AccountHub.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ApiClient _api = new();
    private AccountSection _section = AccountSection.PlayReady;
    private AccountPrimeBucket _prime = AccountPrimeBucket.Prime;
    private AccountSummaryDto? _selected;
    private readonly Dictionary<uint, uint> _loadoutT = new();
    private readonly Dictionary<uint, uint> _loadoutCt = new();
    private bool _loadoutIsCt;
    private bool _loadingLoadout;
    private string? _avatarBase64;

    private static readonly uint[] StarterSlots = [2];
    private static readonly uint[] OtherPistolSlots = [3, 4, 5, 6];
    private static readonly uint[] MidSlots = [8, 9, 10, 11, 12];
    private static readonly uint[] RifleSlots = [14, 15, 16, 17, 18];
    private static readonly IReadOnlyDictionary<uint, string> WeaponNames = new Dictionary<uint, string>
    {
        [1] = "Desert Eagle", [2] = "Dual Berettas", [3] = "Five-SeveN", [4] = "Glock-18",
        [7] = "AK-47", [8] = "AUG", [9] = "AWP", [10] = "FAMAS", [11] = "G3SG1",
        [13] = "Galil AR", [14] = "M249", [16] = "M4A1", [17] = "MAC-10", [19] = "P90",
        [23] = "MP5-SD", [24] = "UMP-45", [25] = "XM1014", [26] = "PP-Bizon", [27] = "MAG-7",
        [28] = "Negev", [29] = "Sawed-Off", [30] = "Tec-9", [32] = "P2000", [33] = "MP7",
        [34] = "MP9", [35] = "Nova", [36] = "P250", [38] = "SCAR-20", [39] = "SG 553",
        [40] = "SSG 08", [60] = "M4A1-S", [61] = "USP-S", [63] = "CZ75-Auto", [64] = "R8 Revolver",
    };

    public ObservableCollection<AccountSummaryDto> Accounts { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Nav.SelectedItem = Nav.MenuItems[0];
        _ = LoadAccountsAsync();
    }

    private async Task LoadAccountsAsync()
    {
        try
        {
            var accounts = await _api.GetAccountsAsync(_section, _prime);
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(account);
            AccountsList.ItemsSource = Accounts;
            StatusText.Text = $"{Accounts.Count} accounts loaded.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        AccountsView.Visibility = Visibility.Collapsed;
        LoadoutView.Visibility = Visibility.Collapsed;
        PersonalizeView.Visibility = Visibility.Collapsed;
        switch (item.Tag?.ToString())
        {
            case "PlayReady": _section = AccountSection.PlayReady; PageTitle.Text = "Play Ready"; AccountsView.Visibility = Visibility.Visible; break;
            case "NotPlayReady": _section = AccountSection.NotPlayReady; PageTitle.Text = "Not Play Ready"; AccountsView.Visibility = Visibility.Visible; break;
            case "Loadout": PageTitle.Text = "Loadout"; LoadoutView.Visibility = Visibility.Visible; _ = LoadLoadoutAsync(); return;
            case "Personalize": PageTitle.Text = "Personalize"; PersonalizeView.Visibility = Visibility.Visible; _ = LoadPersonalizeAsync(); return;
        }
        _ = LoadAccountsAsync();
    }

    private void PrimeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrimeFilter.SelectedItem is ComboBoxItem item && Enum.TryParse<AccountPrimeBucket>(item.Tag?.ToString(), out var prime))
        {
            _prime = prime;
            _ = LoadAccountsAsync();
        }
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = AccountsList.SelectedItem as AccountSummaryDto;
        if (_selected is null) return;
        DetailsName.Text = $"{_selected.PersonaName ?? "Unknown"} · {_selected.SteamId}";
        DetailsStatus.Text = $"Premier: {_selected.PremierRating?.ToString() ?? "—"}\nCS2 Level: {_selected.Cs2Level?.ToString() ?? "—"}\nCooldown: {_selected.CooldownSeconds?.ToString() ?? "None"}\nVAC: {_selected.VacBanned?.ToString() ?? "Unknown"}";
        NoteBox.Text = _selected.Note ?? string.Empty;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAccountsAsync();

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var form = new StackPanel { Spacing = 10 };
        var accountName = new TextBox { PlaceholderText = "Steam account name" };
        var password = new PasswordBox { PlaceholderText = "Steam password" };
        form.Children.Add(accountName);
        form.Children.Add(password);

        var dialog = new ContentDialog
        {
            Title = "Add Steam account",
            Content = form,
            PrimaryButtonText = "Sign in",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var auth = await _api.StartAuthAsync(accountName.Text, password.Password);
            if (auth is null) return;

            if (auth.State is AuthState.AwaitingSteamGuard or AuthState.AwaitingConfirmation)
            {
                var codeBox = new TextBox { PlaceholderText = "Steam Guard or email code" };
                var codeDialog = new ContentDialog
                {
                    Title = auth.PromptMessage ?? "Steam Guard required",
                    Content = codeBox,
                    PrimaryButtonText = "Submit code",
                    CloseButtonText = "Cancel",
                    XamlRoot = Content.XamlRoot,
                };
                if (await codeDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await _api.SubmitGuardCodeAsync(auth.SessionId, codeBox.Text);
                }
            }

            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(1000);
                auth = await _api.GetAuthSessionAsync(auth!.SessionId);
                if (auth?.State is AuthState.Completed or AuthState.Failed) break;
            }

            StatusText.Text = auth?.PromptMessage ?? "Authentication finished.";
            await LoadAccountsAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void RefreshAccount_Click(object sender, RoutedEventArgs e) => await QueueAsync(AccountCommand.RefreshStatus);
    private async void ClearWorkshop_Click(object sender, RoutedEventArgs e) => await QueueAsync(AccountCommand.ClearWorkshop);
    private async void ApplyLoadout_Click(object sender, RoutedEventArgs e)
    {
        var preset = await _api.GetLoadoutAsync();
        await QueueAsync(AccountCommand.ApplyLoadout, preset);
    }

    private async void Personalize_Click(object sender, RoutedEventArgs e)
    {
        var preset = await _api.GetPersonalizeAsync();
        await QueueAsync(AccountCommand.Personalize, preset);
    }
    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var operation = await _api.QueueCommandAsync(_selected.Id, AccountCommand.SignInToSteam);
            if (operation is null) return;

            for (var attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(500);
                operation = await _api.GetOperationAsync(operation!.Id);
                if (operation?.State is OperationState.Succeeded or OperationState.Failed) break;
            }

            if (operation?.State != OperationState.Succeeded)
            {
                StatusText.Text = operation?.Message ?? "Steam sign-in handoff failed.";
                return;
            }

            var handoff = await _api.ClaimHandoffAsync(operation.Id);
            if (handoff is null) throw new InvalidOperationException("The one-time Steam handoff was unavailable.");

            var handoffPath = Path.Combine(Path.GetTempPath(), $"cs2-account-hub-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(handoffPath, JsonSerializer.Serialize(handoff));
            var bridgePath = Environment.GetEnvironmentVariable("CS2_HUB_BRIDGE_PATH") ?? "CS2AccountHub.WindowsBridge.exe";
            var startInfo = new ProcessStartInfo { FileName = bridgePath, UseShellExecute = false };
            startInfo.ArgumentList.Add(handoffPath);
            Process.Start(startInfo);
            StatusText.Text = "Steam sign-in handoff sent to the Windows bridge.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            await _api.SaveNoteAsync(_selected.Id, NoteBox.Text);
            StatusText.Text = "Note saved.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async Task LoadLoadoutAsync()
    {
        var preset = await _api.GetLoadoutAsync();
        if (preset is null) return;
        _loadoutT.Clear();
        _loadoutCt.Clear();
        foreach (var entry in preset.T) _loadoutT[entry.Key] = entry.Value;
        foreach (var entry in preset.Ct) _loadoutCt[entry.Key] = entry.Value;
        BuildLoadoutEditor();
    }

    private void LoadoutTeamSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LoadoutTeamSelector is not null)
        {
            _loadoutIsCt = LoadoutTeamSelector.SelectedIndex == 1;
            BuildLoadoutEditor();
        }
    }

    private void BuildLoadoutEditor()
    {
        if (LoadoutEditorHost is null) return;
        _loadingLoadout = true;
        LoadoutEditorHost.Children.Clear();
        LoadoutEditorHost.ColumnDefinitions.Clear();
        for (var index = 0; index < 3; index++)
        {
            LoadoutEditorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        AddLoadoutColumn(0, ("Pistols", new[] { ("Starting pistol", StarterSlots), ("Other pistols", OtherPistolSlots) }));
        AddLoadoutColumn(1, ("Mid-tier", new[] { ("SMG / shotgun / machine gun", MidSlots) }));
        AddLoadoutColumn(2, ("Rifles / snipers", new[] { ("Rifle slots", RifleSlots) }));
        _loadingLoadout = false;
    }

    private void AddLoadoutColumn(int column, params (string Title, (string Title, uint[] Slots)[] Groups)[] columns)
    {
        var host = new StackPanel { Spacing = 10 };
        Grid.SetColumn(host, column);
        LoadoutEditorHost.Children.Add(host);
        foreach (var columnGroup in columns)
        {
            host.Children.Add(new TextBlock { Text = columnGroup.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var group in columnGroup.Groups)
            {
                host.Children.Add(new TextBlock { Text = group.Title, Opacity = 0.65, Margin = new Thickness(0, 6, 0, 0) });
                foreach (var slot in group.Slots)
                {
                    var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = slot.ToString(), Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Tag = slot, DisplayMemberPath = "Name", SelectedValuePath = "Definition" };
                    foreach (var option in GetWeaponOptions(slot, _loadoutIsCt)) combo.Items.Add(option);
                    combo.SelectionChanged += LoadoutSlot_SelectionChanged;
                    var values = _loadoutIsCt ? _loadoutCt : _loadoutT;
                    if (values.TryGetValue(slot, out var selected)) combo.SelectedValue = selected;
                    Grid.SetColumn(combo, 1);
                    row.Children.Add(combo);
                    host.Children.Add(row);
                }
            }
        }
    }

    private void LoadoutSlot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLoadout || sender is not ComboBox combo || combo.Tag is not uint slot || combo.SelectedValue is not uint definition) return;
        (_loadoutIsCt ? _loadoutCt : _loadoutT)[slot] = definition;
    }

    private static IEnumerable<WeaponOption> GetWeaponOptions(uint slot, bool isCt)
    {
        IEnumerable<uint> definitions = slot switch
        {
            2 => isCt ? [32u, 61u] : [4u],
            >= 3 and <= 6 => [1u, 2u, 3u, 4u, 30u, 36u, 63u, 64u],
            >= 8 and <= 12 => [14u, 17u, 19u, 23u, 24u, 25u, 26u, 27u, 28u, 29u, 33u, 34u, 35u],
            >= 14 and <= 18 => [7u, 8u, 9u, 10u, 11u, 13u, 16u, 38u, 39u, 40u, 60u],
            _ => [],
        };

        foreach (var definition in definitions)
        {
            if (!WeaponNames.TryGetValue(definition, out var name)) continue;
            if (isCt && definition is 4 or 7 or 11 or 13 or 17 or 29 or 30 or 39) continue;
            if (!isCt && definition is 3 or 8 or 10 or 16 or 27 or 32 or 34 or 38 or 60 or 61) continue;
            yield return new WeaponOption(definition, name);
        }
    }

    private async Task LoadPersonalizeAsync()
    {
        var preset = await _api.GetPersonalizeAsync();
        if (preset is null) return;
        PersonaNameBox.Text = preset.PersonaName ?? string.Empty;
        RealNameBox.Text = preset.RealName ?? string.Empty;
        SummaryBox.Text = preset.Summary ?? string.Empty;
        ClearAliasBox.IsChecked = preset.ClearAliasHistory;
        _avatarBase64 = preset.AvatarBase64;
        AvatarStatus.Text = string.IsNullOrWhiteSpace(_avatarBase64) ? "No image selected" : "Saved avatar will be sent with the next update.";
    }

    private async void SaveLoadout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preset = new LoadoutPresetDto("Default", new Dictionary<uint, uint>(_loadoutT), new Dictionary<uint, uint>(_loadoutCt));
            await _api.SaveLoadoutAsync(preset);
            StatusText.Text = "Loadout preset saved on the server.";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void SavePersonalize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _api.SavePersonalizeAsync(new PersonalizePresetDto(PersonaNameBox.Text, RealNameBox.Text, SummaryBox.Text, _avatarBase64, ClearAliasBox.IsChecked == true));
            StatusText.Text = "Personalization preset saved on the server.";
        }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void PickAvatar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var bytes = await File.ReadAllBytesAsync(file.Path);
            _avatarBase64 = Convert.ToBase64String(bytes);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            AvatarPreview.Source = bitmap;
            AvatarStatus.Text = $"Selected: {file.Name}";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async Task QueueAsync(AccountCommand command, object? payload = null)
    {
        if (_selected is null) return;
        try
        {
            var operation = await _api.QueueCommandAsync(_selected.Id, command, payload);
            if (operation is not null)
            {
                var operationId = operation.Id;
                for (var attempt = 0; attempt < 120; attempt++)
                {
                    await Task.Delay(500);
                    operation = await _api.GetOperationAsync(operationId);
                    if (operation?.State is OperationState.Succeeded or OperationState.Failed) break;
                }
            }
            StatusText.Text = operation is null ? "Operation queued." : operation.Message ?? $"Operation {operation.State}.";
            await LoadAccountsAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private sealed record WeaponOption(uint Definition, string Name);
}
