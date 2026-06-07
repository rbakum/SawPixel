using System.Collections.Generic;
using UnityEngine;

// Iteration 2 — picture is built from ANY texture (per-pixel color).
// Assign a Texture2D in the inspector; transparent pixels (alpha < 0.5) are skipped.
// If no texture is assigned, a default 3-color test pattern is generated.
// Pixels are NOT GameObjects — rendered/simulated by two ParticleSystems:
//   HangingPS — static particles (the intact picture), no gravity.
//   FallingPS — built-in PS physics (gravity + world collision against frame colliders).
// Swipe (mouse / finger) to cut: the smaller side falls and breaks apart.
[DisallowMultipleComponent]
public class SliceGame : MonoBehaviour
{
    [Header("Source")]
    public Texture2D sourceTexture;   // assign any texture; null => generated default
    public int maxResolution = 48;    // cap on the longest side when sampling the texture

    const float ORTHO_SIZE = 5f;
    const float MIN_SWIPE = 0.2f;

    static readonly Color FRAME_COLOR = new Color(0.9f, 0.9f, 0.9f, 1f);
    static readonly Color BG_COLOR = new Color(0.08f, 0.08f, 0.10f, 1f);

    struct Px { public Vector3 pos; public Color col; }

    ParticleSystem hangingPS;
    ParticleSystem fallingPS;
    readonly List<Px> hanging = new List<Px>();

    Camera cam;
    float frameHalfW, frameHalfH;
    float pixel;            // world size of one pixel (fit to frame)
    int texW, texH;
    bool dragging;
    Vector3 downPos;

    void Start()
    {
        SetupCamera();
        ComputeFrameBounds();
        BuildColliders();
        BuildFrameVisual();
        var cols = LoadColors(out texW, out texH);
        ComputePixelSize();
        BuildParticleSystems();
        BuildPicture(cols);
        UploadHanging();
    }

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
        frameHalfH = ORTHO_SIZE * 0.92f;
        frameHalfW = ORTHO_SIZE * cam.aspect * 0.92f;
    }

    void ComputePixelSize()
    {
        // Fit the picture into ~85% of the frame on both axes.
        float fitH = frameHalfH * 2f * 0.85f / Mathf.Max(1, texH);
        float fitW = frameHalfW * 2f * 0.85f / Mathf.Max(1, texW);
        pixel = Mathf.Min(fitH, fitW);
    }

    // ---- texture loading ------------------------------------------------

    Color[] LoadColors(out int w, out int h)
    {
        if (sourceTexture != null) return ReadViaBlit(sourceTexture, out w, out h);
        return GenerateDefault(out w, out h);
    }

    // Works even when the texture is not marked Read/Write enabled.
    Color[] ReadViaBlit(Texture2D src, out int w, out int h)
    {
        int sw = src.width, sh = src.height;
        float scale = Mathf.Min(1f, (float)maxResolution / Mathf.Max(sw, sh));
        w = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
        h = Mathf.Max(1, Mathf.RoundToInt(sh * scale));

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prevFilter = src.filterMode;
        Graphics.Blit(src, rt);
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply();
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        var cols = tmp.GetPixels();   // bottom-to-top rows
        Destroy(tmp);
        return cols;
    }

    // Default test pattern: 32x32 split into three vertical color bands.
    Color[] GenerateDefault(out int w, out int h)
    {
        w = 32; h = 32;
        var c = new Color[w * h];
        Color red = new Color(0.90f, 0.20f, 0.20f);
        Color green = new Color(0.25f, 0.80f, 0.35f);
        Color blue = new Color(0.25f, 0.55f, 0.95f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color col = x < w / 3 ? red : (x < 2 * w / 3 ? green : blue);
                c[y * w + x] = col;
            }
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
        float wy = (y - (texH - 1) * 0.5f) * pixel + 0.5f;
        return new Vector3(wx, wy, 0f);
    }

    // ---- frame & colliders ---------------------------------------------

    void BuildColliders()
    {
        const float t = 0.5f;
        const float zd = 10f;
        MakeWall("Floor", new Vector3(0, -frameHalfH - t, 0), new Vector3(frameHalfW * 2 + t * 2, t * 2, zd));
        MakeWall("LeftWall", new Vector3(-frameHalfW - t, 0, 0), new Vector3(t * 2, frameHalfH * 2, zd));
        MakeWall("RightWall", new Vector3(frameHalfW + t, 0, 0), new Vector3(t * 2, frameHalfH * 2, zd));
    }

    void MakeWall(string name, Vector3 pos, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        go.AddComponent<BoxCollider>().size = size;
    }

    void BuildFrameVisual()
    {
        var go = new GameObject("FrameVisual");
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = 4;
        lr.widthMultiplier = 0.05f;
        lr.numCornerVertices = 0;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = FRAME_COLOR;
        lr.SetPosition(0, new Vector3(-frameHalfW, -frameHalfH, 0));
        lr.SetPosition(1, new Vector3(frameHalfW, -frameHalfH, 0));
        lr.SetPosition(2, new Vector3(frameHalfW, frameHalfH, 0));
        lr.SetPosition(3, new Vector3(-frameHalfW, frameHalfH, 0));
    }

    // ---- particle systems ----------------------------------------------

    Material MakePixelMaterial()
    {
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.mainTexture = Texture2D.whiteTexture;
        return mat;
    }

    void BuildParticleSystems()
    {
        hangingPS = MakePS("HangingPS", 0f, false);
        fallingPS = MakePS("FallingPS", 1f, true);
    }

    ParticleSystem MakePS(string name, float gravity, bool collide)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = Mathf.Max(1024, texW * texH + 16);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 1e9f;
        main.startSpeed = 0f;
        main.startSize = pixel;
        main.startColor = Color.white;
        main.gravityModifier = gravity;

        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = false;

        if (collide)
        {
            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.dampen = 0.35f;
            col.bounce = 0.15f;
            col.lifetimeLoss = 0f;
            col.radiusScale = 0.5f;
        }

        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.alignment = ParticleSystemRenderSpace.View;
        r.material = MakePixelMaterial();
        r.sortMode = ParticleSystemSortMode.None;

        ps.Play();
        return ps;
    }

    void UploadHanging()
    {
        int n = hanging.Count;
        var arr = new ParticleSystem.Particle[Mathf.Max(1, n)];
        for (int i = 0; i < n; i++)
        {
            arr[i].position = hanging[i].pos;
            arr[i].velocity = Vector3.zero;
            arr[i].startSize = pixel;
            arr[i].startColor = hanging[i].col;
            arr[i].startLifetime = 1e9f;
            arr[i].remainingLifetime = 1e9f;
        }
        hangingPS.SetParticles(arr, n);
    }

    void EmitFalling(Vector3 pos, Color col)
    {
        var ep = new ParticleSystem.EmitParams();
        ep.position = pos;
        ep.velocity = Vector3.zero;
        ep.startSize = pixel;
        ep.startColor = col;
        ep.startLifetime = 1e9f;
        fallingPS.Emit(ep, 1);
    }

    // ---- input & cutting -----------------------------------------------

    void Update()
    {
        if (Input.GetMouseButtonDown(0)) BeginSwipe(Input.mousePosition);
        else if (Input.GetMouseButtonUp(0)) EndSwipe(Input.mousePosition);

        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
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

        foreach (var p in fall) EmitFalling(p.pos, p.col);

        hanging.Clear();
        hanging.AddRange(stay);
        UploadHanging();
    }
}
