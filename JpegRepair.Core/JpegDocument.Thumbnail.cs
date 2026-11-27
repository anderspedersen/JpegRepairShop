namespace JpegRepair.Core;

/// <summary>
/// A preview picture embedded in the file (EXIF thumbnail, maker-note or MPF preview), registered against the top of the
/// picture: the mean level it shows over the area of every MCU position, per component. Made by
/// <see cref="JpegDocument.FindThumbnail"/>.
/// </summary>
public sealed class ThumbnailReference
{
    internal ThumbnailReference(int sourceOffset, int width, int height, double scale, double offsetX, double offsetY,
        float correlation, int components, float[] expected, bool[] inside)
    {
        SourceOffset = sourceOffset;
        Width = width;
        Height = height;
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Correlation = correlation;
        Components = components;
        Expected = expected;
        Inside = inside;
    }

    /// <summary>File offset of the preview's SOI marker.</summary>
    public int SourceOffset { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Preview pixels per picture pixel.</summary>
    public double Scale { get; }

    /// <summary>Where the picture's top-left corner lies in the preview, in preview pixels (not 0 for letterboxed previews).</summary>
    public double OffsetX { get; }

    public double OffsetY { get; }

    /// <summary>How well the stream matches the preview (median over rows of the best correlation of the brightness, whatever the shift).</summary>
    public float Correlation { get; }

    public int Components { get; }

    /// <summary>Per MCU position and component (position · <see cref="Components"/> + component): the preview's mean level over the MCU's area.</summary>
    internal float[] Expected { get; }

    /// <summary>Per MCU position: whether the MCU's area lies inside the preview's picture.</summary>
    internal bool[] Inside { get; }
}

/// <summary>
/// Embedded previews as a reference for where MCUs belong and what colour they should have.
///
/// A preview is made by the camera from the undamaged picture, so it shows, at low resolution, the brightness and
/// colour of every part of the picture. Damage to the DC prediction moves a whole run of MCUs by the same amount per
/// component, so for a run the right correction is the median difference between the preview and the MCUs' mean levels,
/// and how well a run fits a place is what is left after taking that offset away.
///
/// <see cref="FindThumbnail"/> looks for JPEG pictures inside the APP segments (each must end within its segment) and
/// after the scan (MPF previews), smaller than the picture itself, and decodes them. A camera makes its preview by
/// scaling the whole picture down, keeping the aspect ratio, so the picture is taken to be fitted inside the preview
/// (letterboxed if the shapes differ) or, failing that, to fill it (cropped). Whether a preview really shows this
/// picture is checked on the stream itself, which may be damaged from its first MCU on: for each of up to
/// <see cref="ThumbnailMatchRows"/> MCU rows of the stream, the best correlation of their brightness with the preview's
/// over every y − x the repair can use (<see cref="DpOffsetRange"/>; sideways shifts and colour offsets don't matter
/// to it); the median of these must be at least <see cref="ThumbnailMinMatch"/>. The largest preview that passes is used.
/// </summary>
public sealed partial class JpegDocument
{
    private const float ThumbnailMinMatch = 0.85f;
    private const int ThumbnailMatchRows = 64;
    private const int ThumbnailMinSize = 32;
    private const int ThumbnailSamples = 4;            // bilinear samples per MCU and direction

    /// <summary>The largest embedded preview that shows this picture (see the class remarks), or null.</summary>
    public ThumbnailReference? FindThumbnail()
    {
        int perRow = Frame.McusPerRow;
        int stream = DataMcuCount();
        if (stream < perRow || Frame.Components.Count > 3)
            return null;
        var means = McuMeans(stream);

        var previews = new List<(int Offset, int Width, int Height, float[][] Planes)>();
        foreach (var (offset, length) in EmbeddedJpegs())
        {
            Checkpoint();
            var planes = DecodePreview(File.Source.AsSpan(offset, length).ToArray(), out int width, out int height);
            if (planes != null)
                previews.Add((offset, width, height, planes));
        }

        foreach (var preview in previews.OrderByDescending(p => (long)p.Width * p.Height))
        {
            Checkpoint();
            var reference = Register(preview.Offset, preview.Width, preview.Height, preview.Planes, means);
            if (reference != null)
                return reference;
        }

        return null;
    }

