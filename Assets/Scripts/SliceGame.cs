using System.Collections.Generic;
using UnityEngine;

// Iteration 3 — color-sorting machine.
//   * Picture is built from any texture (per-pixel color), see LoadColors.
//   * SPACE / tap releases the whole picture; swipe still cuts a chunk off.
//   * Released pixels fall, funnel into a central tube, then the tube splits
//     into three channels leading to three jars.
//   * Jar colors are auto-derived from the texture's dominant colors.
//     2 distinct colors  -> [c0, c1, c1];  3+ -> top three.
//   * Each falling pixel is routed to the jar whose color is nearest to it.
//   * Jars are infinite for now: pixels just stack up bottom-to-top.
// Pixels are NOT GameObjects. HangingPS renders the intact picture; FallingPS
// is a pure renderer fed by a tiny hand-written 2D simulation (physics can't
// sort by color, so the falling/routing is done in script).
[DisallowMultipleComponent]
public class SliceGame : MonoBehaviour
{
    [Header("Source")]
    public Texture2D sourceTexture;   // assign any texture; null => generated default
    public int maxResolution = 48;    // cap on the longest side when sampling

    [Header("Tuning")]
    public float gravity = 16f;
    public float funnelSteer = 9f;    // horizontal pull toward the tube
    public float jarSteer = 13f;      // horizontal pull toward the target jar

    const float ORTHO_SIZE = 5f;
    const float MIN_SWIPE = 0.2f;

    static readonly Color FRAME_COLOR = new Color(0.9f, 0.9f, 0.9f, 1f);
    static readonly Color BG_COLOR = new Color(0.08f, 0.08f, 0.10f, 1f);
    static readonly Color MACHINE_COLOR = new Color(0.55f, 0.58f, 0.65f, 1f);

    struct Px { public Vector3 pos; public Color col; }

    class Faller
    {
        public Vector3 pos;
        public float vy;
        public Color col;
        public int jar;
        public bool sorted;
        public bool landed;
    }

    ParticleSystem hangingPS, fallingPS;
    readonly List<Px> hanging = new List<Px>();
    readonly List<Faller> fallers = new List<Faller>();
    ParticleSystem.Particle[] buf = new ParticleSystem.Particle[256];

    Camera cam;
    float W, H;            // frame half width / height
    float pixel;
    int texW, texH;

    // machine layout (world Y / X)
    float funnelTopY, tubeTopY, tubeBotY, jarTopY, jarBottomY, pictureCenterY;
    float tubeHalfW, funnelHalfW, jarInnerHalfW;
    readonly float[] jarCenterX = new float[3];
    readonly Color[] jarColor = new Color[3];
    readonly int[] jarCount = new int[3];
    int jarPerRow;

    bool dragging;
    Vector3 downPos;

    void Start()
    {
        SetupCamera();
        ComputeFrameBounds();
        var cols = LoadColors(out texW, out texH);
        ComputeLayout();
        BuildFrameVisual();
        BuildParticleSystems();
        BuildPicture(cols);
        ExtractPalette();
        BuildMachineVisual();
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
        tubeTopY = 0.12f * H;
        tubeBotY = -0.05f * H;
        jarTopY = -0.28f * H;
        jarBottomY = -0.92f * H;
        pictureCenterY = 0.62f * H;

        // fit picture into the zone above the funnel
        float zoneH = 0.55f * H;
        float zoneW = 1.6f * W;
        pixel = Mathf.Min(zoneH / Mathf.Max(1, texH), zoneW / Mathf.Max(1, texW));

        tubeHalfW = Mathf.Max(pixel * 1.5f, 0.05f * W);
        funnelHalfW = 0.45f * W;
        jarInnerHalfW = 0.26f * W;
        jarCenterX[0] = -0.58f * W;
        jarCenterX[1] = 0f;
        jarCenterX[2] = 0.58f * W;
        jarPerRow = Mathf.Max(1, Mathf.FloorToInt(2f * jarInnerHalfW / pixel));
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
        var cols = tmp.GetPixels();   // bottom-to-top rows
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
        var sum = new Dictionary<int, Vector4>();   // key -> (r,g,b, count)
        foreach (var p in hanging)
        {
            int key = Quant(p.col);
            Vector4 v = sum.TryGetValue(key, out var cur) ? cur : Vector4.zero;
            v.x += p.col.r; v.y += p.col.g; v.z += p.col.b; v.w += 1f;
            sum[key] = v;
        }

        var list = new List<Vector4>(sum.Values);
        list.Sort((a, b) => b.w.CompareTo(a.w));   // by count desc

        var reps = new List<Color>();
        foreach (var v in list)
            reps.Add(new Color(v.x / v.w, v.y / v.w, v.z / v.w, 1f));

        if (reps.Count == 0) { reps.Add(Color.red); reps.Add(Color.green); reps.Add(Color.blue); }
        while (reps.Count < 3) reps.Add(reps[reps.Count - 1]);   // 2 colors -> [c0,c1,c1]

        for (int i = 0; i < 3; i++) jarColor[i] = reps[i];
    }

    static int Quant(Color c)
    {
        int r = Mathf.RoundToInt(c.r * 4f);
        int g = Mathf.RoundToInt(c.g * 4f);
        int b = Mathf.RoundToInt(c.b * 4f);
        return (r * 5 + g) * 5 + b;
    }

    int NearestJar(Color c)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float dr = c.r - jarColor[i].r, dg = c.g - jarColor[i].g, db = c.b - jarColor[i].b;
            float d = dr * dr + dg * dg + db * db;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // ---- visuals --------------------------------------------------------

    void BuildFrameVisual()
    {
        Line("FrameVisual", FRAME_COLOR, 0.05f, true,
            new Vector3(-W, -H, 0), new Vector3(W, -H, 0), new Vector3(W, H, 0), new Vector3(-W, H, 0));
    }

