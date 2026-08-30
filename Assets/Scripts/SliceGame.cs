using System.Collections.Generic;
using UnityEngine;

// Iteration 4 — jars with capacity + a queue ("layers") and clog risk.
//   * Picture is built from any texture (per-pixel color).
//   * SPACE / two-finger tap releases the whole picture; swipe cuts a chunk.
//   * Pixels funnel into a central tube, then route to one of two ACTIVE jars
//     by nearest color (only if that jar still has capacity).
//   * Each jar has a random small capacity and a random color drawn from the
//     texture palette. A number on the jar shows how many pixels it still needs.
//   * When a jar is full its pixels are consumed and the next jar from the queue
//     slides into that slot. The next layer is shown as a
//     preview row with their colors and numbers.
//   * A pixel with no matching active jar piles up in the tube. If the tube
//     fills to the top it CLOGS — the fail state the player must avoid.
// Pixels are NOT GameObjects; ParticleSystem is used purely as a batch renderer
// fed by a small hand-written 2D simulation.
[DisallowMultipleComponent]
public class SliceGame : MonoBehaviour
{
    [Header("Source")]
    public Texture2D sourceTexture;   // assign any texture; null => generated default
    public int maxResolution = 48;

    [Header("Seed")]
    public bool useFixedSeed = false; // if true, the level is built from `seed` every run
    public int seed = 0;              // the fixed seed to use when useFixedSeed is on

    [Header("Tuning")]
    public float gravity = 16f;
    public float funnelSteer = 9f;
    public float jarSteer = 13f;
    public int capacityMin = 5;
    public int capacityMax = 15;
    public float colorMatch = 0.30f;  // max color distance a jar will accept

    [Header("Color grouping")]
    // How close two colors must be to count as the same color for sorting.
    [Range(0.05f, 0.8f)] public float colorMergeDistance = 0.25f;
    // How much lightness matters next to hue. Lower = a dark shade and a bright
    // shade of one hue fold together more eagerly.
    [Range(0f, 1f)] public float lightnessWeight = 0.5f;
    // Hard cap on color families. Once this many are left, merging keeps going
    // even between colors that are NOT alike — set it too low and reds, oranges
    // and yellows get crushed into one jar.
    [Range(2, 16)] public int maxColorFamilies = 10;
    public int tubeCapacity = 0;      // 0 = auto (fills tube geometry)

    [Header("Pixel look")]
    public Texture2D glossTexture;                       // stamped additively over every pixel
    [Range(0f, 2f)] public float glossStrength = 1f;     // 0 = off, 1 = as authored
    // Cut the pixel quads to the gloss texture's own silhouette, so the rounded
    // highlight isn't sitting on a square. Same asset = corners always match.
    public bool roundedPixels = true;

    [Header("Cut")]
    public float fingerPixels = 3.6f; // kept for existing scene tuning; cut width in pixel-widths
    public float eraseImpulse = 4f;   // how hard chipped pixels fly out
    public float cutGuideWidthPixels = 0.22f;
    public float cutGuideDashPixels = 0.85f;
    public float cutGuideGapPixels = 0.55f;

    const float ORTHO_SIZE = 5f;
    const float MIN_SWIPE = 0.2f;
    const int ACTIVE_JARS = 3;
    const int CUT_GUIDE_SORT_ORDER = 100;
    const string GLOSS_SHADER = "SawPixel/PixelGloss";
    const string SHAPE_SHADER = "SawPixel/PixelShape";

    static readonly Color FRAME_COLOR = new Color(0.9f, 0.9f, 0.9f, 1f);
    static readonly Color BG_COLOR = new Color(0.96f, 0.96f, 0.86f, 1f);
    static readonly Color MACHINE_COLOR = new Color(0.55f, 0.58f, 0.65f, 1f);
    static readonly Color CUT_GUIDE_COLOR = new Color(1f, 0f, 0f, 1f);

    protected struct Px { public Vector3 pos; public Color col; public int pi; public int x, y; }   // pi = palette index

    protected class Faller
    {
        public Vector3 pos;
        public float vx;       // horizontal velocity (impulse pop in the picture zone)
        public float vy;
        public Color col;
        public int pi;         // palette index this pixel belongs to
        public int jar;        // active jar index
        public bool routed;    // has passed the tube entry decision point
        public bool inTube;    // parked in the tube buffer, waiting for a free jar
        public bool landed;
        public bool consumed;  // jar completed and ate this pixel; swept out after the loop
    }

    class DetachedChunk
    {
        public readonly List<Px> pixels;
        public float vy;

        public DetachedChunk(List<Px> pixels) { this.pixels = pixels; }
    }

    struct JarDef { public int pi; public Color color; public int capacity; }

    class Slot
    {
        public int pi;         // palette index this jar accepts (-1 = empty/inactive)
        public Color color;
        public int capacity;
        public int reserved;   // assigned (in-flight + landed)
        public int landed;     // settled in the jar
        public LineRenderer box;
        public TextMesh text;
    }

    ParticleSystem hangingPS, fallingPS;
    ParticleSystem hangingGlossPS, fallingGlossPS;
    bool glossOn;
    ParticleSystem cutGuidePS;
    protected readonly List<Px> hanging = new List<Px>();
    protected readonly List<Faller> fallers = new List<Faller>();
    readonly List<DetachedChunk> detachedChunks = new List<DetachedChunk>();
    ParticleSystem.Particle[] buf = new ParticleSystem.Particle[256];
    ParticleSystem.Particle[] cutBuf = new ParticleSystem.Particle[256];

    protected Camera cam;
    protected float W, H, pixel;
    protected int texW, texH;

    protected float funnelTopY, tubeTopY, tubeBotY, jarTopY, jarBottomY, pictureCenterY, previewY;
    protected float tubeHalfW, funnelHalfW, jarInnerHalfW, previewHalfW, previewHalfH;
    protected float pictureZoneW, pictureZoneH;
    protected float eraseRadius;
    readonly float[] jarCenterX = new float[ACTIVE_JARS];
    int jarPerRow, backlogPerRow, backlogCapacity;