    /// <summary>
    /// Corrects the colours of the picture against <paramref name="reference"/> (one undo step): every run of MCUs that
    /// follow on in the stream (broken at blanks, inserted, filled or missing MCUs, after corrupt MCUs and where the
    /// stream jumps) is shifted by the median difference between the preview and its MCUs' mean levels, if that comes
    /// to at least 4 levels. A run of at least 1½ rows' worth of MCUs whose first and last thirds disagree by 4 levels
    /// or more (Y + ½(Cb + Cr)) has a colour step inside it that the runs don't show; it is left as it is and counted
    /// in <c>Unsure</c>.
    /// </summary>
    /// <returns>The runs changed, and the runs left alone because of a colour step inside them.</returns>
    public (int Changed, int Unsure) ApplyThumbnailColours(ThumbnailReference reference)
    {
        int positions = Math.Min(_mcus.Count, Frame.McuCount);
        int n = Frame.Components.Count;
        if (reference.Components != n || positions == 0)
            return (0, 0);

        var means = McuMeans(positions);
        var difference = new float[positions * n];
        var usable = new bool[positions];
        for (int x = 0; x < positions; x++)
        {
            usable[x] = HasPictureData(_mcus[x]) && reference.Inside[x];
            if (!usable[x])
                continue;
            for (int c = 0; c < n; c++)
                difference[x * n + c] = reference.Expected[x * n + c] - means[x * n + c];
        }

        var parts = new List<Edit>();
        int start = 0, unsure = 0;
        for (int x = 1; x <= positions; x++)
        {
            if (x < positions && !RunBreak(x))
                continue;
            if (HasColourStep(start, x - 1, difference, usable, n))
            {
                unsure++;
            }
            else
            {
                var deltas = RunColourFix(start, x - 1, difference, usable, n);
                if (deltas != null)
                    parts.Add(new RangeDcEdit(start, x - 1, deltas));
            }

            start = x;
        }

        if (parts.Count > 0)
            Apply(new CompositeEdit($"Colours from the embedded preview ({parts.Count} runs)", parts.ToArray()));
        return (parts.Count, unsure);
    }

    private static bool HasPictureData(McuEntry entry) =>
        !entry.IsInserted && entry.Status is McuStatus.Ok or McuStatus.Corrupt;

    /// <summary>Whether a run of MCUs with one colour offset ends before position <paramref name="x"/>.</summary>
    private bool RunBreak(int x)
    {
        var before = _mcus[x - 1];
        var entry = _mcus[x];
        return !HasPictureData(before) || !HasPictureData(entry) || before.Status == McuStatus.Corrupt ||
               entry.SourceIndex != before.SourceIndex + 1;
    }

    /// <summary>
    /// Whether the run <paramref name="first"/>..<paramref name="last"/> has a colour step inside it: it has at least
    /// 1½ rows' worth of usable MCUs and the median difference to the preview over its first third and over its last
    /// third differ by at least 4 levels (Y + ½(Cb + Cr)).
    /// </summary>
    private bool HasColourStep(int first, int last, float[] difference, bool[] usable, int n)
    {
        var indices = new List<int>();
        for (int x = first; x <= last; x++)
        {
            if (usable[x])
                indices.Add(x);
        }

        int third = indices.Count / 3;
        if (third < Frame.McusPerRow / 2 || third == 0)
            return false;

        var values = new float[third];
        float size = 0;
        for (int c = 0; c < n; c++)
        {
            for (int k = 0; k < third; k++)
                values[k] = difference[indices[k] * n + c];
            float head = Median(values);
            for (int k = 0; k < third; k++)
                values[k] = difference[indices[indices.Count - third + k] * n + c];
            float tail = Median(values);
            size += (c == 0 ? 1f : 0.5f) * Math.Abs(tail - head);
        }

        return size >= MinColourShiftLevels;
    }

