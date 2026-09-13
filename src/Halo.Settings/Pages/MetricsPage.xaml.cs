using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Halo.Metrics;

namespace Halo.Settings.Pages;

public partial class MetricsPage : UserControl, ISettingsPage, IDisposable
{
    // Same client package a third-party tool would use, so this page exercises the public API.
    private readonly CollectorSession _session = new();
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<MetricRow> _rows = new();
    private int _builtCount = -1;

    public MetricsPage()
    {
        InitializeComponent();
        MetricGrid.ItemsSource = _rows;
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = o => FilterBox.Text.Length == 0 ||
            ((MetricRow)o).Name.Contains(FilterBox.Text, StringComparison.OrdinalIgnoreCase);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
        => CollectionViewSource.GetDefaultView(_rows).Refresh();

    public void OnEnter()
    {
        Refresh();
        _timer.Start();
    }

    public void OnLeave() => _timer.Stop();

    private void Refresh()
    {
        _session.Poll();
        if (!_session.Attached)
        {
            NotRunning($"Collector not running (no {SharedMemoryLayout.SectionName} section).");
            return;
        }

        double age = _session.HeartbeatAgeSeconds;
        int count = _session.MetricCount;
        Header.Text = $"Collector {_session.CollectorVersion} pid {_session.CollectorPid}   ·   heartbeat age {age:0.0}s   ·   {count} metrics";

        if (count != _builtCount)
        {
            _rows.Clear();
            foreach (var m in _session.Metrics())
                _rows.Add(new MetricRow(m.Index, m.Name, m.Type.ToString(), m.Unit.ToString(),
                    m.NominalRateHz.ToString("0.##", CultureInfo.InvariantCulture), m.Type == MetricType.String));
            _builtCount = count;
        }

        foreach (var row in _rows)
        {
            if (row.IsString)
            {
                row.Value = _session.Reader.TryReadString(row.Index, out string sv) ? sv : "(none)";
                row.Age = "";
            }
            else if (_session.Reader.TryRead(row.Index, out double v, out double a))
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
        _session.Dispose();
    }
}