    readonly List<Color> palette = new List<Color>();
    readonly List<Vector3> palettePoints = new List<Vector3>();
    readonly Dictionary<Color, int> shadeFamily = new Dictionary<Color, int>();
    int[] paletteCount;
    readonly Slot[] slots = new Slot[ACTIVE_JARS];
    readonly Queue<JarDef> queue = new Queue<JarDef>();
    LineRenderer[] previewBox = new LineRenderer[ACTIVE_JARS];
    TextMesh[] previewText = new TextMesh[ACTIVE_JARS];
    LineRenderer cutStartRing, cutCurrentRing;
    bool cutting;
    Vector3 cutStart, cutCurrent;

    protected bool clogged;
    protected TextMesh statusText;
    protected Font uiFont;

    int activeSeed;                   // the seed the current level was actually built with
    GUIStyle seedLabelStyle, seedBtnStyle;

    public int CurrentSeed => activeSeed;

    protected virtual void Start()
    {
        InitSeed();
        Build();
    }

    // Pick the seed for this run and lock Unity's RNG to it, so the whole level
    // generation (jar capacities + shuffle) is fully reproducible.
    void InitSeed()
    {
        activeSeed = useFixedSeed ? seed : new System.Random().Next();
        Random.InitState(activeSeed);
    }

    protected virtual void Build()
    {
        SetupCamera();
        ComputeFrameBounds();
        var cols = LoadColors(out texW, out texH);
        ComputeLayout();
        LoadFont();
        BuildFrameVisual();
        BuildParticleSystems();
        BuildPicture(cols);
        ExtractPalette();
        AssignPaletteIndices();
        BuildMachineVisual();
        BuildJarsAndQueue();
        BuildCutPreviewVisual();
        UploadHanging();
    }

    // Restart the level from scratch. Pass a seed to force it; omit to roll a new
    // random one. Tears the spawned visuals down and rebuilds in place (no scene
    // reload), so it works while playing.
    public void Restart(int? withSeed = null)
    {
        if (withSeed.HasValue) { useFixedSeed = true; seed = withSeed.Value; }
        Teardown();
        InitSeed();
        Build();
    }

    protected virtual void Teardown()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        hanging.Clear();
        fallers.Clear();
        detachedChunks.Clear();
        queue.Clear();
        palette.Clear();
        palettePoints.Clear();
        shadeFamily.Clear();

