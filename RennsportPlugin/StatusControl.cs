using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SimHub.Plugins.Styles;

namespace RennsportPlugin
{
    /// <summary>
    /// The plugin's page in SimHub's left menu: live connection status and hints, plus the settings editor
    /// (SettingsPanel), built in code (no XAML).
    /// Reads the plugin's current client on every tick, because SimHub re-runs Init on game change.
    /// </summary>
    public sealed class StatusControl : UserControl
    {
        private static readonly Brush Ok = Frozen(Color.FromRgb(0x4C, 0xAF, 0x50));
        private static readonly Brush Warn = Frozen(Color.FromRgb(0xFF, 0x98, 0x00));
        private static readonly Brush Bad = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));
        private static readonly Brush Off = Frozen(Color.FromRgb(0x80, 0x80, 0x80));

        private static readonly string[] ResponseCodes = { "Success", "AlreadyRegistered", "NotRegistered", "InvalidPassword", "MalformedMessage" };

        private readonly RennsportPlugin _plugin;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Row _game, _api, _driving, _simhub, _gameData, _tuner, _session;
        private readonly Button _testButton;
        private readonly TextBlock _testNote = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        private long _lastPhysicsCount = -1;
        private double _lastTickAt, _physicsRate, _nextProcessCheckAt;
        private bool _gameProcess, _tunerProcess;

        public StatusControl(RennsportPlugin plugin)
        {
            _plugin = plugin;

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _game = new Row(grid, "Rennsport");
            _api = new Row(grid, "Game API");
            _driving = new Row(grid, "Physics");
            _simhub = new Row(grid, "SimHub game");
            _gameData = new Row(grid, "Dashboards / LEDs");
            _tuner = new Row(grid, "Simucube Tuner");
            _session = new Row(grid, "Session");

            _testButton = new SHButtonPrimary { Margin = new Thickness(0, 0, 8, 0), MinWidth = 110 };
            _testButton.Click += (s, e) => ToggleTest();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(_testButton);
            buttons.Children.Add(Button("Open SimHub log", OpenLog));

            var panel = new StackPanel();
            panel.Children.Add(grid);
            panel.Children.Add(buttons);
            panel.Children.Add(_testNote);
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Text = "All values are also available as RennsportPlugin.* properties (for example RennsportPlugin.Connected) " +
                       "in dashboards and formulas. Plugin messages are written to the SimHub log with the prefix [Rennsport].",
            });

            var page = new StackPanel();
            page.Children.Add(new SHSection { Title = "RENNSPORT TELEMETRY STATUS", ShowSeparator = true, Content = panel });
            page.Children.Add(new SHSection { Title = "SETTINGS", ShowSeparator = true, Content = new SettingsPanel(plugin) });
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };

            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _timer.Start(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        private void Refresh()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now >= _nextProcessCheckAt)
            {
                _nextProcessCheckAt = now + 2;
                _gameProcess = IsRunning("Rennsport");
                _tunerProcess = IsRunning("sc-tuner");
            }

            var client = _plugin.Client;
            var settings = _plugin.Settings;
            if (client == null || settings == null)
            {
                _api.Set(Off, "Plugin is starting…");
                return;
            }

            // physics rate from the packet counter
            long count = client.PhysicsPacketCount;
            if (_lastPhysicsCount >= 0 && now > _lastTickAt)
                _physicsRate = Math.Max(0, (count - _lastPhysicsCount) / (now - _lastTickAt));
            _lastPhysicsCount = count;
            _lastTickAt = now;

            bool connected = client.IsConnected, driving = client.IsReceivingPhysics, test = _plugin.TestRunning;

            _testButton.Content = test ? "Stop test" : "Start test";
            if (!test && _testNote.Text.StartsWith("Test running")) _testNote.Text = ""; // stopped by a SimHub game reload
            if (test)
                _game.Set(Warn, "Test mode: the plugin plays a built-in recording from Monza instead of reading the game");
            else
                _game.Set(_gameProcess ? Ok : Off, _gameProcess ? "Running" : "Not running");

            string server = $"{client.Server.Address}:{client.Server.Port}";
            int code = client.LastResponseCode;
            string codeText = code < 0 ? "no answer yet" : $"{code} {(code < ResponseCodes.Length ? ResponseCodes[code] : "")}".TrimEnd();
            if (test)
                _api.Set(connected ? Ok : Off, $"Test data source on {server} (RegisterClient: {codeText})");
            else if (connected)
                _api.Set(Ok, $"Connected to {server} (RegisterClient: {codeText})");
            else if (code == 3)
                _api.Set(Bad, $"Wrong password for {server}. Enter the game's accessPassword below, or leave the password empty to read it from the game config.");
            else if (_gameProcess)
                _api.Set(Bad, $"No answer from {server}. The game's UDP API is off: close the game, enable it (README step 2), start the game again.");
            else
                _api.Set(Off, $"Waiting for the game on {server}");

            if (driving)
                _driving.Set(Ok, $"Receiving, {_physicsRate:0} packets/s ({client.ParseErrorCount} parse errors)");
            else if (connected)
                _driving.Set(Warn, "None. The game sends physics only while you are on track, not in menus.");
            else
                _driving.Set(Off, "None");

            // SimHub's game code for an external sim is "External_<manifest id>"
            string currentGame = _plugin.CurrentGameName;
            bool ours = string.Equals(currentGame, "External_" + ExternalSimBridge.SimId, StringComparison.OrdinalIgnoreCase);
            bool gameRunning = _plugin.SimHubGameRunning;
            if (!settings.FeedGameData)
                _simhub.Set(Off, ours ? $"\"{ExternalSimBridge.SimName}\" selected" : $"Current game: {currentGame ?? "none"}");
            else if (!ours)
                _simhub.Set(Bad, $"Another game is selected ({currentGame ?? "none"}). Select \"{ExternalSimBridge.SimName}\" in SimHub (not a custom \"Rennsport\" game).");
            else if (gameRunning)
                _simhub.Set(Ok, $"\"{ExternalSimBridge.SimName}\" selected, game running");
            else
                _simhub.Set(driving ? Warn : Off, $"\"{ExternalSimBridge.SimName}\" selected, waiting for data");

            if (!settings.FeedGameData)
                _gameData.Set(Off, "Switched off in Settings: only RennsportPlugin.* properties");
            else if (!_plugin.GameDataFeedActive)
                _gameData.Set(Bad, "Off: " + (_plugin.GameDataFeedError ?? "not started") + ". SimHub 9.13 or newer is needed.");
            else
                _gameData.Set(driving ? Ok : Off, $"Feeding \"{ExternalSimBridge.SimName}\" on 127.0.0.1:{settings.ExternalSimPort}");

            if (!settings.FeedSimucube)
                _tuner.Set(Off, "Feed switched off in Settings");
            else
                _tuner.Set(_tunerProcess && driving ? Ok : Off,
                    $"Feed {(driving ? "sending" : "idle")} to 127.0.0.1:{settings.SimucubePort}, Tuner {(_tunerProcess ? "running" : "not running")} (optional)");

            var stage = client.Stage;
            string session = string.Join(" · ", new[] { stage.Track, stage.StageName }.Where(s => !string.IsNullOrEmpty(s)));
            _session.Set(Off, session.Length > 0 ? session : "—");
        }

        private void ToggleTest()
        {
            try
            {
                if (_plugin.TestRunning) { _plugin.StopTest(); _testNote.Text = ""; }
                else
                {
                    _plugin.StartTest();
                    _testNote.Text = "Test running: about 2.5 minutes of driving at Monza, in a loop. Select \"" + ExternalSimBridge.SimName +
                                     "\" as the game to see it on dashboards and LEDs. Click Stop test to read the game again.";
                }
            }
            catch (Exception ex) { _testNote.Text = "Test failed: " + ex.Message; }
            Refresh();
        }

        private static bool IsRunning(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }

        private static void OpenLog()
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "SimHub.txt");
            Open(File.Exists(log) ? log : Path.GetDirectoryName(log));
        }

        private static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch { }
        }

        private static Button Button(string text, Action click)
        {
            var b = new SHButtonSecondary { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>One status line: dot, label, text.</summary>
        private sealed class Row
        {
            private readonly System.Windows.Shapes.Ellipse _dot;
            private readonly TextBlock _text;

            public Row(Grid grid, string label)
            {
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Off, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                var name = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 16, 3) };
                _text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };

                Grid.SetRow(_dot, r); Grid.SetColumn(_dot, 0);
                Grid.SetRow(name, r); Grid.SetColumn(name, 1);
                Grid.SetRow(_text, r); Grid.SetColumn(_text, 2);
                grid.Children.Add(_dot);
                grid.Children.Add(name);
                grid.Children.Add(_text);
            }

            public void Set(Brush state, string text)
            {
                _dot.Fill = state;
                _text.Text = text;
            }
        }
    }
}