    void BuildMachineVisual()
    {
        // funnel
        Line("FunnelL", MACHINE_COLOR, 0.04f, false,
            new Vector3(-funnelHalfW, funnelTopY, 0), new Vector3(-tubeHalfW, tubeTopY, 0));
        Line("FunnelR", MACHINE_COLOR, 0.04f, false,
            new Vector3(funnelHalfW, funnelTopY, 0), new Vector3(tubeHalfW, tubeTopY, 0));
        // tube
        Line("TubeL", MACHINE_COLOR, 0.04f, false,
            new Vector3(-tubeHalfW, tubeTopY, 0), new Vector3(-tubeHalfW, tubeBotY, 0));
        Line("TubeR", MACHINE_COLOR, 0.04f, false,
            new Vector3(tubeHalfW, tubeTopY, 0), new Vector3(tubeHalfW, tubeBotY, 0));
        // 3-way split + jars
        for (int i = 0; i < 3; i++)
        {
            Line("Split" + i, MACHINE_COLOR, 0.04f, false,
                new Vector3(0, tubeBotY, 0), new Vector3(jarCenterX[i], jarTopY, 0));
            float l = jarCenterX[i] - jarInnerHalfW, r = jarCenterX[i] + jarInnerHalfW;
            Line("Jar" + i, jarColor[i], 0.05f, false,
                new Vector3(l, jarTopY, 0), new Vector3(l, jarBottomY, 0),
                new Vector3(r, jarBottomY, 0), new Vector3(r, jarTopY, 0));
        }
    }

    void Line(string name, Color c, float width, bool loop, params Vector3[] pts)
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
        int n = fallers.Count;
        EnsureBuf(n);
        for (int i = 0; i < n; i++) FillParticle(ref buf[i], fallers[i].pos, fallers[i].col);
        fallingPS.SetParticles(buf, Mathf.Max(0, n));
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
    }

    void Simulate(float dt)
    {
        bool any = false;
        foreach (var f in fallers)
        {
            if (f.landed) continue;
            any = true;

            f.vy -= gravity * dt;
            f.pos.y += f.vy * dt;

            if (!f.sorted)
            {
                if (f.pos.y <= tubeTopY) { f.sorted = true; f.jar = NearestJar(f.col); }
                else if (f.pos.y <= funnelTopY)
                    f.pos.x = Mathf.MoveTowards(f.pos.x, 0f, funnelSteer * dt);
            }

            if (f.sorted)
            {
                float tx = jarCenterX[f.jar];
                f.pos.x = Mathf.MoveTowards(f.pos.x, tx, jarSteer * dt);
                bool aligned = Mathf.Abs(f.pos.x - tx) < pixel * 0.5f;

                int rows = jarCount[f.jar] / jarPerRow;
                float landY = jarBottomY + (rows + 0.5f) * pixel;

                if (!aligned && f.pos.y < jarTopY)
                    f.pos.y = jarTopY;                 // wait at the jar mouth until lined up
                else if (aligned && f.pos.y <= landY)
                    Land(f);
            }
        }
        // keep FallingPS alive so SetParticles renders even when nothing moves
        if (!any && fallers.Count > 0) { /* still rendered each frame */ }
    }

    void Land(Faller f)
    {
        int idx = jarCount[f.jar]++;
        int row = idx / jarPerRow;
        int col = idx % jarPerRow;
        float x0 = jarCenterX[f.jar] - jarInnerHalfW + pixel * 0.5f;
        f.pos = new Vector3(x0 + col * pixel, jarBottomY + (row + 0.5f) * pixel, 0f);
        f.vy = 0f;
        f.landed = true;
    }

    // ---- input & release ------------------------------------------------

    void HandleInput()
    {
        if (Input.GetKeyDown(KeyCode.Space)) ReleaseAll();

        if (Input.GetMouseButtonDown(0)) BeginSwipe(Input.mousePosition);
        else if (Input.GetMouseButtonUp(0)) EndSwipe(Input.mousePosition);

        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            if (Input.touchCount >= 2) { ReleaseAll(); return; }
            if (t.phase == TouchPhase.Began) BeginSwipe(t.position);
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) EndSwipe(t.position);
        }
    }

    void BeginSwipe(Vector3 screen) { downPos = ScreenToWorld(screen); dragging = true; }

    void EndSwipe(Vector3 screen)
    {
        if (!dragging) return;
        dragging = false;
        DoCut(downPos, ScreenToWorld(screen));
    }

    Vector3 ScreenToWorld(Vector3 screen)
    {
        screen.z = -cam.transform.position.z;
        var w = cam.ScreenToWorldPoint(screen);
        w.z = 0f;
        return w;
    }

    void AddFaller(Px p) => fallers.Add(new Faller { pos = p.pos, vy = 0f, col = p.col });

    void ReleaseAll()
    {
        if (hanging.Count == 0) return;
        foreach (var p in hanging) AddFaller(p);
        hanging.Clear();
        UploadHanging();
    }

    void DoCut(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        if (d.magnitude < MIN_SWIPE || hanging.Count == 0) return;

        var pos = new List<Px>();
        var neg = new List<Px>();
        foreach (var p in hanging)
        {
            float cross = d.x * (p.pos.y - a.y) - d.y * (p.pos.x - a.x);
            if (cross > 0f) pos.Add(p); else neg.Add(p);
        }
        if (pos.Count == 0 || neg.Count == 0) return;

        var fall = pos.Count <= neg.Count ? pos : neg;
        var stay = pos.Count <= neg.Count ? neg : pos;

        foreach (var p in fall) AddFaller(p);
        hanging.Clear();
        hanging.AddRange(stay);
        UploadHanging();
    }
}