    /// <summary>DC deltas that move the run to the preview's colours, or null if it is less than 4 levels off or has no usable MCU.</summary>
    private int[]? RunColourFix(int first, int last, float[] difference, bool[] usable, int n)
    {
        var values = new List<float>();
        var deltas = new int[n];
        float levels = 0;
        for (int c = 0; c < n; c++)
        {
            values.Clear();
            for (int x = first; x <= last; x++)
            {
                if (usable[x])
                    values.Add(difference[x * n + c]);
            }

            if (values.Count == 0)
                return null;
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values);
            float offset = Median(span);
            float step = _quant[c][0] / 8f;
            deltas[c] = (int)MathF.Round(offset / step);
            levels = Math.Max(levels, Math.Abs(deltas[c]) * step);
        }

        return levels < MinColourShiftLevels ? null : deltas;
    }

    /// <summary>Median of <paramref name="values"/> (mean of the middle two for an even count); sorts them in place.</summary>
    private static float Median(Span<float> values)
    {
        values.Sort();
        int count = values.Length;
        return count % 2 == 1 ? values[count / 2] : 0.5f * (values[count / 2 - 1] + values[count / 2]);
    }

    /// <summary>
    /// Mean level of every component over each of the first <paramref name="count"/> MCUs, from the DC coefficients
    /// (position · components + component; not clamped to 0..255).
    /// </summary>
    private float[] McuMeans(int count)
    {
        int n = Frame.Components.Count;
        var blocks = Layout.Blocks;
        var perComponent = new int[n];
        foreach (var block in blocks)
            perComponent[block.ComponentIndex]++;
        var scale = new float[n];
        for (int c = 0; c < n; c++)
            scale[c] = _quant[c][0] / (8f * perComponent[c]);

        var means = new float[count * n];
        Parallel.For(0, count, i =>
        {
            var coefficients = _mcus[i].Coefficients;
            for (int c = 0; c < n; c++)
                means[i * n + c] = 128;
            for (int b = 0; b < blocks.Count; b++)
            {
                int c = blocks[b].ComponentIndex;
                means[i * n + c] += coefficients[b * 64] * scale[c];
            }
        });
        return means;
    }

    /// <summary>
    /// JPEG pictures embedded in the file (offset, length): inside the payload of every APP segment, each ending within
    /// it, and after the scan. Bytes that only look like an SOI (for example the end of one segment followed by the
    /// next) lead into the picture's own headers and scan, which these limits leave out.
    /// </summary>
    private List<(int Offset, int Length)> EmbeddedJpegs()
    {
        var source = File.Source;
        var found = new List<(int, int)>();
        void Search(int from, int to)
        {
            to = Math.Min(to, source.Length);
            for (int i = Math.Max(0, from); i + 3 < to; i++)
            {
                if (source[i] != 0xFF || source[i + 1] != Markers.SOI || source[i + 2] != 0xFF)
                    continue;
                int end = EmbeddedJpegEnd(source, i);
                if (end > i && end <= to)
                {
                    found.Add((i, end - i));
                    i = end - 1;
                }
            }
        }

        foreach (var segment in File.Segments)
        {
            if (segment.Marker is >= Markers.APP0 and <= Markers.APP15 && segment.Length > 2)
                Search(segment.Offset + 4, segment.Offset + 2 + segment.Length);
        }

        Search(File.Entropy.SourceEnd, source.Length);
        return found;
    }

    /// <summary>
    /// The end (just after EOI) of the JPEG picture starting at <paramref name="start"/>, found by walking its segments
    /// and scans, or −1 if it doesn't hold a complete picture.
    /// </summary>
    private static int EmbeddedJpegEnd(byte[] source, int start)
    {
        int i = start + 2;
        bool scanned = false;
        while (i + 3 < source.Length)
        {
            if (source[i] != 0xFF)
                return -1;
            byte marker = source[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker == Markers.EOI)
                return scanned ? i + 2 : -1;
            if (Markers.IsRst(marker) || marker == 0x01)
            {
                i += 2;
                continue;
            }

            int length = (source[i + 2] << 8) | source[i + 3];
            if (length < 2)
                return -1;
            i += 2 + length;
            if (marker != Markers.SOS)
                continue;

            // Entropy-coded data up to the next marker that isn't stuffing or a restart marker.
            scanned = true;
            while (i + 1 < source.Length && !(source[i] == 0xFF && source[i + 1] != 0x00 && source[i + 1] != 0xFF &&
                                              !Markers.IsRst(source[i + 1])))
                i++;
        }

        return -1;
    }

