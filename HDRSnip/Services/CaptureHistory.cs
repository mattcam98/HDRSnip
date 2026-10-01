using System.IO;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;

namespace HDRSnip.Services;

/// <summary>
/// The last few captures of this session, newest first. Nothing is written to
/// disk and nothing survives exit. Only the newest keeps its full bitmap and HDR
/// frame; older ones are held as a thumbnail plus their PNG bytes, which is a
/// few megabytes each instead of sixty.
/// </summary>
public sealed class CaptureHistory
{
    private const int Capacity = 8;
    private const int ThumbnailEdge = 200;

    private readonly List<Entry> _entries = [];

    public sealed class Entry
    {
        private readonly object _gate = new();
        private CaptureResult? _full;
        private byte[]? _png;

        internal Entry(CaptureResult result)
        {
            _full = result;
            Thumbnail = ImageCodec.Thumbnail(result.Image, ThumbnailEdge);
            Width = result.Image.PixelWidth;
            Height = result.Image.PixelHeight;
            Time = DateTime.Now;
        }

        public BitmapSource Thumbnail { get; }
        public int Width { get; }
        public int Height { get; }
        public DateTime Time { get; }

        /// <summary>The capture, decoded from its PNG if the full bitmap has been let go.</summary>
        public CaptureResult Open()
        {
            lock (_gate)
            {
                if (_full is not null)
                    return _full;

                var image = new PngBitmapDecoder(
                    new MemoryStream(_png!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                image.Freeze();
                return new CaptureResult(image, WasHdr: false, SavedPath: null, _png);
            }
        }

        /// <summary>Swaps the bitmap and HDR frame for PNG bytes. Encodes on the caller's thread.</summary>
        internal void Compact()
        {
            CaptureResult? full;
            lock (_gate)
                full = _full;

            if (full is null)
                return;

            var png = full.Png ?? ImageCodec.EncodePng(full.Image);
            lock (_gate)
            {
                _png = png;
                _full = null;
            }
        }
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public Entry? Latest => _entries.Count > 0 ? _entries[0] : null;

    public void Add(CaptureResult result)
    {
        var previous = Latest;
        _entries.Insert(0, new Entry(result));
        if (_entries.Count > Capacity)
            _entries.RemoveAt(Capacity);

        if (previous is not null)
            _ = Task.Run(previous.Compact);
    }
}
