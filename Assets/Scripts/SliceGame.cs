using System.Collections.Generic;
using UnityEngine;

// Iteration 1 — slice game prototype.
// 32x32 pixel picture ("I") hangs inside a frame. Swipe with mouse/finger to cut.
// The smaller side of the cut falls and breaks into separate physical pixels.
// Pixels are NOT GameObjects — they are rendered/simulated by two ParticleSystems.
//   HangingPS  — static particles that hang in place (the intact picture), no gravity.
//   FallingPS  — built-in PS physics: gravity + world collision against frame colliders.
// Self-assembling: drop this single component on one GameObject and press Play.
[DisallowMultipleComponent]
public class SliceGame : MonoBehaviour
{
    const int GRID = 32;
    const float PIXEL = 0.22f;        // world size of one pixel
    const float CENTER_Y = 0.5f;      // vertical center of the picture
    const float ORTHO_SIZE = 5f;      // camera half-height
    const float MIN_SWIPE = 0.2f;     // ignore taps shorter than this

    static readonly Color PIXEL_COLOR = new Color(0.30f, 0.75f, 1f, 1f);
    static readonly Color FRAME_COLOR = new Color(0.9f, 0.9f, 0.9f, 1f);
    static readonly Color BG_COLOR = new Color(0.08f, 0.08f, 0.10f, 1f);

    ParticleSystem hangingPS;
    ParticleSystem fallingPS;
    readonly List<Vector3> hanging = new List<Vector3>();

    Camera cam;
    float frameHalfW, frameHalfH;
    bool dragging;
    Vector3 downPos;

    void Start()
    {
        SetupCamera();
        ComputeFrameBounds();
        BuildColliders();
        BuildFrameVisual();
        BuildParticleSystems();
        BuildPicture();
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

    void BuildColliders()
    {
        // 3D box colliders (invisible) so FallingPS world collision has something to hit.
        const float t = 0.5f;   // thickness
        const float zd = 10f;   // depth so z=0 particles always overlap
        MakeWall("Floor", new Vector3(0, -frameHalfH - t, 0), new Vector3(frameHalfW * 2 + t * 2, t * 2, zd));
        MakeWall("LeftWall", new Vector3(-frameHalfW - t, 0, 0), new Vector3(t * 2, frameHalfH * 2, zd));
        MakeWall("RightWall", new Vector3(frameHalfW + t, 0, 0), new Vector3(t * 2, frameHalfH * 2, zd));
    }

    void MakeWall(string name, Vector3 pos, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        var bc = go.AddComponent<BoxCollider>();
        bc.size = size;
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
        main.maxParticles = GRID * GRID + 16;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 1e9f;
        main.startSpeed = 0f;
        main.startSize = PIXEL;
        main.startColor = PIXEL_COLOR;
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

    // Letter "I" pattern on a 32x32 grid.
    static bool IsLetterI(int x, int y)
    {
        bool topBar = y >= 26 && y <= 31 && x >= 6 && x <= 25;
        bool bottomBar = y >= 0 && y <= 5 && x >= 6 && x <= 25;
        bool stem = x >= 13 && x <= 18;
        return topBar || bottomBar || stem;
    }

    void BuildPicture()
    {
        hanging.Clear();
        for (int y = 0; y < GRID; y++)
            for (int x = 0; x < GRID; x++)
                if (IsLetterI(x, y))
                    hanging.Add(GridToWorld(x, y));
    }

    Vector3 GridToWorld(int x, int y)
    {
        float wx = (x - (GRID - 1) * 0.5f) * PIXEL;
        float wy = (y - (GRID - 1) * 0.5f) * PIXEL + CENTER_Y;
        return new Vector3(wx, wy, 0f);
    }

    // Push the current hanging list into HangingPS as static particles.
    void UploadHanging()
    {
        int n = hanging.Count;
        var arr = new ParticleSystem.Particle[Mathf.Max(1, n)];
        for (int i = 0; i < n; i++)
        {
            arr[i].position = hanging[i];
            arr[i].velocity = Vector3.zero;
            arr[i].startSize = PIXEL;
            arr[i].startColor = PIXEL_COLOR;
            arr[i].startLifetime = 1e9f;
            arr[i].remainingLifetime = 1e9f;
        }
        hangingPS.SetParticles(arr, n);
    }

    void EmitFalling(Vector3 pos)
    {
        var ep = new ParticleSystem.EmitParams();
        ep.position = pos;
        ep.velocity = Vector3.zero;
        ep.startSize = PIXEL;
        ep.startColor = PIXEL_COLOR;
        ep.startLifetime = 1e9f;
        fallingPS.Emit(ep, 1);
    }

    void Update()
    {
        // Mouse (editor / desktop)
        if (Input.GetMouseButtonDown(0)) BeginSwipe(Input.mousePosition);
        else if (Input.GetMouseButtonUp(0)) EndSwipe(Input.mousePosition);

        // Touch (mobile)
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Began) BeginSwipe(t.position);
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) EndSwipe(t.position);
        }
    }

    void BeginSwipe(Vector3 screen)
    {
        downPos = ScreenToWorld(screen);
        dragging = true;
    }

    void EndSwipe(Vector3 screen)
    {
        if (!dragging) return;
        dragging = false;
        DoCut(downPos, ScreenToWorld(screen));
    }

    Vector3 ScreenToWorld(Vector3 screen)
    {
        screen.z = -cam.transform.position.z; // distance to z=0 plane
        var w = cam.ScreenToWorldPoint(screen);
        w.z = 0f;
        return w;
    }

    void DoCut(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        if (d.magnitude < MIN_SWIPE) return;
        if (hanging.Count == 0) return;

        var sidePos = new List<Vector3>();
        var sideNeg = new List<Vector3>();
        foreach (var p in hanging)
        {
            // sign of cross(d, p-a) decides the side of the infinite line a->b
            float cross = d.x * (p.y - a.y) - d.y * (p.x - a.x);
            if (cross > 0f) sidePos.Add(p); else sideNeg.Add(p);
        }

        // Line must actually cross the shape (both sides non-empty).
        if (sidePos.Count == 0 || sideNeg.Count == 0) return;

        // Smaller side falls.
        List<Vector3> fall = sidePos.Count <= sideNeg.Count ? sidePos : sideNeg;
        List<Vector3> stay = sidePos.Count <= sideNeg.Count ? sideNeg : sidePos;

        foreach (var p in fall) EmitFalling(p);

        hanging.Clear();
        hanging.AddRange(stay);
        UploadHanging();
    }
}
