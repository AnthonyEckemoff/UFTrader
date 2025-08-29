using LightSpeedTrader.IBKR; // your IBKR wrapper
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LightSpeedTrader
{
    public partial class MainWindow : Window
    {
        // --- IBKR client (your wrapper) ---
        private IBKRClient _ibClient;

        // --- Data structures ---
        // Symbol set
        private List<string> _activeSymbols = new();

        // Candles: symbol -> timeframe -> collection
        private readonly Dictionary<string, Dictionary<string, ObservableCollection<FinancialPoint>>> _candlesPerSymbolTimeframe = new();

        // Volume per symbol/timeframe
        private readonly Dictionary<string, Dictionary<string, ObservableCollection<ObservableValue>>> _volumePerSymbolTimeframe = new();

        // SMA/EMA per symbol/timeframe
        private readonly Dictionary<string, Dictionary<string, ObservableCollection<ObservableValue>>> _smaPerSymbolTimeframe = new();
        private readonly Dictionary<string, Dictionary<string, ObservableCollection<ObservableValue>>> _emaPerSymbolTimeframe = new();

        // Trade markers (global)
        private readonly ObservableCollection<ObservablePoint> _buyMarkers = new();
        private readonly ObservableCollection<ObservablePoint> _sellMarkers = new();

        // charts dictionary
        private readonly Dictionary<string, CartesianChart> _charts = new();

        //private readonly Dictionary<CartesianChart, bool> _crosshairLocked = new();
        //private readonly Dictionary<CartesianChart, Coordinate> _lockedCrosshair = new();
        //private readonly Dictionary<CartesianChart, Tooltip> _chartTooltips = new();

        // Alerts
        public class PriceAlert
        {
            public string Symbol { get; set; }
            public double Price { get; set; }
            public bool Above { get; set; }
            public bool OneShot { get; set; } = true;
            public bool Triggered { get; set; } = false;
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public override string ToString()
            {
                var dir = Above ? "Above" : "Below";
                return $"{Symbol} {dir} {Price:F2} {(Triggered ? "[TRIGGERED]" : "")}";
            }
        }
        private readonly List<PriceAlert> _alerts = new();

        // misc
        private const int MaxCandles = 200;
        private readonly string[] _timeframes = new[] { "1m", "5m", "15m" };
        private DispatcherTimer _snoozeTimer;

        public MainWindow()
        {
            InitializeComponent();

            _charts["1m"] = Chart1Min;
            _charts["5m"] = Chart5Min;
            _charts["15m"] = Chart15Min;

            InitializeChartDefaults();
            //HookChartEvents();

            _ibClient = new IBKRClient();

            _ibClient.OnConnectionStatusChanged += status =>
            {
                Dispatcher.Invoke(() =>
                {
                    EnvironmentStatus.Text = status;
                    EnvironmentStatus.Foreground = status.Contains("PAPER") ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Red;
                });
            };

            _ibClient.OnRealtimeBar += (symbol, timeframe, time, open, high, low, close, volume) =>
            {
                Dispatcher.Invoke(() =>
                {
                    EnsureSymbolTimeframe(symbol, timeframe);

                    var candles = _candlesPerSymbolTimeframe[symbol][timeframe];
                    var volumes = _volumePerSymbolTimeframe[symbol][timeframe];

                    double prevClose = candles.LastOrDefault()?.Close ?? open;
                    var dt = DateTime.FromOADate(time);

                    var fp = new FinancialPoint(dt, high, open, close, low);
                    candles.Add(fp);
                    volumes.Add(new ObservableValue(volume));

                    if (candles.Count > MaxCandles) candles.RemoveAt(0);
                    if (volumes.Count > MaxCandles) volumes.RemoveAt(0);

                    UpdateIndicators(symbol, timeframe);
                    UpdateChartSeriesFor(symbol, timeframe);
                    CheckAlertsForSymbol(symbol, fp);
                });
            };

            foreach (var kv in _charts)
            {
                AttachSeriesLayers(kv.Value, kv.Key);
            }
        }

        #region Initialization helpers

        private void InitializeChartDefaults()
        {
            // Formatters
            Func<double, string> xFormatter = val => DateTime.FromOADate(val).ToString("HH:mm");
            Func<double, string> priceFormatter = v => v.ToString("F2");

            foreach (var kv in _charts)
            {
                var chart = kv.Value;
                chart.Series = new ISeries[] { }; // will be filled on symbol add
                chart.XAxes = new Axis[] { new Axis { Labeler = xFormatter, LabelsRotation = 15 } };
                chart.YAxes = new Axis[]
                {
                    new Axis { Name = "Price", Labeler = priceFormatter },
                    new Axis { Name = "Volume", Position = LiveChartsCore.Measure.AxisPosition.End}
                };

                // default tooltip

                chart.TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Auto;
                chart.FindingStrategy = LiveChartsCore.Measure.FindingStrategy.Automatic;
                chart.ZoomMode = LiveChartsCore.Measure.ZoomAndPanMode.X;
            }
        }

        // Call this once after creating your charts
        //private void HookChartEvents()
        //{
        //    foreach (var kv in _charts)
        //    {
        //        var chart = kv.Value;

        //        // Initialize tooltip if not already
        //        if (!_chartTooltips.ContainsKey(chart))
        //        {
        //            var tooltip = new Tooltip
        //            {
        //                SelectionMode = TooltipSelectionMode.OnlySender,
        //                ShowSeries = true,
        //            };
        //            chart.Tooltip = tooltip;
        //            _chartTooltips[chart] = tooltip;
        //        }

        //        // Mouse move -> update crosshair and tooltip
        //        chart.MouseMove += (s, e) =>
        //        {
        //            // skip if crosshair is locked
        //            if (_crosshairLocked.TryGetValue(chart, out var locked) && locked)
        //                return;

        //            Point pos = e.GetPosition(chart);
        //            var points = chart.GetPointsAt(new LvcPointD(pos.X, pos.Y)).ToArray();
        //            if (points.Length == 0) return;

        //            var nearest = points[0];

        //            // store coordinate for crosshair rendering
        //            UpdateCrosshair(chart, nearest);

        //            // update tooltip
        //            _chartTooltips[chart].ShowTooltip(new[] { nearest });
        //        };

        //        // Mouse down -> toggle lock
        //        chart.MouseDown += (s, e) =>
        //        {
        //            Point pos = e.GetPosition(chart);
        //            var points = chart.GetPointsAt(new LvcPointD(pos.X, pos.Y)).ToArray();
        //            if (points.Length == 0) return;

        //            var nearest = points[0];

        //            if (!_crosshairLocked.ContainsKey(chart))
        //                _crosshairLocked[chart] = false;

        //            _crosshairLocked[chart] = !_crosshairLocked[chart];

        //            if (_crosshairLocked[chart])
        //            {
        //                _lockedCrosshair[chart] = nearest.Coordinate;
        //                // show tooltip at locked position
        //                _chartTooltips[chart].ShowTooltip(new[] { nearest });
        //            }
        //            else
        //            {
        //                _lockedCrosshair.Remove(chart);
        //                _chartTooltips[chart].Hide();
        //            }
        //        };

        //        // Optional: Mouse leave -> hide crosshair if not locked
        //        chart.MouseLeave += (s, e) =>
        //        {
        //            if (_crosshairLocked.TryGetValue(chart, out var locked) && !locked)
        //            {
        //                _chartTooltips[chart].Hide();
        //            }
        //        };
        //    }
        //}

        // Render crosshair at given coordinate
        //private void UpdateCrosshair(CartesianChart chart, ChartPoint point)
        //{
        //    // LiveChartsCore WPF renders the crosshair via tooltip or custom visual element
        //    // We just ensure the tooltip is at the correct point
        //    // For custom crosshair visuals, you can draw a LineSeries or Rectangle visual
        //}

        //// Optional helper to update crosshair when locked (redraw)
        //private void RefreshLockedCrosshair(CartesianChart chart)
        //{
        //    if (_crosshairLocked.TryGetValue(chart, out var locked) && locked && _lockedCrosshair.TryGetValue(chart, out var coord))
        //    {
        //        // create dummy ChartPoint with locked coordinate
        //        var dummyPoint = ChartPoint.Empty;
        //        dummyPoint.Coordinate = coord;
        //        _chartTooltips[chart].ShowTooltip(new[] { dummyPoint });
        //    }
        //}

        private void AttachSeriesLayers(CartesianChart chart, string timeframeKey)
        {
            // Each chart will contain per-symbol series: candlesticks, volume (scalesYAt=1), SMA, EMA, plus global buy/sell markers (scatter)
            // We'll keep chart.Series dynamic when symbols are added.
            // Also add the marker series if not present:
            var existing = chart.Series?.ToList() ?? new List<ISeries>();

            // Buy/Sell marker series (global)
            if (!existing.Any(s => s.Name == "Buys"))
            {
                existing.Add(new ScatterSeries<ObservablePoint>
                {
                    Name = "Buys",
                    Values = _buyMarkers,
                    GeometrySize = 12,
                    Fill = new SolidColorPaint(SKColors.LimeGreen)
                });
            }
            if (!existing.Any(s => s.Name == "Sells"))
            {
                existing.Add(new ScatterSeries<ObservablePoint>
                {
                    Name = "Sells",
                    Values = _sellMarkers,
                    GeometrySize = 12,
                    Fill = new SolidColorPaint(SKColors.Red)
                });
            }

            chart.Series = existing.ToArray();
        }

        #endregion

        #region Watchlist & symbol wiring

        private void EnsureSymbolContainers(string symbol)
        {
            if (!_candlesPerSymbolTimeframe.ContainsKey(symbol))
            {
                _candlesPerSymbolTimeframe[symbol] = new Dictionary<string, ObservableCollection<FinancialPoint>>();
                _volumePerSymbolTimeframe[symbol] = new Dictionary<string, ObservableCollection<ObservableValue>>();
                _smaPerSymbolTimeframe[symbol] = new Dictionary<string, ObservableCollection<ObservableValue>>();
                _emaPerSymbolTimeframe[symbol] = new Dictionary<string, ObservableCollection<ObservableValue>>();

                foreach (var tf in _timeframes)
                {
                    _candlesPerSymbolTimeframe[symbol][tf] = new ObservableCollection<FinancialPoint>();
                    _volumePerSymbolTimeframe[symbol][tf] = new ObservableCollection<ObservableValue>();
                    _smaPerSymbolTimeframe[symbol][tf] = new ObservableCollection<ObservableValue>();
                    _emaPerSymbolTimeframe[symbol][tf] = new ObservableCollection<ObservableValue>();
                }
            }
        }

        private void EnsureSymbolTimeframe(string symbol, string timeframe)
        {
            EnsureSymbolContainers(symbol);
            if (!_candlesPerSymbolTimeframe[symbol].ContainsKey(timeframe))
            {
                _candlesPerSymbolTimeframe[symbol][timeframe] = new ObservableCollection<FinancialPoint>();
                _volumePerSymbolTimeframe[symbol][timeframe] = new ObservableCollection<ObservableValue>();
                _smaPerSymbolTimeframe[symbol][timeframe] = new ObservableCollection<ObservableValue>();
                _emaPerSymbolTimeframe[symbol][timeframe] = new ObservableCollection<ObservableValue>();
            }
        }

        private void AddSymbolToWatchlist(string symbol)
        {
            symbol = symbol.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol) || _activeSymbols.Contains(symbol)) return;

            _activeSymbols.Add(symbol);
            EnsureSymbolContainers(symbol);

            // Add to watchlist UI
            WatchlistBox.Items.Add(symbol);
            AlertSymbolCombo.Items.Add(symbol);

            // Add series for this symbol into every chart (each timeframe chart)
            foreach (var tf in _timeframes)
            {
                var chart = GetChartForTimeframe(tf);
                if (chart == null) continue;

                // Candlestick series
                var cs = new CandlesticksSeries<FinancialPoint>
                {
                    Name = $"{symbol} {tf} Candles",
                    Values = _candlesPerSymbolTimeframe[symbol][tf],
                    UpFill = new SolidColorPaint(SKColors.LightGreen),
                    UpStroke = new SolidColorPaint(SKColors.Green),
                    DownFill = new SolidColorPaint(SKColors.IndianRed),
                    DownStroke = new SolidColorPaint(SKColors.Red),
                    // Optional: format data labels instead of tooltip
                    DataLabelsFormatter = point =>
                    {
                        var fp = point.Model as FinancialPoint;
                        if (fp == null) return null;
                        return $"{symbol} O:{fp.Open:F2} H:{fp.High:F2} L:{fp.Low:F2} C:{fp.Close:F2}";
                    }
                };

                // Volume series (scale to Y axis index 1)
                var vs = new ColumnSeries<ObservableValue>
                {
                    Name = $"{symbol} {tf} Volume",
                    Values = _volumePerSymbolTimeframe[symbol][tf],
                    MaxBarWidth = 12,
                    ScalesYAt = 1
                };

                // SMA and EMA line series
                var smaSeries = new LineSeries<ObservableValue>
                {
                    Name = $"{symbol} {tf} SMA",
                    Values = _smaPerSymbolTimeframe[symbol][tf],
                    Stroke = new SolidColorPaint(SKColors.Orange) { StrokeThickness = 1.5f },
                    GeometrySize = 0
                };
                var emaSeries = new LineSeries<ObservableValue>
                {
                    Name = $"{symbol} {tf} EMA",
                    Values = _emaPerSymbolTimeframe[symbol][tf],
                    Stroke = new SolidColorPaint(SKColors.MediumPurple) { StrokeThickness = 1.5f },
                    GeometrySize = 0
                };

                // Insert series into chart (preserve markers)
                var list = chart.Series?.ToList() ?? new List<ISeries>();
                int insertIdx = list.FindIndex(s => s.Name == "Buys");
                if (insertIdx < 0) insertIdx = list.Count;

                list.Insert(insertIdx, cs);
                list.Insert(insertIdx + 1, vs);
                list.Insert(insertIdx + 2, smaSeries);
                list.Insert(insertIdx + 3, emaSeries);

                chart.Series = list.ToArray();
            }

            // Ensure UI selects new symbol
            WatchlistBox.SelectedItem = symbol;
        }


        private CartesianChart GetChartForTimeframe(string timeframe)
        {
            if (string.IsNullOrEmpty(timeframe)) return null;
            return _charts.TryGetValue(timeframe, out var c) ? c : null;
        }

        #endregion

        #region Indicators (SMA/EMA) calculation

        private void UpdateIndicators(string symbol, string timeframe)
        {
            var candles = _candlesPerSymbolTimeframe[symbol][timeframe];
            var smaCol = _smaPerSymbolTimeframe[symbol][timeframe];
            var emaCol = _emaPerSymbolTimeframe[symbol][timeframe];

            var closes = candles.Select(c => c.Close).ToArray();
            int n = closes.Length;
            int period = Math.Min(14, Math.Max(2, Math.Min(20, n))); // default 14, but safe

            // SMA
            smaCol.Clear();
            var smaVals = ComputeSMA(closes, period);
            foreach (var v in smaVals) smaCol.Add(new ObservableValue(v));

            // EMA
            emaCol.Clear();
            var emaVals = ComputeEMA(closes, period);
            foreach (var v in emaVals) emaCol.Add(new ObservableValue(v));
        }

        private double[] ComputeSMA(double[] values, int period)
        {
            var result = new double[values.Length];
            double sum = 0;
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
                if (i >= period) sum -= values[i - period];
                result[i] = (i >= period - 1) ? sum / period : values[i];
            }
            return result;
        }

        private double[] ComputeEMA(double[] values, int period)
        {
            var result = new double[values.Length];
            if (values.Length == 0) return result;
            double k = 2.0 / (period + 1);
            result[0] = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                result[i] = values[i] * k + result[i - 1] * (1 - k);
            }
            return result;
        }

        #endregion

        #region Chart updates and visuals

        private void UpdateChartSeriesFor(string symbol, string timeframe)
        {
            // Update volume coloring and keep series in sync
            var chart = GetChartForTimeframe(timeframe);
            if (chart == null) return;

            // Get all candles for the symbol/timeframe
            if (!_candlesPerSymbolTimeframe.TryGetValue(symbol, out var tfCandles) ||
                !tfCandles.TryGetValue(timeframe, out var all) ||
                all.Count == 0) return;

            // Auto-scroll X axis: show last N candles
            var xAxis = chart.XAxes.FirstOrDefault();
            if (xAxis != null)
            {
                int show = 60;
                int start = Math.Max(0, all.Count - show);
                xAxis.MinLimit = all[start].Coordinate.SecondaryValue;  // Use numeric X
                xAxis.MaxLimit = all.Last().Coordinate.SecondaryValue;
            }

            // Update volume series
            var volSeriesName = $"{symbol} {timeframe} Volume";
            var volSeries = chart.Series.FirstOrDefault(s => s.Name == volSeriesName) as ColumnSeries<ObservableValue>;
            if (volSeries != null)
            {
                var vols = _volumePerSymbolTimeframe[symbol][timeframe];

                // Assign numeric values only; no Tag or PointForeground
                volSeries.Values = vols;

                // Optional: manually update the coordinate if needed
                for (int i = 0; i < vols.Count; i++)
                {
                    var v = vols[i];
                    v.Coordinate = new Coordinate(i, v.Value ?? 0); // X = index, Y = value
                }
            }
        }


        #endregion

        #region Alerts

        private void AddAlertButton_Click(object sender, RoutedEventArgs e)
        {
            if (AlertSymbolCombo.SelectedItem == null) { ShowBanner("Select symbol for alert"); return; }
            if (!double.TryParse(AlertPriceBox.Text, out double price)) { ShowBanner("Invalid alert price"); return; }
            var sym = AlertSymbolCombo.SelectedItem.ToString();
            var dirItem = AlertDirectionCombo.SelectedItem as ComboBoxItem;
            bool above = (dirItem?.Tag?.ToString() ?? "Above") == "Above";
            var a = new PriceAlert { Symbol = sym, Price = price, Above = above, OneShot = AlertOneShotCheck.IsChecked == true };
            _alerts.Add(a);
            AlertsListBox.Items.Insert(0, a.ToString());
            ShowBanner($"Alert added: {a}");
        }

        private void CheckAlertsForSymbol(string symbol, FinancialPoint latestCandle)
        {
            if (_alerts == null || !_alerts.Any()) return;

            double currentPrice = latestCandle.Close;
            var matches = _alerts.Where(a => !a.Triggered && a.Symbol == symbol).ToList();
            foreach (var alert in matches)
            {
                bool triggered = alert.Above ? currentPrice >= alert.Price : currentPrice <= alert.Price;
                if (triggered)
                {
                    alert.Triggered = true;
                    string msg = $"{DateTime.Now:HH:mm:ss} ALERT: {alert.Symbol} {(alert.Above ? ">=" : "<=")} {alert.Price:F2} (price {currentPrice:F2})";
                    AlertsListBox.Items.Insert(0, msg);
                    TradeLogBox.Items.Insert(0, msg);
                    SystemSounds.Beep.Play();
                    // non-blocking banner
                    ShowBanner(msg);
                    if (alert.OneShot)
                    {
                        // keep as triggered but you might want to remove it from list
                    }
                }
            }
        }

        private void RemoveAlert_Click(object sender, RoutedEventArgs e)
        {
            if (AlertsListBox.SelectedItem is string selected)
            {
                // remove first matching alert text or raw string
                var toRemove = _alerts.FirstOrDefault(a => a.ToString() == selected);
                if (toRemove != null) _alerts.Remove(toRemove);
                AlertsListBox.Items.Remove(selected);
            }
        }

        private void SnoozeAlert_Click(object sender, RoutedEventArgs e)
        {
            if (AlertsListBox.SelectedItem is string selected)
            {
                AlertsListBox.Items.Remove(selected);
                // re-add after 5 minutes
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                timer.Tick += (s, ev) =>
                {
                    AlertsListBox.Items.Add(selected);
                    timer.Stop();
                };
                timer.Start();
                ShowBanner("Alert snoozed for 5 minutes");
            }
        }

        private void ShowBanner(string text)
        {
            AlertBannerText.Text = text;
            AlertBanner.Visibility = Visibility.Visible;

            // auto-hide after 8 seconds
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t.Tick += (s, e) =>
            {
                AlertBanner.Visibility = Visibility.Collapsed;
                t.Stop();
            };
            t.Start();
        }

        private void CloseBanner_Click(object sender, RoutedEventArgs e)
        {
            AlertBanner.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region Trading

        private void DollarAmountTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var ok = decimal.TryParse(DollarAmountTextBox.Text, out _);
            BuyButton.IsEnabled = ok && !string.IsNullOrWhiteSpace(SelectedSymbolText.Text);
            SellButton.IsEnabled = ok && !string.IsNullOrWhiteSpace(SelectedSymbolText.Text);
        }

        private void BuyButton_Click(object sender, RoutedEventArgs e) => ExecuteTrade("BUY");
        private void SellButton_Click(object sender, RoutedEventArgs e) => ExecuteTrade("SELL");

        private void ExecuteTrade(string side)
        {
            var symbol = SelectedSymbolText.Text;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                ShowBanner("No symbol selected");
                return;
            }

            if (!decimal.TryParse(DollarAmountTextBox.Text, out var amount))
            {
                ShowBanner("Invalid amount");
                return;
            }

            try
            {
                // Place the order via IBKR
                _ibClient.PlaceOrder(symbol, amount, side);

                // Add marker at latest candle close
                var tf = "1m"; // default timeframe
                EnsureSymbolTimeframe(symbol, tf);

                if (!_candlesPerSymbolTimeframe.TryGetValue(symbol, out var tfCandles) ||
                    !tfCandles.TryGetValue(tf, out var candles) ||
                    candles.Count == 0) return;

                var last = candles.Last();
                var x = last.Coordinate.SecondaryValue; // X-axis (ticks)
                var y = last.Close;                      // Y-axis (Close price)

                if (side == "BUY")
                    _buyMarkers.Add(new ObservablePoint(x, y));
                else
                    _sellMarkers.Add(new ObservablePoint(x, y));

                TradeLogBox.Items.Insert(0, $"{DateTime.Now:HH:mm:ss} {side} {symbol} ${amount} @ {y:F2}");
            }
            catch (Exception ex)
            {
                ShowBanner("Order error: " + ex.Message);
            }
        }



        #endregion

        #region UI events and helpers

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out int port)) port = 4002;
            if (!int.TryParse(ClientIdTextBox.Text, out int clientId)) clientId = 0;
            var host = HostTextBox.Text;
            Task.Run(async () =>
            {
                try
                {
                    await _ibClient.ConnectAsync(host, port, clientId);
                    Dispatcher.Invoke(() => ShowBanner("Connected!"));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => ShowBanner("Connect failed: " + ex.Message));
                }
            });
        }

        private void WatchButton_Click(object sender, RoutedEventArgs e)
        {
            var sym = StockTextBox.Text?.Trim().ToUpper();
            if (string.IsNullOrEmpty(sym)) { ShowBanner("Enter symbol"); return; }
            AddSymbolToWatchlist(sym);

            // Request realtime bars from IBKR for each timeframe you want
            foreach (var tf in _timeframes)
            {
                _ibClient.RequestRealtimeBars(sym, tf); // adapt to your IBKRClient signature
            }
            PopulateAlertSymbols();
            ShowBanner($"Watching {sym}");
        }

        private void WatchlistBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WatchlistBox.SelectedItem is string sym)
            {
                SelectedSymbolText.Text = sym;
                DollarAmountTextBox_TextChanged(null, null);
                // focus chart: select the 1m tab by default
                ChartTabs.SelectedIndex = 0;
            }
        }

        private void RemoveFromWatchlist_Click(object sender, RoutedEventArgs e)
        {
            if (WatchlistBox.SelectedItem is string sym)
            {
                _activeSymbols.Remove(sym);
                WatchlistBox.Items.Remove(sym);
                AlertSymbolCombo.Items.Remove(sym);
                // Removing series from charts is left as exercise: you can find series by Name and remove them from chart.Series
            }
        }

        private void RequestHistory_Click(object sender, RoutedEventArgs e)
        {
            if (WatchlistBox.SelectedItem is string sym)
            {
                // ask IBKR for historical bars for each timeframe (you need to implement RequestHistoricalBars in IBKRClient)
                foreach (var tf in _timeframes)
                {
                    _ibClient.RequestHistoricalBars(sym, tf); // optional
                }
                ShowBanner($"Requested history for {sym}");
            }
        }

        private void PopulateAlertSymbols()
        {
            AlertSymbolCombo.Items.Clear();
            foreach (var s in _activeSymbols) AlertSymbolCombo.Items.Add(s);
        }

        #endregion
    }
}
