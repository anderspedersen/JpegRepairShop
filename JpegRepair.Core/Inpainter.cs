namespace JpegRepair.Core;

/// <summary>
/// Exemplar-based inpainting after Criminisi, Pérez &amp; Toyama ("Region filling and object removal by exemplar-based
/// image inpainting", IEEE TIP 2004). The hole is filled from its border inwards, one square patch at a time:
///   1. Every pixel on the fill front (a hole pixel next to a known one) gets a priority: confidence (how much of its
///      patch is known, and how reliably) × data term (how strongly an edge runs into the hole there), so that edges
///      and well-supported places are continued first.
///   2. The front pixel with the highest priority is taken; its patch is compared with every fully intact patch in a
///      window around it (sum of squared differences over the patch's known pixels, all channels), and the hole
///      pixels of the patch's centre (a smaller square, see the copy radius) are copied from the best match.
///   3. Confidence and priorities around the filled patch are updated, and the next patch is taken.
/// Copying patches carries texture and edges into the hole, which smooth interpolation would blur. Copying only the
/// centre of each patch, rather than all of it as in the paper, makes the front advance a pixel or two at a time
/// from every side: in a band one MCU row high, a whole patch matched against one side would fill most of the band
/// from that side alone and move the boundary between what is above and below it.
///
/// Works on an image of any number of channels (one float array per channel, row-major), with one state per pixel:
/// <see cref="Source"/> pixels can be copied from, <see cref="Context"/> pixels are known (they count when comparing
/// patches) but are not copied from, <see cref="Target"/> pixels are filled, <see cref="Unavailable"/> pixels are
/// ignored. Filled pixels become <see cref="Context"/>.
/// </summary>
internal sealed class Inpainter
{
    public const byte Source = 0, Context = 1, Target = 2, Unavailable = 3;

    /// <summary>Added to the data term so that flat areas are still filled in confidence order.</summary>
    private const float DataTermFloor = 0.05f;

    private readonly float[][] _channels;
    private readonly byte[] _state;
    private readonly int _width, _height, _radius, _copyRadius, _searchRadius;
    private readonly float[] _confidence;
    private readonly float[] _priority;
    private readonly bool[] _validSource;   // patch centre whose whole patch is inside and Source
    private readonly PriorityQueue<int, float> _queue = new();

    /// <param name="channels">Pixel values per channel (Width × Height each); hole pixels are overwritten.</param>
    /// <param name="state">State per pixel (<see cref="Source"/>, <see cref="Context"/>, ...); updated as pixels are filled.</param>
    /// <param name="patchRadius">Patches compared are (2 r + 1)² pixels.</param>
    /// <param name="copyRadius">Of each patch, the hole pixels within this distance of its centre are filled (at most <paramref name="patchRadius"/>).</param>
    /// <param name="searchRadius">Matches are searched for within this distance (in pixels) of the patch being filled.</param>
    public Inpainter(float[][] channels, byte[] state, int width, int height, int patchRadius, int copyRadius, int searchRadius)
    {
        _channels = channels;
        _state = state;
        _width = width;
        _height = height;
        _radius = patchRadius;
        _copyRadius = Math.Clamp(copyRadius, 0, patchRadius);
        _searchRadius = searchRadius;

        int n = width * height;
        _confidence = new float[n];
        _priority = new float[n];
        for (int p = 0; p < n; p++)
            _confidence[p] = IsKnown(p) ? 1f : 0f;

        _validSource = FindValidSources();
    }

    private bool IsKnown(int p) => _state[p] is Source or Context;

    public void Run(CancellationToken cancellation = default)
    {
        for (int p = 0; p < _state.Length; p++)
        {
            if (IsFront(p))
                Enqueue(p);
        }

        int steps = 0;
        while (_queue.TryDequeue(out int p, out float negativePriority))
        {
            if (!IsFront(p) || _priority[p] != -negativePriority)
                continue; // stale entry

            if ((++steps & 63) == 0)
                cancellation.ThrowIfCancellationRequested();

            float confidence = Confidence(p);
            int match = FindMatch(p, _searchRadius);
            if (match < 0)
                match = FindMatch(p, Math.Max(_width, _height));
            if (match < 0)
                FillFromNeighbours(p);
            else
                CopyPatch(p, match, confidence);

            UpdateAround(p);
        }

        FillLeftovers();
    }

