using System.Collections.Generic;
using UnityEngine;

// Iteration 4 — jars with capacity + a queue ("layers") and clog risk.
//   * Picture is built from any texture (per-pixel color).
//   * SPACE / two-finger tap releases the whole picture; swipe cuts a chunk.
//   * Pixels funnel into a central tube, then route to one of three ACTIVE jars
//     by nearest color (only if that jar still has capacity).
//   * Each jar has a random small capacity and a random color drawn from the
//     texture palette. A number on the jar shows how many pixels it still needs.
//   * When a jar is full its pixels are consumed and the next jar from the queue
//     slides into that slot. The next layer (3 upcoming jars) is shown as a
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

    [Header("Tuning")]
    public float gravity = 16f;
    public float funnelSteer = 9f;
    public float jarSteer = 13f;
    public int capacityMin = 5;
    public int capacityMax = 15;
    public float colorMatch = 0.30f;  // max color distance a jar will accept

    [Header("Erase")]
    public float fingerPixels = 4.5f; // erase radius in pixel-widths (finger size)
    public float eraseImpulse = 4f;   // how hard chipped pixels fly out

    const float ORTHO_SIZE = 5f;
    const float MIN_SWIPE = 0.2f;
    const int PALETTE_MAX = 5;

    static readonly Color FRAME_COLOR = new Color(0.9f, 0.9f, 0.9f, 1f);
    static readonly Color BG_COLOR = new Color(0.08f, 0.08f, 0.10f, 1f);
    static readonly Color MACHINE_COLOR = new Color(0.55f, 0.58f, 0.65f, 1f);

    struct Px { public Vector3 pos; public Color col; public int pi; }   // pi = palette index

    class Faller
    {
        public Vector3 pos;
        public float vx;       // horizontal velocity (impulse pop in the picture zone)
        public float vy;
        public Color col;
        public int pi;         // palette index this pixel belongs to
        public int jar;        // 0..2 active jar
        public bool routed;    // has passed the tube entry decision point
        public bool inTube;    // parked in the tube buffer, waiting for a free jar
        public bool landed;
        public bool consumed;  // jar completed and ate this pixel; swept out after the loop
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
    readonly List<Px> hanging = new List<Px>();
    readonly List<Faller> fallers = new List<Faller>();
    ParticleSystem.Particle[] buf = new ParticleSystem.Particle[256];

    Camera cam;
    float W, H, pixel;
    int texW, texH;

    float funnelTopY, tubeTopY, tubeBotY, jarTopY, jarBottomY, pictureCenterY, previewY;
    float tubeHalfW, funnelHalfW, jarInnerHalfW, previewHalfW, previewHalfH;
    float eraseRadius;
    readonly float[] jarCenterX = new float[3];
    int jarPerRow, backlogPerRow, backlogCapacity;

    readonly List<Color> palette = new List<Color>();
    int[] paletteCount;
    readonly Slot[] slots = new Slot[3];
    readonly Queue<JarDef> queue = new Queue<JarDef>();
    LineRenderer[] previewBox = new LineRenderer[3];
    TextMesh[] previewText = new TextMesh[3];

    bool clogged;
    TextMesh statusText;
    Font uiFont;

    void Start()
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
        UploadHanging();
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

    void ComputeLayout()
    {
        funnelTopY = 0.35f * H;
        tubeTopY = 0.14f * H;
        tubeBotY = -0.04f * H;
        jarTopY = -0.20f * H;
        jarBottomY = -0.66f * H;
        previewY = -0.84f * H;
        pictureCenterY = 0.62f * H;

        float zoneH = 0.55f * H;
        float zoneW = 1.6f * W;
        pixel = Mathf.Min(zoneH / Mathf.Max(1, texH), zoneW / Mathf.Max(1, texW));

        tubeHalfW = Mathf.Max(pixel * 3f, 0.08f * W);
        funnelHalfW = 0.45f * W;
        jarInnerHalfW = 0.26f * W;
        jarCenterX[0] = -0.58f * W;
        jarCenterX[1] = 0f;
        jarCenterX[2] = 0.58f * W;
        jarPerRow = Mathf.Max(1, Mathf.FloorToInt(2f * jarInnerHalfW / pixel));

        previewHalfW = 0.14f * W;
        previewHalfH = 0.07f * H;

        backlogPerRow = Mathf.Max(1, Mathf.FloorToInt(2f * tubeHalfW / pixel));
        backlogCapacity = backlogPerRow * Mathf.Max(2, Mathf.FloorToInt((tubeTopY - tubeBotY) / pixel));

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

    Color[] ReadViaBlit(Texture2D src, out int w, out int h)
    {
        int sw = src.width, sh = src.height;
        float scale = Mathf.Min(1f, (float)maxResolution / Mathf.Max(sw, sh));
        w = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
        h = Mathf.Max(1, Mathf.RoundToInt(sh * scale));

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
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
                hanging.Add(new Px { pos = PixelToWorld(x, y), col = col });
            }
    }

    Vector3 PixelToWorld(int x, int y)
    {
        float wx = (x - (texW - 1) * 0.5f) * pixel;
        float wy = (y - (texH - 1) * 0.5f) * pixel + pictureCenterY;
        return new Vector3(wx, wy, 0f);
    }

    // ---- palette --------------------------------------------------------

    void ExtractPalette()
    {
        var sum = new Dictionary<int, Vector4>();
        foreach (var p in hanging)
        {
            int key = Quant(p.col);
            Vector4 v = sum.TryGetValue(key, out var cur) ? cur : Vector4.zero;
            v.x += p.col.r; v.y += p.col.g; v.z += p.col.b; v.w += 1f;
            sum[key] = v;
        }
        var list = new List<Vector4>(sum.Values);
        list.Sort((a, b) => b.w.CompareTo(a.w));

        palette.Clear();
        foreach (var v in list)
        {
            if (palette.Count >= PALETTE_MAX) break;
            palette.Add(new Color(v.x / v.w, v.y / v.w, v.z / v.w, 1f));
        }
        if (palette.Count == 0) { palette.Add(Color.red); palette.Add(Color.green); palette.Add(Color.blue); }
    }

    static int Quant(Color c)
    {
        int r = Mathf.RoundToInt(c.r * 4f);
        int g = Mathf.RoundToInt(c.g * 4f);
        int b = Mathf.RoundToInt(c.b * 4f);
        return (r * 5 + g) * 5 + b;
    }

    static float ColorDist2(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return dr * dr + dg * dg + db * db;
    }

    int NearestPaletteIndex(Color c)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            float d = ColorDist2(c, palette[i]);
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

        for (int i = 0; i < 3; i++)
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

        for (int i = 0; i < 3; i++)
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
        for (int i = 0; i < 3; i++)
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
        for (int i = 0; i < 3; i++)
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
        for (int i = 0; i < 3; i++)
            MakeLine("Split" + i, MACHINE_COLOR, 0.04f, false,
                new Vector3(0, tubeBotY, 0), new Vector3(jarCenterX[i], jarTopY, 0));
    }

    LineRenderer MakeLine(string name, Color c, float width, bool loop, params Vector3[] pts)
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

    TextMesh MakeText(string name, Vector3 pos, float worldHeight, Color c)
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

    void UploadHanging()
    {
        int n = hanging.Count;
        EnsureBuf(n);
        for (int i = 0; i < n; i++) FillParticle(ref buf[i], hanging[i].pos, hanging[i].col);
        hangingPS.SetParticles(buf, n);
    }

    void RenderFallers()
    {
        EnsureBuf(fallers.Count);
        int n = 0;
        foreach (var f in fallers)
        {
            if (f.consumed) continue;
            FillParticle(ref buf[n++], f.pos, f.col);
        }
        fallingPS.SetParticles(buf, n);
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

    // ---- simulation -----------------------------------------------------

    void Update()
    {
        HandleInput();
        Simulate(Time.deltaTime);
        RenderFallers();
        if (sweepConsumed) { fallers.RemoveAll(f => f.consumed); sweepConsumed = false; }
    }

    bool sweepConsumed;

    void Simulate(float dt)
    {
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

    // Touch/hold erases: every frame the finger is down, all hanging pixels
    // within a finger-sized radius chip off and fly out with a diagonal impulse.
    void HandleInput()
    {
        if (Input.GetKeyDown(KeyCode.Space)) ReleaseAll();

        bool down = false;
        Vector3 screen = default;
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            if (t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled) { down = true; screen = t.position; }
        }
        else if (Input.GetMouseButton(0)) { down = true; screen = Input.mousePosition; }

        if (down) Erase(ScreenToWorld(screen));
    }

    void Erase(Vector3 center)
    {
        if (hanging.Count == 0) return;
        float r2 = eraseRadius * eraseRadius;

        // pixels inside the finger radius are the ones chipping off this frame
        var remove = new List<int>();
        for (int i = 0; i < hanging.Count; i++)
        {
            float dx = hanging[i].pos.x - center.x, dy = hanging[i].pos.y - center.y;
            if (dx * dx + dy * dy <= r2) remove.Add(i);
        }
        if (remove.Count == 0) return;

        var removeSet = new HashSet<int>(remove);
        float nr2 = eraseRadius * 3f; nr2 *= nr2;   // neighborhood to analyze

        foreach (int idx in remove)
        {
            var p = hanging[idx];

            // repulsion away from nearby pixels that are NOT being destroyed:
            // each surviving neighbor pushes this pixel outward (inverse-square),
            // so it flies toward open space, never back into the picture.
            Vector2 away = Vector2.zero;
            for (int j = 0; j < hanging.Count; j++)
            {
                if (removeSet.Contains(j)) continue;
                float dx = p.pos.x - hanging[j].pos.x, dy = p.pos.y - hanging[j].pos.y;
                float d2 = dx * dx + dy * dy;
                if (d2 > nr2 || d2 < 1e-6f) continue;
                away += new Vector2(dx, dy) / d2;
            }

            Vector2 dir;
            if (away.sqrMagnitude > 1e-5f) dir = away.normalized;
            else
            {
                // fully surrounded (no open side nearby) — fan out from the touch point
                Vector2 fc = new Vector2(p.pos.x - center.x, p.pos.y - center.y);
                if (fc.sqrMagnitude > 1e-5f) dir = fc.normalized;
                else { float a = Random.value * Mathf.PI * 2f; dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); }
            }

            float mag = eraseImpulse * Random.Range(0.7f, 1.1f);
            float vx = dir.x * mag;
            float vy = dir.y * mag + eraseImpulse * 0.25f;   // slight upward arc for feel
            fallers.Add(new Faller { pos = p.pos, vx = vx, vy = vy, col = p.col, pi = p.pi });
        }

        for (int k = remove.Count - 1; k >= 0; k--) hanging.RemoveAt(remove[k]);
        UploadHanging();
    }

    Vector3 ScreenToWorld(Vector3 screen)
    {
        screen.z = -cam.transform.position.z;
        var w = cam.ScreenToWorldPoint(screen);
        w.z = 0f;
        return w;
    }

    void AddFaller(Px p) => fallers.Add(new Faller { pos = p.pos, vy = 0f, col = p.col, pi = p.pi });

    void ReleaseAll()
    {
        if (hanging.Count == 0) return;
        foreach (var p in hanging) AddFaller(p);
        hanging.Clear();
        UploadHanging();
    }

}
