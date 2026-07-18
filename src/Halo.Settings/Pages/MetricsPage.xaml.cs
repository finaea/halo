using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using Halo.Shared.Metrics;

namespace Halo.Settings.Pages;

public partial class MetricsPage : UserControl, ISettingsPage, IDisposable
{
    private readonly MetricsReader _reader = new();
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<MetricRow> _rows = new();
    private int _builtCount = -1;

    public MetricsPage()
    {
        InitializeComponent();
        MetricGrid.ItemsSource = _rows;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void OnEnter()
    {
        Refresh();
        _timer.Start();
    }

    public void OnLeave() => _timer.Stop();

    private void Refresh()
    {
        if (!_reader.TryAttach())
        {
            NotRunning("Collector not running (no Halo.Metrics.v1 shared section).");
            return;
        }

        double age = _reader.HeartbeatAgeSeconds;
        if (age > 10) // stale section left over from a previous collector; drop it and retry next tick
        {
            _reader.Detach();
            NotRunning("Collector not running (heartbeat stale).");
            return;
        }

        _reader.RefreshRegistry();
        int count = _reader.MetricCount;
        Header.Text = $"Collector pid {_reader.CollectorPid}   ·   heartbeat age {age:0.0}s   ·   {count} metrics";

        if (count != _builtCount)
        {
            _rows.Clear();
            for (int i = 0; i < count; i++)
            {
                var d = _reader.DescribeIndex(i);
                if (d == null) continue;
                var (type, unit, rate, name) = d.Value;
                _rows.Add(new MetricRow(i, name, type.ToString(), unit.ToString(),
                    rate.ToString("0.##", CultureInfo.InvariantCulture), type == MetricType.String));
            }
            _builtCount = count;
        }

        foreach (var row in _rows)
        {
            if (row.IsString)
            {
                row.Value = _reader.TryReadString(row.Index, out string sv) ? sv : "(none)";
                row.Age = "";
            }
            else if (_reader.TryRead(row.Index, out double v, out double a))
            {
                row.Value = v.ToString("0.###", CultureInfo.InvariantCulture);
                row.Age = a.ToString("0.0", CultureInfo.InvariantCulture);
            }
            else
            {
                row.Value = "N/A";
                row.Age = "";
            }
        }
    }

    private void NotRunning(string message)
    {
        Header.Text = message;
        _rows.Clear();
        _builtCount = -1;
    }

    public void Dispose()
    {
        _timer.Stop();
        _reader.Dispose();
    }
}
