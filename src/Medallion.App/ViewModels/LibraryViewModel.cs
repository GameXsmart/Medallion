using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Medallion.Core.Clips;
using Medallion.Core.Diagnostics;

namespace Medallion.App.ViewModels;

/// <summary>One clip card in the library.</summary>
public sealed class ClipItemViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;
    private ImageSource? _thumbnail;
    private bool _isRenaming;
    private string _editName;

    public ClipItemViewModel(ClipInfo clip, LibraryViewModel owner)
    {
        Clip = clip;
        _owner = owner;
        _editName = clip.FileName;

        PlayCommand = new RelayCommand(() => ClipLibrary.Play(Clip.FilePath));
        RevealCommand = new RelayCommand(() => ClipLibrary.RevealInExplorer(Clip.FilePath));
        BeginRenameCommand = new RelayCommand(() => { EditName = Clip.FileName; IsRenaming = true; });
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        CommitRenameCommand = new RelayCommand(CommitRename);
        DeleteCommand = new RelayCommand(() => _owner.Delete(this));
    }

    public ClipInfo Clip { get; }

    public RelayCommand PlayCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand CommitRenameCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public string Name => Clip.FileName;
    public string Meta => $"{Clip.DisplayDate}    ·    {Clip.DurationLabel}    ·    " +
                          $"{Clip.ResolutionLabel}    ·    {Clip.FpsLabel}    ·    {Clip.SizeLabel}";

    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    /// <summary>Loads the thumbnail decoded to display size so the list stays light.</summary>
    public void LoadThumbnail()
    {
        try
        {
            if (Clip.ThumbnailPath is null || !File.Exists(Clip.ThumbnailPath)) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Clip.ThumbnailPath);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 240;
            image.EndInit();
            image.Freeze();

            Thumbnail = image;
        }
        catch (Exception ex)
        {
            Log.Debug($"Thumbnail load failed: {ex.Message}");
        }
    }

    private void CommitRename()
    {
        IsRenaming = false;
        if (string.Equals(EditName, Clip.FileName, StringComparison.Ordinal)) return;

        if (_owner.Rename(this, EditName))
        {
            Raise(nameof(Name));
            Raise(nameof(Meta));
        }
    }
}

public enum ClipSort { Newest, Oldest, Largest, Longest }

public sealed class LibraryViewModel : ObservableObject
{
    private readonly List<ClipItemViewModel> _all = new();
    private bool _isEmpty = true;
    private string _summary = string.Empty;
    private string _search = string.Empty;
    private Choice<ClipSort>? _sort;

    public LibraryViewModel()
    {
        SortOptions = new[]
        {
            new Choice<ClipSort>(ClipSort.Newest, "Newest first"),
            new Choice<ClipSort>(ClipSort.Oldest, "Oldest first"),
            new Choice<ClipSort>(ClipSort.Largest, "Largest first"),
            new Choice<ClipSort>(ClipSort.Longest, "Longest first")
        };
        _sort = SortOptions[0];

        RefreshCommand = new RelayCommand(Refresh);
        OpenFolderCommand = new RelayCommand(() =>
            ClipLibrary.RevealInExplorer(Path.Combine(App.Settings.SaveDirectory, "_")));
    }

    public ObservableCollection<ClipItemViewModel> Clips { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public IReadOnlyList<Choice<ClipSort>> SortOptions { get; }

    /// <summary>Free-text filter over clip names. Applied without re-reading the folder.</summary>
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyView(); }
    }

    public Choice<ClipSort>? Sort
    {
        get => _sort;
        set { if (Set(ref _sort, value)) ApplyView(); }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public void Refresh()
    {
        try
        {
            _all.Clear();

            foreach (var clip in App.Library.Scan(App.Settings.SaveDirectory, App.Engine.FfprobePath, App.Engine.FfmpegPath))
            {
                var item = new ClipItemViewModel(clip, this);
                item.LoadThumbnail();
                _all.Add(item);
            }

            ApplyView();
        }
        catch (Exception ex)
        {
            Log.Error("Library refresh failed", ex);
            Summary = "Could not read the clips folder";
        }
    }

    /// <summary>Re-projects the cached scan through the current filter and sort order.</summary>
    private void ApplyView()
    {
        IEnumerable<ClipItemViewModel> view = _all;

        if (!string.IsNullOrWhiteSpace(_search))
            view = view.Where(c => c.Name.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase));

        view = (_sort?.Value ?? ClipSort.Newest) switch
        {
            ClipSort.Oldest => view.OrderBy(c => c.Clip.CreatedUtc),
            ClipSort.Largest => view.OrderByDescending(c => c.Clip.FileSizeBytes),
            ClipSort.Longest => view.OrderByDescending(c => c.Clip.DurationSeconds),
            _ => view.OrderByDescending(c => c.Clip.CreatedUtc)
        };

        Clips.Clear();
        foreach (var item in view) Clips.Add(item);

        long totalBytes = _all.Sum(c => c.Clip.FileSizeBytes);
        IsEmpty = _all.Count == 0;

        Summary = _all.Count == 0
            ? "No clips yet"
            : Clips.Count == _all.Count
                ? $"{_all.Count} clip{(_all.Count == 1 ? "" : "s")}  ·  {totalBytes / (1024.0 * 1024.0 * 1024.0):0.00} GB"
                : $"{Clips.Count} of {_all.Count} clips";
    }

    public bool Rename(ClipItemViewModel item, string newName)
    {
        var result = App.Library.Rename(item.Clip, newName);
        if (result is null) return false;

        // The thumbnail is keyed on the old name; regenerate lazily on next refresh.
        return true;
    }

    public void Delete(ClipItemViewModel item)
    {
        if (App.Library.Delete(item.Clip))
        {
            _all.Remove(item);
            ApplyView();
        }
    }
}