    private bool IsFront(int p)
    {
        if (_state[p] != Target)
            return false;
        int x = p % _width, y = p / _width;
        return (x > 0 && IsKnown(p - 1)) || (x + 1 < _width && IsKnown(p + 1))
               || (y > 0 && IsKnown(p - _width)) || (y + 1 < _height && IsKnown(p + _width));
    }

    private void Enqueue(int p)
    {
        float priority = Confidence(p) * (DataTerm(p) + DataTermFloor);
        _priority[p] = priority;
        _queue.Enqueue(p, -priority);
    }

    /// <summary>Recomputes the priorities of the front pixels whose patch overlaps the patch just filled.</summary>
    private void UpdateAround(int p)
    {
        int reach = 2 * _radius + 1;
        int cx = p % _width, cy = p / _width;
        for (int y = Math.Max(0, cy - reach); y <= Math.Min(_height - 1, cy + reach); y++)
        {
            for (int x = Math.Max(0, cx - reach); x <= Math.Min(_width - 1, cx + reach); x++)
            {
                int q = y * _width + x;
                if (IsFront(q))
                    Enqueue(q);
            }
        }
    }

    /// <summary>Mean confidence over the patch (pixels outside the image count as 0).</summary>
    private float Confidence(int p)
    {
        int cx = p % _width, cy = p / _width, r = _radius;
        float sum = 0;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r); y++)
        {
            int row = y * _width;
            for (int x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                sum += _confidence[row + x];
        }

        int side = 2 * r + 1;
        return sum / (side * side);
    }

    /// <summary>
    /// |isophote · normal| / 255: the strongest gradient (of the first channel) among the patch's known pixels, turned
    /// 90° so it runs along the edge, against the normal of the fill front.
    /// </summary>
    private float DataTerm(int p)
    {
        int cx = p % _width, cy = p / _width, r = _radius;
        var luma = _channels[0];
        float bestMagnitude = 0, gx = 0, gy = 0;
        for (int y = Math.Max(1, cy - r); y <= Math.Min(_height - 2, cy + r); y++)
        {
            for (int x = Math.Max(1, cx - r); x <= Math.Min(_width - 2, cx + r); x++)
            {
                int q = y * _width + x;
                if (!IsKnown(q) || !IsKnown(q - 1) || !IsKnown(q + 1) || !IsKnown(q - _width) || !IsKnown(q + _width))
                    continue;
                float dx = (luma[q + 1] - luma[q - 1]) * 0.5f;
                float dy = (luma[q + _width] - luma[q - _width]) * 0.5f;
                float magnitude = dx * dx + dy * dy;
                if (magnitude > bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    gx = dx;
                    gy = dy;
                }
            }
        }

        if (bestMagnitude == 0)
            return 0;

        // Normal of the front: gradient of the known/unknown mask (Sobel).
        float nx = 0, ny = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= _width || y >= _height || !IsKnown(y * _width + x))
                    continue;
                int weight = dx == 0 || dy == 0 ? 2 : 1;
                nx += dx * weight;
                ny += dy * weight;
            }
        }

        float length = MathF.Sqrt(nx * nx + ny * ny);
        if (length == 0)
            return 0;

        // Isophote = gradient turned by 90°.
        return MathF.Abs(-gy * nx + gx * ny) / length / 255f;
    }

    /// <summary>
    /// Centre of the intact patch within <paramref name="searchRadius"/> that best matches the known pixels of the
    /// patch at <paramref name="p"/>, or -1. Ties go to the nearer patch.
    /// </summary>
    private int FindMatch(int p, int searchRadius)
    {
        int cx = p % _width, cy = p / _width, r = _radius;
        int channels = _channels.Length;

        // Known pixels of the patch being filled: offset from the centre and value per channel.
        var offsets = new List<int>();
        var values = new List<float>();
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= _width || y >= _height)
                    continue;
                int q = y * _width + x;
                if (!IsKnown(q))
                    continue;
                offsets.Add(dy * _width + dx);
                for (int c = 0; c < channels; c++)
                    values.Add(_channels[c][q]);
            }
        }

        var offsetArray = offsets.ToArray();
        var valueArray = values.ToArray();
        int x0 = Math.Max(r, cx - searchRadius), x1 = Math.Min(_width - 1 - r, cx + searchRadius);
        int y0 = Math.Max(r, cy - searchRadius), y1 = Math.Min(_height - 1 - r, cy + searchRadius);
        if (x1 < x0 || y1 < y0)
            return -1;

        var gate = new object();
        double best = double.MaxValue;
        long bestDistance = long.MaxValue;
        int bestCentre = -1;

        Parallel.For(y0, y1 + 1,
            () => (Cost: double.MaxValue, Distance: long.MaxValue, Centre: -1),
            (y, _, local) =>
            {
                int rowStart = y * _width;
                for (int x = x0; x <= x1; x++)
                {
                    int q = rowStart + x;
                    if (!_validSource[q])
                        continue;

                    double limit = local.Cost;
                    double cost = 0;
                    for (int i = 0; i < offsetArray.Length && cost <= limit; i++)
                    {
                        int at = q + offsetArray[i];
                        int v = i * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            float d = _channels[c][at] - valueArray[v + c];
                            cost += d * d;
                        }
                    }

                    if (cost > local.Cost)
                        continue;
                    long distance = (long)(x - cx) * (x - cx) + (long)(y - cy) * (y - cy);
                    if (cost < local.Cost || distance < local.Distance)
                        local = (cost, distance, q);
                }

                return local;
            },
            local =>
            {
                if (local.Centre < 0)
                    return;
                lock (gate)
                {
                    if (local.Cost < best || (local.Cost == best && local.Distance < bestDistance))
                    {
                        best = local.Cost;
                        bestDistance = local.Distance;
                        bestCentre = local.Centre;
                    }
                }
            });

        return bestCentre;
    }

    /// <summary>Copies the hole pixels of the centre of the patch at <paramref name="p"/> from the patch at <paramref name="source"/>.</summary>
    private void CopyPatch(int p, int source, float confidence)
    {
        int cx = p % _width, cy = p / _width, r = _copyRadius;
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= _width || y >= _height)
                    continue;
                int q = y * _width + x;
                if (_state[q] != Target)
                    continue;
                int from = source + dy * _width + dx;
                for (int c = 0; c < _channels.Length; c++)
                    _channels[c][q] = _channels[c][from];
                _state[q] = Context;
                _confidence[q] = confidence;
            }
        }
    }

    /// <summary>For when no intact patch exists at all: the pixel becomes the mean of its known neighbours.</summary>
    private void FillFromNeighbours(int p)
    {
        int cx = p % _width, cy = p / _width;
        var sums = new float[_channels.Length];
        int count = 0;
        for (int y = Math.Max(0, cy - 1); y <= Math.Min(_height - 1, cy + 1); y++)
        {
            for (int x = Math.Max(0, cx - 1); x <= Math.Min(_width - 1, cx + 1); x++)
            {
                int q = y * _width + x;
                if (!IsKnown(q))
                    continue;
                for (int c = 0; c < sums.Length; c++)
                    sums[c] += _channels[c][q];
                count++;
            }
        }

        for (int c = 0; c < sums.Length; c++)
            _channels[c][p] = count > 0 ? sums[c] / count : 128f;
        _state[p] = Context;
        _confidence[p] = Confidence(p);
    }

    /// <summary>Hole pixels that no known pixel can reach (surrounded by unavailable ones) get the mean of all known pixels.</summary>
    private void FillLeftovers()
    {
        var sums = new double[_channels.Length];
        long count = 0;
        for (int p = 0; p < _state.Length; p++)
        {
            if (!IsKnown(p))
                continue;
            for (int c = 0; c < sums.Length; c++)
                sums[c] += _channels[c][p];
            count++;
        }

        for (int p = 0; p < _state.Length; p++)
        {
            if (_state[p] != Target)
                continue;
            for (int c = 0; c < sums.Length; c++)
                _channels[c][p] = count > 0 ? (float)(sums[c] / count) : 128f;
            _state[p] = Context;
        }
    }

    /// <summary>Patch centres whose whole patch lies inside the image and consists of <see cref="Source"/> pixels.</summary>
    private bool[] FindValidSources()
    {
        int w = _width, h = _height, r = _radius;
        // Summed-area table of non-source pixels.
        var sum = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int run = 0;
            for (int x = 0; x < w; x++)
            {
                run += _state[y * w + x] == Source ? 0 : 1;
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + run;
            }
        }

        var valid = new bool[w * h];
        for (int y = r; y < h - r; y++)
        {
            for (int x = r; x < w - r; x++)
            {
                int top = y - r, left = x - r, bottom = y + r + 1, right = x + r + 1;
                int bad = sum[bottom * (w + 1) + right] - sum[top * (w + 1) + right]
                          - sum[bottom * (w + 1) + left] + sum[top * (w + 1) + left];
                valid[y * w + x] = bad == 0;
            }
        }

        return valid;
    }
}