    /// <summary>Decodes a preview into planes Y, Cb, Cr (or Y alone for a greyscale picture), or null if it can't be used.</summary>
    private float[][]? DecodePreview(byte[] data, out int width, out int height)
    {
        width = height = 0;
        byte[] bgra;
        try
        {
            var file = JpegFile.Parse(data);
            int w = file.Frame.Width, h = file.Frame.Height;
            if (w < ThumbnailMinSize || h < ThumbnailMinSize || (long)w * h >= (long)Frame.Width * Frame.Height)
                return null;
            var scan = ScanDecoder.Decode(file);
            bgra = new JpegRenderer(file, scan.Layout, scan).RenderBgra();
            width = w;
            height = h;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything that isn't a picture this decoder can read (other formats, progressive, damaged) is skipped.
            return null;
        }

        int n = Frame.Components.Count == 1 ? 1 : 3;
        var planes = new float[n][];
        for (int c = 0; c < n; c++)
            planes[c] = new float[width * height];
        for (int i = 0; i < width * height; i++)
        {
            float b = bgra[4 * i], g = bgra[4 * i + 1], r = bgra[4 * i + 2];
            planes[0][i] = 0.299f * r + 0.587f * g + 0.114f * b;
            if (n == 3)
            {
                planes[1][i] = 128 - 0.168736f * r - 0.331264f * g + 0.5f * b;
                planes[2][i] = 128 + 0.5f * r - 0.418688f * g - 0.081312f * b;
            }
        }

        return planes;
    }

    /// <summary>
    /// Places the preview on the picture (fitted inside, else filling it; see the class remarks) and checks that it shows
    /// this picture. Returns null if neither fit reaches <see cref="ThumbnailMinMatch"/>.
    /// </summary>
    private ThumbnailReference? Register(int sourceOffset, int width, int height, float[][] planes, float[] means)
    {
        var frame = Frame;
        int n = frame.Components.Count;
        if (planes.Length != Math.Min(n, 3))
            return null;

        double inside = Math.Min((double)width / frame.Width, (double)height / frame.Height);
        double fill = Math.Max((double)width / frame.Width, (double)height / frame.Height);
        ThumbnailReference? best = null;
        foreach (double scale in fill > inside * 1.001 ? new[] { inside, fill } : new[] { inside })
        {
            Checkpoint();
            double x = (width - frame.Width * scale) / 2, y = (height - frame.Height * scale) / 2;
            int positions = frame.McuCount;
            var expected = new float[positions * n];
            var within = new bool[positions];
            Parallel.For(0, positions, () => new float[n], (p, _, values) =>
            {
                within[p] = PreviewMean(planes, n, width, height, scale, x, y, p, values);
                values.CopyTo(expected, p * n);
                return values;
            }, _ => { });

            float match = RowMatch(expected, within, means, n);
            if (match >= ThumbnailMinMatch && (best == null || match > best.Correlation))
                best = new ThumbnailReference(sourceOffset, width, height, scale, x, y, match, n, expected, within);
        }

        return best;
    }