        cutting = false;
        clogged = false;
        sweepConsumed = false;
        statusText = null;
        cutStartRing = cutCurrentRing = null;
        cutGuidePS = hangingPS = fallingPS = null;
        hangingGlossPS = fallingGlossPS = null;
        glossOn = false;
        for (int i = 0; i < ACTIVE_JARS; i++) { slots[i] = null; previewBox[i] = null; previewText[i] = null; }
    }

    // ---- on-screen seed overlay ----------------------------------------

    void OnGUI()
    {
        if (seedLabelStyle == null)
        {
            seedLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            seedBtnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }

        const float pad = 10f;
        GUI.Label(new Rect(pad, pad, 360f, 24f), "Seed: " + activeSeed, seedLabelStyle);
        if (GUI.Button(new Rect(pad, pad + 26f, 90f, 26f), "Copy", seedBtnStyle))
            GUIUtility.systemCopyBuffer = activeSeed.ToString();
    }

    // ---- setup ----------------------------------------------------------

    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
        }
        cam.orthographic = true;
        cam.orthographicSize = ORTHO_SIZE;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = BG_COLOR;
    }

    void ComputeFrameBounds()
    {
        H = ORTHO_SIZE * 0.92f;
        W = ORTHO_SIZE * cam.aspect * 0.92f;
    }

    // Vertical bands of the machine + the picture zone size. Split out so a
    // variation can move things around without copying the derived math below.
    protected virtual void ConfigureBands()
    {
        funnelTopY = 0.35f * H;
        tubeTopY = 0.14f * H;
        tubeBotY = -0.04f * H;
        jarTopY = -0.20f * H;
        jarBottomY = -0.66f * H;
        previewY = -0.84f * H;
        pictureCenterY = 0.62f * H;

        pictureZoneH = 0.55f * H;
        pictureZoneW = 1.6f * W;
    }

    protected virtual void ComputeLayout()
    {
        ConfigureBands();
        pixel = Mathf.Min(pictureZoneH / Mathf.Max(1, texH), pictureZoneW / Mathf.Max(1, texW));

        tubeHalfW = Mathf.Max(pixel * 3f, 0.08f * W);
        funnelHalfW = 0.45f * W;
        float jarSpan = 0.92f * W;                       // total width the jar row may use
        float jarSlotW = 2f * jarSpan / ACTIVE_JARS;
        jarInnerHalfW = jarSlotW * 0.42f;                // 8% gap between neighbours
        for (int i = 0; i < ACTIVE_JARS; i++)
            jarCenterX[i] = -jarSpan + jarSlotW * (i + 0.5f);
        jarPerRow = Mathf.Max(1, Mathf.FloorToInt(2f * jarInnerHalfW / pixel));

        previewHalfW = 0.14f * W;
        previewHalfH = 0.07f * H;

        backlogPerRow = Mathf.Max(1, Mathf.FloorToInt(2f * tubeHalfW / pixel));
        int autoCapacity = backlogPerRow * Mathf.Max(2, Mathf.FloorToInt((tubeTopY - tubeBotY) / pixel));
        backlogCapacity = tubeCapacity > 0 ? tubeCapacity : autoCapacity;

        eraseRadius = pixel * fingerPixels;
    }

    void LoadFont()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (uiFont == null) uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    // ---- texture loading ------------------------------------------------

    Color[] LoadColors(out int w, out int h)
    {
        if (sourceTexture != null) return ReadViaBlit(sourceTexture, out w, out h);
        return GenerateDefault(out w, out h);
    }

    // Read the source 1:1 into a per-pixel color grid. Nearest-neighbour only:
    // we want crisp pixels, never bilinear blur. Aspect ratio is preserved — the
    // grid keeps the texture's real width/height; game scaling happens later via
    // the uniform `pixel` size, not by squashing the grid.
    Color[] ReadViaBlit(Texture2D src, out int w, out int h)
    {
        int sw = src.width, sh = src.height;
        float scale = Mathf.Min(1f, (float)maxResolution / Mathf.Max(sw, sh));
        w = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
        h = Mathf.Max(1, Mathf.RoundToInt(sh * scale));

        // force point sampling on the source so the blit can't introduce
        // half-tone interpolation, regardless of the texture's import filter.
        var prevFilter = src.filterMode;
        src.filterMode = FilterMode.Point;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rt.filterMode = FilterMode.Point;
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        src.filterMode = prevFilter;
        var cols = tmp.GetPixels();
        Destroy(tmp);
        return cols;
    }

    Color[] GenerateDefault(out int w, out int h)
    {
        w = 32; h = 32;
        var c = new Color[w * h];
        Color red = new Color(0.90f, 0.20f, 0.20f);
        Color green = new Color(0.25f, 0.80f, 0.35f);
        Color blue = new Color(0.25f, 0.55f, 0.95f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                c[y * w + x] = x < w / 3 ? red : (x < 2 * w / 3 ? green : blue);
        return c;
    }

    void BuildPicture(Color[] cols)
    {
        hanging.Clear();
        for (int y = 0; y < texH; y++)
            for (int x = 0; x < texW; x++)
            {
                Color col = cols[y * texW + x];
                if (col.a < 0.5f) continue;
                col.a = 1f;
                hanging.Add(new Px { pos = PixelToWorld(x, y), col = col, x = x, y = y });
            }
    }

    protected Vector3 PixelToWorld(int x, int y)
    {
        float wx = (x - (texW - 1) * 0.5f) * pixel;
        float wy = (y - (texH - 1) * 0.5f) * pixel + pictureCenterY;
        return new Vector3(wx, wy, 0f);
    }

    // ---- palette --------------------------------------------------------

    // Colors live on a cone: angle = hue, radius = saturation, height = lightness.
    // Distances there behave the way people talk about color — two shades of red
    // sit next to each other, red and orange do not — and grays collapse onto the
    // axis instead of picking up a meaningless hue.
    Vector3 ColorPoint(Color c)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        float a = h * Mathf.PI * 2f;
        return new Vector3(Mathf.Cos(a) * s, Mathf.Sin(a) * s, v * lightnessWeight);
    }

    class Family
    {
        public Vector3 center;
        public Color dominant;                      // most common real shade — this is what the jar shows
        public int weight;
        public readonly List<int> members = new List<int>();   // indices into the distinct-shade list
    }

    // Fold every shade in the picture into a handful of color families, so hand
    // shading, anti-aliasing and JPEG mush all sort into one jar while genuinely
    // different colors stay apart. Pixels keep their own shade on screen — only
    // the sorting is grouped.
    //
    // Two passes: a greedy sweep that swallows near-duplicates, then agglomerative
    // merging of the closest families. Merging stops once everything left is far
    // enough apart — or, if there are still too many, once only maxColorFamilies remain.
    // The second pass is what lets a chain of shades (bright red -> mid -> dark)
    // end up in one family even though the ends are far apart.
    void ExtractPalette()
    {
        palette.Clear();
        palettePoints.Clear();
        shadeFamily.Clear();

        var tally = new Dictionary<Color, int>();
        foreach (var p in hanging) tally[p.col] = tally.TryGetValue(p.col, out int n) ? n + 1 : 1;
        if (tally.Count == 0)
        {
            AddPaletteEntry(Color.red);
            AddPaletteEntry(Color.green);
            AddPaletteEntry(Color.blue);
            return;
        }

        var shades = new List<Color>(tally.Keys);
        shades.Sort((a, b) => tally[b].CompareTo(tally[a]));      // most common first
        var points = new List<Vector3>(shades.Count);
        foreach (var c in shades) points.Add(ColorPoint(c));

        var families = SeedFamilies(shades, points, tally);
        MergeFamilies(families, shades, points, tally);
        families.Sort((a, b) => b.weight.CompareTo(a.weight));

        for (int i = 0; i < families.Count; i++)
        {
            AddPaletteEntry(families[i].dominant, families[i].center);
            foreach (int m in families[i].members) shadeFamily[shades[m]] = i;
        }
    }

    void AddPaletteEntry(Color c) => AddPaletteEntry(c, ColorPoint(c));

    void AddPaletteEntry(Color c, Vector3 point)
    {
        palette.Add(c);
        palettePoints.Add(point);
    }

    // Pass 1: walk the shades most-common-first, dropping each into the first
    // family it is practically identical to. Kills noise before the real work.
    List<Family> SeedFamilies(List<Color> shades, List<Vector3> points, Dictionary<Color, int> tally)
    {
        var families = new List<Family>();
        float near2 = Mathf.Pow(colorMergeDistance * 0.4f, 2f);

        for (int i = 0; i < shades.Count; i++)
        {
            int hit = -1;
            for (int k = 0; k < families.Count; k++)
                if ((families[k].center - points[i]).sqrMagnitude <= near2) { hit = k; break; }

            if (hit < 0)
            {
                var f = new Family { center = points[i], dominant = shades[i] };
                f.members.Add(i);
                families.Add(f);
            }
            else families[hit].members.Add(i);
        }

        foreach (var f in families) Recenter(f, shades, points, tally);
        return families;
    }

    // Pass 2: repeatedly glue together the two closest families.
    void MergeFamilies(List<Family> families, List<Color> shades, List<Vector3> points,
                       Dictionary<Color, int> tally)
    {
        float merge2 = colorMergeDistance * colorMergeDistance;
        while (families.Count > 1)
        {
            int bi = 0, bj = 1;
            float best = float.MaxValue;
            for (int i = 0; i < families.Count; i++)
                for (int j = i + 1; j < families.Count; j++)
                {
                    float d = (families[i].center - families[j].center).sqrMagnitude;
                    if (d < best) { best = d; bi = i; bj = j; }
                }

            if (best > merge2 && families.Count <= maxColorFamilies) break;

            families[bi].members.AddRange(families[bj].members);
            families.RemoveAt(bj);
            Recenter(families[bi], shades, points, tally);
        }
    }

    void Recenter(Family f, List<Color> shades, List<Vector3> points, Dictionary<Color, int> tally)
    {
        Vector3 sum = Vector3.zero;
        int total = 0, top = -1;
        foreach (int m in f.members)
        {
            int n = tally[shades[m]];
            sum += points[m] * n;
            total += n;
            if (n > top) { top = n; f.dominant = shades[m]; }
        }
        if (total > 0) f.center = sum / total;
        f.weight = total;
    }

    // Which family a shade sorts into. Every shade in the picture was mapped
    // during grouping; anything else (a color that only shows up later) falls
    // back to the nearest family center.
    int NearestPaletteIndex(Color c)
    {
        if (shadeFamily.TryGetValue(c, out int known)) return known;

        Vector3 pt = ColorPoint(c);
        int best = 0;
        float bd = float.MaxValue;
        for (int i = 0; i < palettePoints.Count; i++)
        {
            float d = (palettePoints[i] - pt).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // Tag every pixel with its palette index and tally how many pixels each color has.
    void AssignPaletteIndices()
    {
        paletteCount = new int[palette.Count];
        for (int i = 0; i < hanging.Count; i++)
        {
            var p = hanging[i];
            p.pi = NearestPaletteIndex(p.col);
            hanging[i] = p;
            paletteCount[p.pi]++;
        }
    }

    // Build the full finite jar sequence up front: for each color, its jars'
    // capacities sum to EXACTLY the number of pixels of that color. So a jar can
    // never ask for a color/amount that doesn't exist. Order is then shuffled.
    List<JarDef> BuildSequence()
    {
        var defs = new List<JarDef>();
        for (int pi = 0; pi < palette.Count; pi++)
        {
            int rem = paletteCount[pi];
            while (rem > 0)
            {
                int cap = rem <= capacityMax ? rem : Random.Range(capacityMin, capacityMax + 1);
                cap = Mathf.Min(cap, rem);
                defs.Add(new JarDef { pi = pi, color = palette[pi], capacity = cap });
                rem -= cap;
            }
        }
        // Fisher–Yates shuffle
        for (int i = defs.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (defs[i], defs[j]) = (defs[j], defs[i]);
        }
        return defs;
    }

    // ---- jars & queue ---------------------------------------------------

    void BuildJarsAndQueue()
    {
        foreach (var def in BuildSequence()) queue.Enqueue(def);

        for (int i = 0; i < ACTIVE_JARS; i++)
        {
            var s = new Slot();
            float l = jarCenterX[i] - jarInnerHalfW, r = jarCenterX[i] + jarInnerHalfW;
            s.box = MakeLine("Jar" + i, Color.white, 0.05f, false,
                new Vector3(l, jarTopY, 0), new Vector3(l, jarBottomY, 0),
                new Vector3(r, jarBottomY, 0), new Vector3(r, jarTopY, 0));
            s.text = MakeText("JarNum" + i, new Vector3(jarCenterX[i], jarTopY - 0.07f * H, 0), 0.32f, Color.white);
            slots[i] = s;
            if (queue.Count > 0) FillSlot(i, queue.Dequeue());
            else EmptySlot(i);
        }

        for (int i = 0; i < ACTIVE_JARS; i++)
        {
            float cx = jarCenterX[i];
            previewBox[i] = MakeLine("PrevBox" + i, Color.white, 0.035f, true,
                new Vector3(cx - previewHalfW, previewY - previewHalfH, 0),
                new Vector3(cx + previewHalfW, previewY - previewHalfH, 0),
                new Vector3(cx + previewHalfW, previewY + previewHalfH, 0),
                new Vector3(cx - previewHalfW, previewY + previewHalfH, 0));
            previewText[i] = MakeText("PrevNum" + i, new Vector3(cx, previewY, 0), 0.22f, Color.white);
        }
        RefreshPreview();

        statusText = MakeText("Status", new Vector3(0, 0.93f * H, 0), 0.4f, new Color(1f, 0.4f, 0.4f));
        statusText.text = "";
    }

    void FillSlot(int i, JarDef def)
    {
        var s = slots[i];
        s.pi = def.pi;
        s.color = def.color;
        s.capacity = def.capacity;
        s.reserved = 0;
        s.landed = 0;
        s.box.enabled = true;
        s.box.startColor = s.box.endColor = def.color;
        UpdateSlotText(i);
    }

    void EmptySlot(int i)
    {
        var s = slots[i];
        s.pi = -1;
        s.capacity = 0;
        s.reserved = 0;
        s.landed = 0;
        s.box.enabled = false;     // no more jars for this slot
        s.text.text = "";
    }

    void UpdateSlotText(int i)
    {
        var s = slots[i];
        if (s.pi < 0) { s.text.text = ""; return; }
        s.text.text = (s.capacity - s.landed).ToString();
        s.text.color = s.color;
    }

    void CompleteSlot(int i)
    {
        // mark the jar's pixels as eaten (don't mutate the list mid-simulation;
        // they are swept out at the end of Update)
        foreach (var f in fallers)
            if (f.landed && f.jar == i) f.consumed = true;
        sweepConsumed = true;
        if (queue.Count > 0) FillSlot(i, queue.Dequeue());
        else EmptySlot(i);          // finite sequence: nothing left for this slot
        RefreshPreview();
    }

    void RefreshPreview()
    {
        var arr = queue.ToArray();
        for (int i = 0; i < ACTIVE_JARS; i++)
        {
            if (i < arr.Length)
            {
                previewBox[i].enabled = true;
                previewBox[i].startColor = previewBox[i].endColor = arr[i].color;
                previewText[i].text = arr[i].capacity.ToString();
                previewText[i].color = arr[i].color;
            }
            else { previewBox[i].enabled = false; previewText[i].text = ""; }
        }
    }

    // Route strictly by palette index: a pixel only goes to a jar of its own
    // color that still has room. No threshold guessing — guarantees the totals
    // (jar capacities == pixel counts) stay consistent.
    int ChooseJar(int pi)
    {
        for (int i = 0; i < ACTIVE_JARS; i++)
            if (slots[i].pi == pi && slots[i].reserved < slots[i].capacity)
                return i;
        return -1;
    }

    // ---- visuals --------------------------------------------------------

    void BuildFrameVisual()
    {
        MakeLine("FrameVisual", FRAME_COLOR, 0.05f, true,
            new Vector3(-W, -H, 0), new Vector3(W, -H, 0), new Vector3(W, H, 0), new Vector3(-W, H, 0));
    }

    void BuildMachineVisual()
    {
        MakeLine("FunnelL", MACHINE_COLOR, 0.04f, false,
            new Vector3(-funnelHalfW, funnelTopY, 0), new Vector3(-tubeHalfW, tubeTopY, 0));
        MakeLine("FunnelR", MACHINE_COLOR, 0.04f, false,
            new Vector3(funnelHalfW, funnelTopY, 0), new Vector3(tubeHalfW, tubeTopY, 0));
        MakeLine("TubeL", MACHINE_COLOR, 0.04f, false,
            new Vector3(-tubeHalfW, tubeTopY, 0), new Vector3(-tubeHalfW, tubeBotY, 0));
        MakeLine("TubeR", MACHINE_COLOR, 0.04f, false,
            new Vector3(tubeHalfW, tubeTopY, 0), new Vector3(tubeHalfW, tubeBotY, 0));
        for (int i = 0; i < ACTIVE_JARS; i++)
            MakeLine("Split" + i, MACHINE_COLOR, 0.04f, false,
                new Vector3(0, tubeBotY, 0), new Vector3(jarCenterX[i], jarTopY, 0));
    }

    protected LineRenderer MakeLine(string name, Color c, float width, bool loop, params Vector3[] pts)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.numCornerVertices = 0;
        lr.widthMultiplier = width;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = c;
        lr.positionCount = pts.Length;
        lr.SetPositions(pts);
        return lr;
    }

    protected TextMesh MakeText(string name, Vector3 pos, float worldHeight, Color c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        var tm = go.AddComponent<TextMesh>();
        tm.font = uiFont;
        tm.GetComponent<MeshRenderer>().sharedMaterial = uiFont.material;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.fontSize = 64;
        tm.color = c;
        // characterSize tuned so one line ≈ worldHeight units tall
        tm.characterSize = worldHeight * 0.16f;
        return tm;
    }

    // ---- particle systems ----------------------------------------------

    void BuildParticleSystems()
    {
        hangingPS = MakePS("HangingPS");
        fallingPS = MakePS("FallingPS");
        ApplyPixelShape();

        // A second quad per pixel, same place and size, drawn on top with the
        // gloss texture added to whatever color is underneath.
        var shader = Shader.Find(GLOSS_SHADER);
        glossOn = glossTexture != null && glossStrength > 0f && shader != null;
        if (!glossOn)
        {
            if (glossStrength > 0f)
                Debug.LogWarning("[" + GetType().Name + "] pixel gloss is off: " + (glossTexture == null
                    ? "no Gloss Texture assigned on the component"
                    : "shader '" + GLOSS_SHADER + "' not found"), this);
            return;
        }

        var mat = new Material(shader) { mainTexture = glossTexture };
        hangingGlossPS = MakePS("HangingGlossPS");
        fallingGlossPS = MakePS("FallingGlossPS");
        foreach (var ps in new[] { hangingGlossPS, fallingGlossPS })
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.material = mat;
            r.sortingOrder = 1;
        }
    }

    protected virtual void BuildCutPreviewVisual()
    {
        cutGuidePS = MakePS("CutGuidePS");
        var main = cutGuidePS.main;
        main.maxParticles = 4096;
        cutGuidePS.GetComponent<ParticleSystemRenderer>().sortingOrder = CUT_GUIDE_SORT_ORDER;

        cutStartRing = MakeLine("CutStartRing", CUT_GUIDE_COLOR, pixel * 0.12f, true, Vector3.zero);
        cutCurrentRing = MakeLine("CutCurrentRing", CUT_GUIDE_COLOR, pixel * 0.12f, true, Vector3.zero);
        cutStartRing.sortingOrder = CUT_GUIDE_SORT_ORDER + 1;
        cutCurrentRing.sortingOrder = CUT_GUIDE_SORT_ORDER + 1;
        SetCutPreviewVisible(false);
    }

    // Mask the color quads with the gloss texture's alpha so a pixel is a rounded
    // square, not a hard block under a rounded highlight.
    void ApplyPixelShape()
    {
        if (!roundedPixels || glossTexture == null) return;
        var shader = Shader.Find(SHAPE_SHADER);
        if (shader == null)
        {
            Debug.LogWarning("[" + GetType().Name + "] rounded pixels are off: shader '" + SHAPE_SHADER + "' not found", this);
            return;
        }

        var mat = new Material(shader) { mainTexture = glossTexture };
        hangingPS.GetComponent<ParticleSystemRenderer>().material = mat;
        fallingPS.GetComponent<ParticleSystemRenderer>().material = mat;
    }

    ParticleSystem MakePS(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = Mathf.Max(2048, texW * texH + 16);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 1e9f;
        main.startSpeed = 0f;
        main.startSize = pixel;
        main.startColor = Color.white;
        main.gravityModifier = 0f;

        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = false;

        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.alignment = ParticleSystemRenderSpace.View;
        r.material = new Material(Shader.Find("Sprites/Default")) { mainTexture = Texture2D.whiteTexture };
        r.sortMode = ParticleSystemSortMode.None;

        ps.Play();
        return ps;
    }

    protected void UploadHanging()
    {
        int n = hanging.Count;
        EnsureBuf(n);
        for (int i = 0; i < n; i++) FillParticle(ref buf[i], hanging[i].pos, hanging[i].col);
        hangingPS.SetParticles(buf, n);

        if (!glossOn) return;
        for (int i = 0; i < n; i++) FillGlossParticle(ref buf[i], hanging[i].pos);
        hangingGlossPS.SetParticles(buf, n);
    }

    void RenderFallers()
    {
        int chunkPixels = 0;
        foreach (var c in detachedChunks) chunkPixels += c.pixels.Count;

        EnsureBuf(fallers.Count + chunkPixels);
        int n = 0;
        foreach (var f in fallers)
        {
            if (f.consumed) continue;
            FillParticle(ref buf[n++], f.pos, f.col);
        }
        foreach (var c in detachedChunks)
            foreach (var p in c.pixels)
                FillParticle(ref buf[n++], p.pos, p.col);
        fallingPS.SetParticles(buf, n);

        if (!glossOn) return;
        n = 0;
        foreach (var f in fallers)
        {
            if (f.consumed) continue;
            FillGlossParticle(ref buf[n++], f.pos);
        }
        foreach (var c in detachedChunks)
            foreach (var p in c.pixels)
                FillGlossParticle(ref buf[n++], p.pos);
        fallingGlossPS.SetParticles(buf, n);
    }

    void EnsureBuf(int n) { if (buf.Length < Mathf.Max(1, n)) buf = new ParticleSystem.Particle[Mathf.Max(1, n)]; }

    void FillParticle(ref ParticleSystem.Particle p, Vector3 pos, Color col)
    {
        p.position = pos;
        p.velocity = Vector3.zero;
        p.startSize = pixel;
        p.startColor = col;
        p.startLifetime = 1e9f;
        p.remainingLifetime = 1e9f;
    }

    // white tint = add the gloss as authored; the shader multiplies by it
    void FillGlossParticle(ref ParticleSystem.Particle p, Vector3 pos)
        => FillParticle(ref p, pos, new Color(glossStrength, glossStrength, glossStrength, 1f));

    void FillGuideParticle(ref ParticleSystem.Particle p, Vector3 pos, Color col)
    {
        FillParticle(ref p, pos, col);
        p.startSize = Mathf.Max(0.02f, pixel * cutGuideWidthPixels);
    }

    // ---- simulation -----------------------------------------------------

    protected virtual void Update()
    {
        HandleInput();
        Simulate(Time.deltaTime);
        RenderFallers();
        if (sweepConsumed) { fallers.RemoveAll(f => f.consumed); sweepConsumed = false; }
    }

    bool sweepConsumed;

    void Simulate(float dt)
    {
        SimulateDetachedChunks(dt);

        foreach (var f in fallers)
        {
            if (f.landed || f.inTube) continue;   // parked pixels are handled in WaitingPass

            f.vy -= gravity * dt;
            f.pos.y += f.vy * dt;

            if (!f.routed)
            {
                if (f.pos.y <= tubeTopY)
                {
                    f.routed = true;
                    int j = ChooseJar(f.pi);
                    if (j >= 0) { f.jar = j; slots[j].reserved++; }
                    else f.inTube = true;          // no jar right now — wait in the tube
                }
                else if (f.pos.y <= funnelTopY)
                    f.pos.x = Mathf.MoveTowards(f.pos.x, 0f, funnelSteer * dt);
                else
                {
                    // picture zone: fly out with the chip impulse, bounce off the walls
                    f.pos.x += f.vx * dt;
                    float lim = W - pixel * 0.5f;
                    if (f.pos.x > lim) { f.pos.x = lim; f.vx = -f.vx * 0.5f; }
                    else if (f.pos.x < -lim) { f.pos.x = -lim; f.vx = -f.vx * 0.5f; }
                }
            }

            if (f.routed && f.jar >= 0) StepIntoJar(f, dt);
        }

        WaitingPass();
    }

    void SimulateDetachedChunks(float dt)
    {
        for (int i = detachedChunks.Count - 1; i >= 0; i--)
        {
            var chunk = detachedChunks[i];
            chunk.vy -= gravity * dt;

            float bottom = float.MaxValue;
            for (int p = 0; p < chunk.pixels.Count; p++)
            {
                var px = chunk.pixels[p];
                px.pos.y += chunk.vy * dt;
                chunk.pixels[p] = px;
                bottom = Mathf.Min(bottom, px.pos.y - pixel * 0.5f);
            }

            if (bottom <= funnelTopY)
            {
                foreach (var px in chunk.pixels) AddFaller(px, chunk.vy);
                detachedChunks.RemoveAt(i);
            }
        }
    }

    // The tube is a buffer: pixels with no matching free jar wait here and are
    // re-checked every frame. When a suitable jar opens up (a jar completes and
    // the next one slides in), the oldest waiting pixel of that color drains into
    // it. The tube only CLOGs if more pixels are waiting than it can hold.
    void WaitingPass()
    {
        // drain: oldest-first, so FIFO within a color
        foreach (var f in fallers)
        {
            if (f.landed || !f.inTube) continue;
            int j = ChooseJar(f.pi);
            if (j >= 0) { f.jar = j; slots[j].reserved++; f.inTube = false; f.vy = 0f; }
        }
        // restack whoever is still waiting, bottom-up inside the tube
        int k = 0;
        float x0 = -tubeHalfW + pixel * 0.5f;
        foreach (var f in fallers)
        {
            if (f.landed || !f.inTube) continue;
            int row = k / backlogPerRow, col = k % backlogPerRow;
            f.pos = new Vector3(x0 + col * pixel, tubeBotY + (row + 0.5f) * pixel, 0f);
            f.vy = 0f;
            k++;
        }
        clogged = k > backlogCapacity;
        statusText.text = clogged ? "CLOG!" : "";
    }

    void StepIntoJar(Faller f, float dt)
    {
        float tx = jarCenterX[f.jar];
        f.pos.x = Mathf.MoveTowards(f.pos.x, tx, jarSteer * dt);
        bool aligned = Mathf.Abs(f.pos.x - tx) < pixel * 0.5f;

        int rows = slots[f.jar].landed / jarPerRow;
        float landY = jarBottomY + (rows + 0.5f) * pixel;

        if (!aligned && f.pos.y < jarTopY) f.pos.y = jarTopY;
        else if (aligned && f.pos.y <= landY)
        {
            var s = slots[f.jar];
            int idx = s.landed++;
            int row = idx / jarPerRow, col = idx % jarPerRow;
            float x0 = jarCenterX[f.jar] - jarInnerHalfW + pixel * 0.5f;
            f.pos = new Vector3(x0 + col * pixel, jarBottomY + (row + 0.5f) * pixel, 0f);
            f.vy = 0f; f.landed = true;
            UpdateSlotText(f.jar);
            if (s.landed >= s.capacity) CompleteSlot(f.jar);
        }
    }

    // ---- input ----------------------------------------------------------

    // First tap anchors point A. Dragging previews point B and the cut segment.
    // Releasing cuts the picture along the segment and shatters the smaller side.
    protected virtual void HandleInput()
    {
        if (Input.GetKeyDown(KeyCode.Space)) ReleaseAll();

        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            Vector3 world = ScreenToWorld(t.position);
            if (t.phase == TouchPhase.Began) BeginCut(world);
            else if (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary) UpdateCut(world);
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) EndCut(world);
            return;
        }

        if (Input.GetMouseButtonDown(0)) BeginCut(ScreenToWorld(Input.mousePosition));
        else if (Input.GetMouseButton(0)) UpdateCut(ScreenToWorld(Input.mousePosition));
        else if (Input.GetMouseButtonUp(0)) EndCut(ScreenToWorld(Input.mousePosition));
    }

    void BeginCut(Vector3 world)
    {
        cutting = true;
        cutStart = cutCurrent = world;
        SetCutPreviewVisible(true);
        UpdateCutPreview();
    }

    void SetCutPreviewVisible(bool visible)
    {
        if (cutStartRing != null) cutStartRing.enabled = visible;
        if (cutCurrentRing != null) cutCurrentRing.enabled = visible;
        if (!visible && cutGuidePS != null) cutGuidePS.SetParticles(cutBuf, 0);
    }

    void UpdateCutPreview()
    {
        UpdateCircle(cutStartRing, cutStart, pixel * 0.72f);
        UpdateCircle(cutCurrentRing, cutCurrent, pixel * 0.72f);

        float len = (cutCurrent - cutStart).magnitude;
        if (len < 1e-4f)
        {
            cutGuidePS.SetParticles(cutBuf, 0);
            return;
        }

        bool[,] occupied = BuildHangingGrid();
        Vector3 dir = (cutCurrent - cutStart) / len;
        float sampleStep = Mathf.Max(0.01f, pixel * 0.13f);
        int maxSamples = Mathf.CeilToInt(len / sampleStep) + 1;
        if (cutBuf.Length < maxSamples) cutBuf = new ParticleSystem.Particle[maxSamples];

        int n = 0;
        float dash = Mathf.Max(sampleStep, pixel * cutGuideDashPixels);
        float gap = Mathf.Max(sampleStep, pixel * cutGuideGapPixels);
        float cycle = dash + gap;
        for (float d = 0f; d <= len; d += sampleStep)
        {
            Vector3 pos = cutStart + dir * d;
            bool overPicture = IsWorldInsideOccupiedPixel(pos, occupied);
            if (overPicture && Mathf.Repeat(d, cycle) > dash) continue;
            FillGuideParticle(ref cutBuf[n++], pos, CUT_GUIDE_COLOR);
        }

        cutGuidePS.SetParticles(cutBuf, n);
    }

    protected void UpdateCircle(LineRenderer lr, Vector3 center, float radius)
    {
        if (lr == null) return;

        const int steps = 32;
        if (lr.positionCount != steps) lr.positionCount = steps;
        for (int i = 0; i < steps; i++)
        {
            float a = Mathf.PI * 2f * i / steps;
            lr.SetPosition(i, center + new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
        }
    }

    void UpdateCut(Vector3 world)
    {
        if (!cutting) return;
        cutCurrent = world;
        UpdateCutPreview();
    }

    void EndCut(Vector3 world)
    {
        if (!cutting) return;
        cutCurrent = world;
        cutting = false;
        SetCutPreviewVisible(false);

        if ((cutCurrent - cutStart).magnitude >= MIN_SWIPE)
            Cut(cutStart, cutCurrent);
    }

    void Cut(Vector3 a, Vector3 b)
    {
        if (hanging.Count == 0) return;

        var remove = new List<int>();
        float halfWidth = Mathf.Max(pixel * 0.18f, eraseRadius * 0.18f);
        for (int i = 0; i < hanging.Count; i++)
            if (DistancePointSegment(hanging[i].pos, a, b) <= halfWidth)
                remove.Add(i);
        if (remove.Count == 0) return;

        Vector2 cutDir = new Vector2(b.x - a.x, b.y - a.y);
        if (cutDir.sqrMagnitude < 1e-5f) cutDir = Vector2.right;
        Vector2 cutNormal = new Vector2(-cutDir.y, cutDir.x).normalized;
        foreach (int idx in remove)
        {
            var p = hanging[idx];
            float side = Mathf.Sign(Vector2.Dot(new Vector2(p.pos.x - a.x, p.pos.y - a.y), cutNormal));
            if (side == 0f) side = Random.value < 0.5f ? -1f : 1f;
            ShatterPixel(p, cutNormal * side, eraseImpulse * Random.Range(0.65f, 1.05f));
        }

        for (int k = remove.Count - 1; k >= 0; k--) hanging.RemoveAt(remove[k]);
        DetachSeparatedPieces(true, a, b);
        UploadHanging();
    }

    protected void ShatterPixel(Px p, Vector2 dir, float impulse)
    {
        if (dir.sqrMagnitude < 1e-5f)
        {
            float a = Random.value * Mathf.PI * 2f;
            dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        dir.Normalize();
        fallers.Add(new Faller
        {
            pos = p.pos,
            vx = dir.x * impulse,
            vy = dir.y * impulse + eraseImpulse * 0.2f,
            col = p.col,
            pi = p.pi
        });
    }

    protected void DetachSeparatedPieces(bool shatterDetached, Vector3 cutA, Vector3 cutB)
    {
        if (hanging.Count <= 1) return;

        int[,] indexAt = BuildHangingIndexGrid();
        var visited = new bool[hanging.Count];
        var components = new List<List<int>>();

        for (int i = 0; i < hanging.Count; i++)
        {
            if (visited[i]) continue;

            var component = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(i);
            visited[i] = true;

            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                component.Add(idx);
                var p = hanging[idx];

                EnqueueNeighbor(p.x - 1, p.y, indexAt, visited, q);
                EnqueueNeighbor(p.x + 1, p.y, indexAt, visited, q);
                EnqueueNeighbor(p.x, p.y - 1, indexAt, visited, q);
                EnqueueNeighbor(p.x, p.y + 1, indexAt, visited, q);
            }

            components.Add(component);
        }

        if (components.Count <= 1) return;

        int mainComponent = 0;
        for (int i = 1; i < components.Count; i++)
            if (components[i].Count > components[mainComponent].Count)
                mainComponent = i;

        var detachSet = new HashSet<int>();
        for (int i = 0; i < components.Count; i++)
        {
            if (i == mainComponent) continue;

            var pixels = new List<Px>(components[i].Count);
            foreach (int idx in components[i])
            {
                pixels.Add(hanging[idx]);
                detachSet.Add(idx);
            }

            if (shatterDetached)
                ShatterChunk(pixels, cutA, cutB);
            else
                detachedChunks.Add(new DetachedChunk(pixels));
        }

        for (int i = hanging.Count - 1; i >= 0; i--)
            if (detachSet.Contains(i))
                hanging.RemoveAt(i);
    }

    void ShatterChunk(List<Px> pixels, Vector3 cutA, Vector3 cutB)
    {
        if (pixels.Count == 0) return;

        Vector2 center = Vector2.zero;
        foreach (var p in pixels) center += new Vector2(p.pos.x, p.pos.y);
        center /= pixels.Count;

        Vector2 baseDir = ChunkShatterDir(center, cutA, cutB);

        foreach (var p in pixels)
        {
            Vector2 fromCenter = new Vector2(p.pos.x, p.pos.y) - center;
            Vector2 dir = (baseDir * 0.85f + fromCenter.normalized * 0.55f).normalized;
            ShatterPixel(p, dir, eraseImpulse * Random.Range(0.85f, 1.35f));
        }
    }

    // Which way a detached chunk sprays. Cut-driven here; blast variations push
    // the chunk away from the explosion instead.
    protected virtual Vector2 ChunkShatterDir(Vector2 center, Vector3 cutA, Vector3 cutB)
    {
        Vector2 cutDir = new Vector2(cutB.x - cutA.x, cutB.y - cutA.y);
        Vector2 cutNormal = cutDir.sqrMagnitude > 1e-5f ? new Vector2(-cutDir.y, cutDir.x).normalized : Vector2.up;
        float side = Mathf.Sign(Vector2.Dot(center - new Vector2(cutA.x, cutA.y), cutNormal));
        if (side == 0f) side = 1f;
        return cutNormal * side;
    }

    int[,] BuildHangingIndexGrid()
    {
        var indexAt = new int[texW, texH];
        for (int y = 0; y < texH; y++)
            for (int x = 0; x < texW; x++)
                indexAt[x, y] = -1;

        for (int i = 0; i < hanging.Count; i++)
            indexAt[hanging[i].x, hanging[i].y] = i;
        return indexAt;
    }

    void EnqueueNeighbor(int x, int y, int[,] indexAt, bool[] visited, Queue<int> q)
    {
        if (x < 0 || x >= texW || y < 0 || y >= texH) return;
        int idx = indexAt[x, y];
        if (idx < 0 || visited[idx]) return;
        visited[idx] = true;
        q.Enqueue(idx);
    }

    protected bool[,] BuildHangingGrid()
    {
        var occupied = new bool[texW, texH];
        foreach (var p in hanging) occupied[p.x, p.y] = true;
        return occupied;
    }

    bool IsWorldInsideOccupiedPixel(Vector3 world, bool[,] occupied)
    {
        int x = Mathf.RoundToInt(world.x / pixel + (texW - 1) * 0.5f);
        int y = Mathf.RoundToInt((world.y - pictureCenterY) / pixel + (texH - 1) * 0.5f);
        if (x < 0 || x >= texW || y < 0 || y >= texH || !occupied[x, y]) return false;

        Vector3 center = PixelToWorld(x, y);
        return Mathf.Abs(world.x - center.x) <= pixel * 0.5f
            && Mathf.Abs(world.y - center.y) <= pixel * 0.5f;
    }

    static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector2 ap = new Vector2(p.x - a.x, p.y - a.y);
        Vector2 ab = new Vector2(b.x - a.x, b.y - a.y);
        float ab2 = ab.sqrMagnitude;
        if (ab2 < 1e-6f) return ap.magnitude;
        float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / ab2);
        Vector2 closest = new Vector2(a.x, a.y) + ab * t;
        return Vector2.Distance(new Vector2(p.x, p.y), closest);
    }

    bool IsEdgePixel(Px p, bool[,] occupied)
    {
        return IsEmptyNeighbor(p.x - 1, p.y, occupied)
            || IsEmptyNeighbor(p.x + 1, p.y, occupied)
            || IsEmptyNeighbor(p.x, p.y - 1, occupied)
            || IsEmptyNeighbor(p.x, p.y + 1, occupied);
    }

    bool IsEmptyNeighbor(int x, int y, bool[,] occupied)
    {
        return x < 0 || x >= texW || y < 0 || y >= texH || !occupied[x, y];
    }

    protected Vector3 ScreenToWorld(Vector3 screen)
    {
        screen.z = -cam.transform.position.z;
        var w = cam.ScreenToWorldPoint(screen);
        w.z = 0f;
        return w;
    }

    void AddFaller(Px p, float vy = 0f) => fallers.Add(new Faller { pos = p.pos, vy = vy, col = p.col, pi = p.pi });

    void ReleaseAll()
    {
        if (hanging.Count == 0) return;
        foreach (var p in hanging) AddFaller(p);
        hanging.Clear();
        UploadHanging();
    }

}
