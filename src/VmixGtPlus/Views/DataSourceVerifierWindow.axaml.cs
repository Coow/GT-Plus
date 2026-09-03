using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VmixGtPlus.Controls;
using VmixGtPlus.Models;
using VmixGtPlus.Services;

namespace VmixGtPlus.Views;

/// <summary>checks a data source against the open title, fetch or paste JSON / XML / CSV then see which named elements vMix would fill and which would be left empty</summary>
public partial class DataSourceVerifierWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly IBrush OkBrush          = new SolidColorBrush(Color.Parse("#4caf50"));
    private static readonly IBrush WarnBrush        = new SolidColorBrush(Color.Parse("#ffb300"));
    private static readonly IBrush ErrorBrush       = new SolidColorBrush(Color.Parse("#e05a5a"));
    private static readonly IBrush InfoBrush        = new SolidColorBrush(Color.Parse("#5a8fd6"));
    private static readonly IBrush MutedBrush       = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush NameBrush        = new SolidColorBrush(Color.Parse("#dddddd"));
    private static readonly IBrush ValueBrush       = new SolidColorBrush(Color.Parse("#9fd08f"));
    private static readonly IBrush SeparatorBrush   = new SolidColorBrush(Color.Parse("#242424"));

    private readonly GtCanvasControl? _canvas;
    private readonly DispatcherTimer _debounce;

    private DataParseResult? _result;
    private bool _suppress;
    private bool _sortByProblem;

    // parameterless ctor for the XAML runtime loader, the app always uses the one below
    public DataSourceVerifierWindow() : this(null) { }

    public DataSourceVerifierWindow(GtCanvasControl? canvas)
    {
        InitializeComponent();
        _canvas = canvas;

        _suppress = true;
        FormatCombo.SelectedIndex = 0;
        _suppress = false;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Verify(); };

        // the title can change while this window sits open, so re-match on every activation
        Activated += (_, _) => BuildResults();

        UpdateOptionVisibility();
        Verify();
    }

    private DataSourceFormat SelectedFormat => FormatCombo.SelectedIndex switch
    {
        1 => DataSourceFormat.Xml,
        2 => DataSourceFormat.Csv,
        _ => DataSourceFormat.Json,
    };

    private GtDocument? Document => _canvas?.Document;

    private async void Get_Click(object? sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            FetchStatus.Text       = "Enter a URL first.";
            FetchStatus.Foreground = ErrorBrush;
            return;
        }

        if (!url!.Contains("://")) url = "http://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            FetchStatus.Text       = "That is not a valid URL.";
            FetchStatus.Foreground = ErrorBrush;
            return;
        }

        GetButton.IsEnabled    = false;
        FetchStatus.Foreground = MutedBrush;
        FetchStatus.Text       = "Fetching...";

        try
        {
            using var response = await Http.GetAsync(uri);
            var bytes       = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            LoadPayload(bytes, contentType, uri.AbsolutePath);

            FetchStatus.Foreground = response.IsSuccessStatusCode ? MutedBrush : ErrorBrush;
            FetchStatus.Text       = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}  ·  " +
                                     FormatSize(bytes.Length);
        }
        catch (Exception ex)
        {
            FetchStatus.Foreground = ErrorBrush;
            FetchStatus.Text       = "Request failed: " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            GetButton.IsEnabled = true;
        }
    }

    private async void File_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Data Source",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Data Sources")
                    { Patterns = new[] { "*.json", "*.xml", "*.csv", "*.tsv", "*.txt", "*.xlsx" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;

        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            LoadPayload(bytes, "", path);

            FetchStatus.Foreground = MutedBrush;
            FetchStatus.Text       = Path.GetFileName(path) + "  ·  " + FormatSize(bytes.Length);
        }
        catch (Exception ex)
        {
            FetchStatus.Foreground = ErrorBrush;
            FetchStatus.Text       = "Could not read the file: " + ex.Message;
        }
    }

    /// <summary>puts a fetched payload into the data box, converting workbooks to CSV and picking the format from the content type or file extension when it is unambiguous</summary>
    private void LoadPayload(byte[] bytes, string contentType, string pathOrUrl)
    {
        if (DataSourceService.LooksLikeXlsx(bytes))
        {
            if (DataSourceService.TryConvertXlsxToCsv(bytes, out var csv, out var error))
            {
                SetFormat(DataSourceFormat.Csv);
                DataBox.Text = csv;
                return;
            }

            FetchStatus.Foreground = ErrorBrush;
            FetchStatus.Text       = error;
            return;
        }

        var sniffed = Sniff(contentType, pathOrUrl);
        if (sniffed is not null) SetFormat(sniffed.Value);

        DataBox.Text = Decode(bytes);
    }

    private static DataSourceFormat? Sniff(string contentType, string pathOrUrl)
    {
        var ct  = contentType.ToLowerInvariant();
        var ext = Path.GetExtension(pathOrUrl).ToLowerInvariant();

        if (ct.Contains("json") || ext == ".json") return DataSourceFormat.Json;
        if (ct.Contains("xml")  || ext == ".xml")  return DataSourceFormat.Xml;
        if (ct.Contains("csv")  || ext == ".csv" || ext == ".tsv") return DataSourceFormat.Csv;
        return null;
    }

    private static string Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string FormatSize(int bytes) =>
        bytes < 1024 ? bytes + " B" : (bytes / 1024.0).ToString("0.#") + " KB";

    private void SetFormat(DataSourceFormat format)
    {
        var index = format switch
        {
            DataSourceFormat.Xml => 1,
            DataSourceFormat.Csv => 2,
            _                    => 0,
        };

        if (FormatCombo.SelectedIndex == index) return;

        _suppress = true;
        FormatCombo.SelectedIndex = index;
        _suppress = false;
        UpdateOptionVisibility();
    }

    private void DataBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void FormatCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        UpdateOptionVisibility();
        Verify();
    }

    private void HeaderCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        Verify();
    }

    private void RecordCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        BuildResults();
    }

    private void SortButton_Click(object? sender, RoutedEventArgs e)
    {
        _sortByProblem = SortButton.IsChecked == true;
        BuildResults();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void UpdateOptionVisibility()
    {
        HeaderCheck.IsVisible = SelectedFormat == DataSourceFormat.Csv;

        DataHint.Text = SelectedFormat switch
        {
            DataSourceFormat.Json => "flat object, or array of flat objects",
            DataSourceFormat.Xml  => "flat nodes, or a repeating row node",
            _                     => "comma, semicolon or tab separated",
        };
    }

    private void Verify()
    {
        var previous = RecordCombo.SelectedIndex;

        _result = DataSourceService.Parse(DataBox.Text, SelectedFormat, HeaderCheck.IsChecked == true);

        _suppress = true;
        var labels = _result.Records.Select(r => r.Label).ToList();
        RecordCombo.ItemsSource = labels;
        RecordCombo.SelectedIndex = labels.Count == 0 ? -1
            : Math.Min(Math.Max(previous, 0), labels.Count - 1);
        _suppress = false;

        RecordPanel.IsVisible = labels.Count > 1;

        BuildResults();
    }

    private DataRecord? CurrentRecord
    {
        get
        {
            if (_result is null || _result.Records.Count == 0) return null;
            var index = RecordCombo.SelectedIndex;
            if (index < 0 || index >= _result.Records.Count) index = 0;
            return _result.Records[index];
        }
    }

    private void BuildResults()
    {
        ResultStack.Children.Clear();

        var doc = Document;

        if (doc is null)
        {
            ResultStack.Children.Add(Note("No title open. Open a .gtzip to verify against it.", MutedBrush));
            ElementsHint.Text = "";
            SummaryText.Text  = "";
            return;
        }

        if (_result is not null && _result.Errors.Count > 0)
            foreach (var error in _result.Errors)
                ResultStack.Children.Add(Note(error, ErrorBrush));

        var record  = CurrentRecord;
        var matches = DataMatchService.Match(doc, record);

        if (matches.Count == 0)
        {
            ResultStack.Children.Add(Note("The title has no named elements to bind.", MutedBrush));
            ElementsHint.Text = "0 elements";
            SummaryText.Text  = "";
            return;
        }

        // OrderBy is stable, so document order survives inside each severity group
        var ordered = _sortByProblem
            ? matches.OrderBy(m => DataMatchService.SeverityRank(m.Status)).ToList()
            : matches;

        foreach (var match in ordered)
            ResultStack.Children.Add(MatchRow(match));

        var unused = DataMatchService.UnusedFields(doc, record);
        if (unused.Count > 0)
        {
            ResultStack.Children.Add(SectionHeader($"UNUSED DATA FIELDS ({unused.Count})"));
            foreach (var field in unused)
                ResultStack.Children.Add(UnusedRow(field));
        }

        ElementsHint.Text = matches.Count + (matches.Count == 1 ? " element" : " elements");
        SummaryText.Text  = Summarise(matches, unused.Count);
        SummaryText.Foreground = matches.Any(m => m.Status is DataMatchStatus.Missing
                                                       or DataMatchStatus.CaseMismatch
                                                       or DataMatchStatus.FieldError)
            ? WarnBrush
            : MutedBrush;
    }

    private static string Summarise(List<DataElementMatch> matches, int unusedCount)
    {
        var parts = new List<string> { matches.Count + " elements" };

        void Add(DataMatchStatus status, string label)
        {
            var count = matches.Count(m => m.Status == status);
            if (count > 0) parts.Add(count + " " + label);
        }

        Add(DataMatchStatus.Ok, "matched");
        Add(DataMatchStatus.CaseMismatch, "case mismatch");
        Add(DataMatchStatus.Missing, "missing");
        Add(DataMatchStatus.FieldError, "unusable");
        Add(DataMatchStatus.Unverified, "not verified");
        Add(DataMatchStatus.NotBindable, "hidden");

        if (unusedCount > 0) parts.Add(unusedCount + " unused field" + (unusedCount == 1 ? "" : "s"));

        return string.Join("  ·  ", parts);
    }

    private static IBrush StatusBrush(DataMatchStatus status) => status switch
    {
        DataMatchStatus.Ok           => OkBrush,
        DataMatchStatus.CaseMismatch => WarnBrush,
        DataMatchStatus.Missing      => ErrorBrush,
        DataMatchStatus.FieldError   => ErrorBrush,
        DataMatchStatus.Unverified   => InfoBrush,
        _                            => MutedBrush,
    };

    private Control MatchRow(DataElementMatch match)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill  = StatusBrush(match.Status),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var name = new TextBlock
        {
            Text = match.ElementName,
            Foreground = NameBrush,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // shapes bind their colour as Name.Fill.Color, so spell out the field when it differs
        var field = new TextBlock
        {
            Text = match.FieldName.Equals(match.ElementName, StringComparison.Ordinal)
                ? ""
                : match.FieldName,
            Foreground = MutedBrush,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var kind = new TextBlock
        {
            Text = match.Kind,
            Foreground = MutedBrush,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0),
        };

        var value = new TextBlock
        {
            Text = match.Status == DataMatchStatus.Missing ? "-" : DisplayValue(match.Value),
            Foreground = match.Status == DataMatchStatus.Missing ? MutedBrush : ValueBrush,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*") };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(field, 2);
        Grid.SetColumn(kind, 3);
        Grid.SetColumn(value, 4);
        top.Children.Add(dot);
        top.Children.Add(name);
        top.Children.Add(field);
        top.Children.Add(kind);
        top.Children.Add(value);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(top);

        if (!string.IsNullOrEmpty(match.Message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = match.Message,
                Foreground = match.Status == DataMatchStatus.Ok ? MutedBrush : StatusBrush(match.Status),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 0, 0, 0),
            });
        }

        return new Border
        {
            Padding = new Thickness(8, 6),
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
    }

    private Control UnusedRow(DataField field)
    {
        var text = field.Name + "  =  " + DisplayValue(field.Value);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = MutedBrush,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (field.Error is not null)
            stack.Children.Add(new TextBlock
            {
                Text = field.Error,
                Foreground = ErrorBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

        return new Border
        {
            Padding = new Thickness(8, 5),
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
    }

    private static Control SectionHeader(string text) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
        Padding = new Thickness(8, 5),
        Margin = new Thickness(0, 8, 0, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedBrush,
        },
    };

    private static Control Note(string text, IBrush brush) => new Border
    {
        Padding = new Thickness(8, 6),
        Child = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    private static string DisplayValue(string value)
    {
        var single = value.Replace("\r", "").Replace("\n", " ");
        return single.Length > 80 ? single.Substring(0, 80) + "..." : single;
    }
}