    /// <summary>
    /// How well the stream matches the preview's brightness whatever its shifts (see the class remarks): the median over
    /// up to <see cref="ThumbnailMatchRows"/> stream rows of the best correlation over every y − x the repair can use.
    /// </summary>
    private float RowMatch(float[] expected, bool[] within, float[] means, int n)
    {
        int perRow = Frame.McusPerRow;
        int positions = within.Length;
        int rows = means.Length / n / perRow;
        if (rows == 0)
            return 0;
        var (offMin, offMax) = DpOffsetRange();
        int picked = Math.Min(ThumbnailMatchRows, rows);
        var best = new float[picked];
        Parallel.For(0, picked, k =>
        {
            int row = picked == 1 ? 0 : (int)((long)k * (rows - 1) / (picked - 1));
            var ys = new List<int>(perRow);
            for (int y = row * perRow; y < (row + 1) * perRow; y++)
            {
                if (HasPictureData(_mcus[y]) && _mcus[y].Status == McuStatus.Ok)
                    ys.Add(y);
            }

            float top = -1;
            if (ys.Count >= perRow / 2)
            {
                for (int off = offMin; off <= offMax; off++)
                {
                    double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                    int m = 0;
                    foreach (int y in ys)
                    {
                        int x = y - off;
                        if (x < 0 || x >= positions || !within[x])
                            continue;
                        double a = expected[x * n], b = means[y * n];
                        sa += a;
                        sb += b;
                        saa += a * a;
                        sbb += b * b;
                        sab += a * b;
                        m++;
                    }

                    if (m < perRow / 2)
                        continue;
                    double va = m * saa - sa * sa, vb = m * sbb - sb * sb;
                    if (va <= 0 || vb <= 0)
                        continue;
                    top = Math.Max(top, (float)((m * sab - sa * sb) / Math.Sqrt(va * vb)));
                }
            }

            best[k] = top;
        });

        var found = best.Where(b => b > -1).ToArray();
        if (found.Length == 0)
            return 0;
        return Median(found);
    }

    /// <summary>
    /// The preview's mean level of each of the first <paramref name="components"/> planes over the visible area of MCU
    /// position <paramref name="position"/> (<see cref="ThumbnailSamples"/>² bilinear samples). Picture pixel (u, v)
    /// lies at (u · scale + x, v · scale + y) in the preview, pixel centres at half-integers. False if the area isn't
    /// inside the preview.
    /// </summary>
    private bool PreviewMean(float[][] planes, int components, int width, int height, double scale, double x, double y,
        int position, Span<float> values)
    {
        var frame = Frame;
        values[..components].Clear();
        int column = position % frame.McusPerRow, row = position / frame.McusPerRow;
        double left = (double)column * frame.McuWidth, top = (double)row * frame.McuHeight;
        double areaWidth = Math.Min(frame.McuWidth, frame.Width - left);
        double areaHeight = Math.Min(frame.McuHeight, frame.Height - top);
        if (areaWidth <= 0 || areaHeight <= 0)
            return false;

        const int k = ThumbnailSamples;
        double firstX = (left + 0.5 * areaWidth / k) * scale + x - 0.5;
        double lastX = (left + (k - 0.5) * areaWidth / k) * scale + x - 0.5;
        double firstY = (top + 0.5 * areaHeight / k) * scale + y - 0.5;
        double lastY = (top + (k - 0.5) * areaHeight / k) * scale + y - 0.5;
        if (firstX < -0.5 || firstY < -0.5 || lastX > width - 0.5 || lastY > height - 0.5)
            return false;

        for (int j = 0; j < k; j++)
        {
            double ty = (top + (j + 0.5) * areaHeight / k) * scale + y - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(ty), 0, height - 2);
            float fy = (float)Math.Clamp(ty - y0, 0, 1);
            for (int i = 0; i < k; i++)
            {
                double tx = (left + (i + 0.5) * areaWidth / k) * scale + x - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(tx), 0, width - 2);
                float fx = (float)Math.Clamp(tx - x0, 0, 1);
                int at = y0 * width + x0;
                for (int c = 0; c < components; c++)
                {
                    var plane = planes[c];
                    float topValue = plane[at] * (1 - fx) + plane[at + 1] * fx;
                    float bottomValue = plane[at + width] * (1 - fx) + plane[at + width + 1] * fx;
                    values[c] += topValue * (1 - fy) + bottomValue * fy;
                }
            }
        }

        for (int c = 0; c < components; c++)
            values[c] /= k * k;
        return true;
    }

}
