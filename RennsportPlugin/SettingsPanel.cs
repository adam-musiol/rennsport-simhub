using System;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimHub.Plugins.Styles;

namespace RennsportPlugin
{
    /// <summary>
    /// Settings editor on the plugin's page. Edits a copy of the settings; "Save and apply" validates it,
    /// saves it and restarts the plugin's client and bridges (no SimHub restart needed).
    /// </summary>
    public sealed class SettingsPanel : StackPanel
    {
        private readonly RennsportPlugin _plugin;
        private readonly TextBox _host = Input(), _port = Input(), _externalSimPort = Input(), _simucubePort = Input();
        private readonly PasswordBox _password = new PasswordBox { Width = 180, Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _feedGameData = Toggle(), _blockBuiltInGame = Toggle(), _feedSimucube = Toggle();
        private readonly TextBlock _message = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        public SettingsPanel(RennsportPlugin plugin)
        {
            _plugin = plugin;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddHeader(grid, "Game API");
            AddRow(grid, "Host", _host, "Address of the PC running Rennsport, normally 127.0.0.1");
            AddRow(grid, "Port", _port, "0 = read from the game's BroadcastingSocket.json (default 559)");
            AddRow(grid, "Password", _password, "Empty = read accessPassword from the game's BroadcastingSocket.json");

            AddHeader(grid, "Dashboards / LEDs");
            AddRow(grid, "Enabled", _feedGameData, "Feed the \"Rennsport (API)\" game (stock dashboards, LEDs, ShakeIt). Off = only RennsportPlugin.* properties");
            AddRow(grid, "Port", _externalSimPort, "Local port for the \"Rennsport (API)\" game; change it only if 30559 is taken");
            AddRow(grid, "Block built-in", _blockBuiltInGame, "Stop SimHub's own \"Rennsport\" game from being auto-detected, so automatic game switching picks \"Rennsport (API)\"");

            AddHeader(grid, "Simucube Tuner (optional)");
            AddRow(grid, "Enabled", _feedSimucube, "Send the feed for Tuner's Telemetry FFB. Harmless without Tuner");
            AddRow(grid, "Port", _simucubePort, "Must match the port in Tuner's game_db.json entry (the installer writes 9877)");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(Button<SHButtonPrimary>("Save and apply", Save));
            buttons.Children.Add(Button<SHButtonSecondary>("Undo changes", () => { Load(_plugin.Settings); _message.Text = ""; }));
            buttons.Children.Add(Button<SHButtonSecondary>("Defaults", () => { Load(new RennsportSettings()); Info("Defaults loaded, not saved yet."); }));

            Children.Add(grid);
            Children.Add(buttons);
            Children.Add(_message);

            // reload from the current settings whenever the page is shown
            Loaded += (s, e) => { if (_plugin.Settings != null) Load(_plugin.Settings); _message.Text = ""; };
        }

        private void Load(RennsportSettings s)
        {
            _host.Text = s.Host;
            _port.Text = s.Port.ToString();
            _password.Password = s.Password ?? "";
            _feedGameData.IsChecked = s.FeedGameData;
            _externalSimPort.Text = s.ExternalSimPort.ToString();
            _blockBuiltInGame.IsChecked = s.BlockBuiltInGame;
            _feedSimucube.IsChecked = s.FeedSimucube;
            _simucubePort.Text = s.SimucubePort.ToString();
        }

        private void Save()
        {
            var s = new RennsportSettings
            {
                Host = _host.Text.Trim(),
                Password = _password.Password.Length > 0 ? _password.Password : null,
                FeedGameData = _feedGameData.IsChecked == true,
                BlockBuiltInGame = _blockBuiltInGame.IsChecked == true,
                FeedSimucube = _feedSimucube.IsChecked == true,
            };
            if (!IPAddress.TryParse(s.Host, out _)) { Error("Host must be an IP address, for example 127.0.0.1."); return; }
            if (!TryPort(_port.Text, true, out s.Port)) { Error("Game API port must be 0 or 1–65535."); return; }
            if (!TryPort(_externalSimPort.Text, false, out s.ExternalSimPort)) { Error("Dashboards / LEDs port must be 1–65535."); return; }
            if (!TryPort(_simucubePort.Text, false, out s.SimucubePort)) { Error("Simucube Tuner port must be 1–65535."); return; }

            try
            {
                _plugin.ApplySettings(s);
                Load(_plugin.Settings);
                Info("Saved and applied.");
            }
            catch (Exception ex) { Error("Could not apply the settings: " + ex.Message); }
        }

        private static bool TryPort(string text, bool allowZero, out int port) =>
            int.TryParse(text.Trim(), out port) && port <= 65535 && (port > 0 || (allowZero && port == 0));

        private void Error(string text) { _message.Foreground = Brushes.IndianRed; _message.Text = text; }
        private void Info(string text) { _message.ClearValue(TextBlock.ForegroundProperty); _message.Text = text; }

        private static TextBox Input() =>
            new TextBox { Width = 180, Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };

        private static CheckBox Toggle() =>
            new SHToggleCheckbox { Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };

        private static Button Button<T>(string text, Action click) where T : Button, new()
        {
            var b = new T { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        private static void AddHeader(Grid grid, string title)
        {
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, grid.RowDefinitions.Count == 0 ? 0 : 12, 0, 2) };
            Place(grid, t, 0);
            Grid.SetColumnSpan(t, 3);
        }

        private static void AddRow(Grid grid, string label, FrameworkElement input, string hint)
        {
            int r = grid.RowDefinitions.Count;
            Place(grid, new TextBlock { Text = label, Margin = new Thickness(16, 3, 16, 3), VerticalAlignment = VerticalAlignment.Center }, 0, r);
            Place(grid, input, 1, r);
            Place(grid, new TextBlock { Text = hint, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, 2, r);
        }

        private static void Place(Grid grid, UIElement element, int column, int row = -1)
        {
            if (row < 0 || row == grid.RowDefinitions.Count)
            {
                row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
    }
}
