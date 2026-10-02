using System.Collections.Generic;
using UnityEngine;

// Dropping stones: the aim, the stones themselves, what they hit and the cracks
// they leave behind.
//
// The stones are ordinary Unity 2D physics — a Rigidbody2D and a CircleCollider2D
// each — and every crystal still on the board is a PolygonCollider2D cut from the
// very polygon it is drawn with. A hole gets no collider at all, because it is
// drawn as nothing: what looks empty is empty and what looks solid is solid. The
// two walls are the only thing that is solid without being drawn, and all they do
// is keep a stone from flying off the side of the screen.
//
// All of it lives in BOARD coordinates, on a root that never moves. The board
// scrolls by moving its visual root alone, and each stone is drawn on that same
// root at the position its body holds — so a scroll is the camera moving past the
// shaft, not the shaft moving under the stones. Nothing in the simulation ever
// sees it: no collider is dragged through a stone, a stone resting on a crystal
// stays resting on it while the view slides, and the scroll never has to wait for
// the stones to settle.
public partial class MarbleDown : MonoBehaviour
{
    [Header("Drop")]
    // Hits a layer takes before it gives. Every one short of the last cracks it;
    // the last one breaks it. Each layer of a double, a triple or a stone shell
    // counts on its own. The gift cells (energy, jar) ignore this: one hit opens them.
    [Min(1)] public int hitsToBreak = 2;
    // Stone radius, as a fraction of a cell. Read when the level is built — the drop
    // lane under the HUD is sized to hold it — so a change shows after a Restart.
    [Range(0.1f, 0.4f)] public float pebbleRadius = 0.2f;
    [Range(0f, 1f)] public float pebbleBounciness = 0.35f;
    [Range(0f, 1f)] public float pebbleFriction = 0.3f;
    // Gravity scale. Well over 1: at Unity's plain 9.81 a stone this size floats
    // down like a balloon, and the whole point is that it is heavy.
    public float pebbleGravity = 3f;
    // Letting go throws the stone down at this speed instead of releasing it at
    // rest. Without it a crystal right under the lane is struck at a crawl and the
    // first drop of a level reads as a nudge.
    public float pebbleThrowSpeed = 3f;
    // How fast a rolling stone loses its spin — rolling resistance, in effect.
    public float pebbleSpinDamping = 3f;
    // A contact slower than this along its normal is not a hit. It is what stops a
    // stone that has come to rest — or is just rolling across a crystal — from
    // pecking it to death, and it keeps the stone's own little rebounds after a
    // short drop from being a free second hit. A drop from high up still bounces
    // hard enough to strike twice, which is the reward for finding a long fall.
    public float pebbleMinHitSpeed = 2.5f;
    // The same stone and the same crystal inside this window are one hit: the
    // solver may report one contact more than once while it settles it.
    public float pebbleHitCooldown = 0.1f;
    public float pebbleRestSpeed = 0.3f;        // slower than this counts as lying still...
    public float pebbleRestTime = 0.3f;         // ...and after this long lying still it goes
    public float pebbleLifetime = 6f;           // a stone wedged somewhere goes regardless
    public float pebbleFadeTime = 0.25f;

    // Over the whole board, under the HUD band: a stone that bounces up past the
    // lane disappears behind the HUD instead of being painted over the jars.
    const int Z_AIM = 9, Z_GHOST = 10, Z_PEBBLE = 11;

    static readonly Color AIM_LINE = new Color(1f, 1f, 1f, 0.30f);
    static readonly Color AIM_GHOST = new Color(1f, 1f, 1f, 0.85f);
    static readonly Color AIM_IMPACT = new Color(1f, 1f, 1f, 0.28f);
    static readonly Color CRACK_COLOR = new Color(0.07f, 0.02f, 0.05f, 0.92f);

    class Pebble
    {
        public Rigidbody2D body;
        public SpriteRenderer sr;          // on the board's visual root, so it scrolls with the view
        public MarbleDownPebble probe;
        public Vector3 size;               // sprite scale at full size
        public float age, still;
        public float fade = -1f;           // below 0 while live; 0..1 while it goes
        public readonly Dictionary<Cell, float> lastHit = new Dictionary<Cell, float>();
    }

    readonly List<Pebble> pebbles = new List<Pebble>();
    readonly Dictionary<Collider2D, Cell> cellOfSolid = new Dictionary<Collider2D, Cell>();
    readonly RaycastHit2D[] castHits = new RaycastHit2D[32];
    readonly List<Vector3> crackVerts = new List<Vector3>();
    readonly List<int> crackTris = new List<int>();
    bool[,] exposed;                       // solid and open to the sky; see ComputeExposure
    Transform physicsRoot;
    PhysicsMaterial2D pebbleMaterial;
    Sprite pebbleSprite;
    SpriteRenderer aimLine, aimGhost, aimImpact;
    float pebbleR;                         // stone radius in board units, fixed for the level
    float dropY;                           // centre line of the drop lane, world y
    bool aiming;
    float aimX;

    // Stones still able to hit something. A fading one is not counted.
    public int PebblesInFlight
    {
        get
        {
            int n = 0;
            foreach (var p in pebbles) if (p.fade < 0f) n++;
            return n;
        }
    }

    bool PebblesLive() => PebblesInFlight > 0;

    // ---- build ------------------------------------------------------------

    void BuildDrop()
    {
        pebbles.Clear();
        cellOfSolid.Clear();
        aiming = false;

        // both survive a Restart: neither depends on the level
        if (pebbleSprite == null) pebbleSprite = MakePebbleSprite(128);
        if (pebbleMaterial == null) pebbleMaterial = new PhysicsMaterial2D("Pebble");

        physicsRoot = new GameObject("Physics").transform;
        physicsRoot.SetParent(transform, false);

        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
            {
                var cell = board[c, r];
                if (cell.kind == Kind.Missing) continue;          // drawn as nothing, so it is nothing

                var go = new GameObject("Solid" + c + "_" + r);
                go.transform.SetParent(physicsRoot, false);
                go.transform.localPosition = cell.root.localPosition;
                var solid = go.AddComponent<PolygonCollider2D>();
                solid.SetPath(0, cell.inner);
                cell.solid = solid;
                cellOfSolid[solid] = cell;
                SyncSolid(cell);
            }

        // The walls. The crystals stop a gap short of the board edge and the board
        // fill leaves a margin past that, so without them a stone that strikes the
        // outer column at an angle just leaves the screen.
        float half = boardWidth * cellSize * 0.5f;
        float top = windowTop + 3f * cellH;                       // over the lane at any scroll
        float bottom = boardTopY - (boardHeight + 4) * cellH;     // well past the door
        for (int side = -1; side <= 1; side += 2)
        {
            var wall = new GameObject(side < 0 ? "WallLeft" : "WallRight");
            wall.transform.SetParent(physicsRoot, false);
            wall.transform.localPosition = new Vector3(side * (half + 0.5f), (top + bottom) * 0.5f, 0f);
            wall.AddComponent<BoxCollider2D>().size = new Vector2(1f, top - bottom);
        }

        aimLine = MakeSprite(transform, "AimLine", blankSprite, Z_AIM);
        aimGhost = MakeSprite(transform, "AimGhost", pebbleSprite, Z_GHOST);
        aimImpact = MakeSprite(transform, "AimImpact", pebbleSprite, Z_GHOST);
        FitSprite(aimGhost, pebbleSprite, 2f * pebbleR, 2f * pebbleR);
        FitSprite(aimImpact, pebbleSprite, 2f * pebbleR, 2f * pebbleR);
        aimLine.color = AIM_LINE;
        aimGhost.color = AIM_GHOST;
        aimImpact.color = AIM_IMPACT;
        aimLine.enabled = aimGhost.enabled = aimImpact.enabled = false;
    }

    // Restart destroys the old level with Destroy, which waits for the end of the
    // frame. Switched off now, the old shaft cannot take part in a physics step
    // that happens before then, and no stone of it can call back into the new one.
    void ResetDrop()
    {
        foreach (var p in pebbles) if (p.probe != null) p.probe.hit = null;
        pebbles.Clear();
        cellOfSolid.Clear();
        if (physicsRoot != null) physicsRoot.gameObject.SetActive(false);
        physicsRoot = null;
        aimLine = aimGhost = aimImpact = null;
        exposed = null;
        aiming = false;
    }

    // A crystal is solid while it is on the board and below the line. Broken is
    // gone; culled is sky.
    void SyncSolid(Cell cell)
    {
        if (cell.solid != null) cell.solid.enabled = !cell.broken && !cell.hidden;
    }

    // ---- aiming -----------------------------------------------------------

    // Finger down on the board picks up a stone. While the finger is held the stone
    // follows it along the lane, and letting go drops it. Only the press has to
    // land on the board — the finger may wander into the HUD after that and the
    // stone still goes where it was last seen. A press under the board with the
    // door open is still the way out.
    void HandleInput()
    {
        if (finished) { aiming = false; return; }

        bool down = Input.GetMouseButtonDown(0);
        bool up = Input.GetMouseButtonUp(0);
        bool cancel = false;
        Vector3 screen = Input.mousePosition;
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            down = t.phase == TouchPhase.Began;
            up = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            cancel = t.phase == TouchPhase.Canceled;              // the OS took the touch: not a throw
            screen = t.position;
        }

        screen.z = -cam.transform.position.z;
        Vector3 world = cam.ScreenToWorldPoint(screen);

        if (down)
        {
            if (world.y > windowTop) return;                      // that's the HUD, not the board
            if (world.y - scrollY < boardTopY - boardHeight * cellH && DoorOpen()) { TryExit(); return; }
            // nothing to pay with: the finger does nothing rather than aim a throw
            // that cannot happen
            aiming = energy > 0;
        }
        if (!aiming) return;

        aimX = ClampAim(world.x);
        if (!up) return;

        aiming = false;
        if (!cancel) Drop(aimX);
    }

    float ClampAim(float x)
    {
        float limit = Mathf.Max(0f, boardWidth * cellSize * 0.5f - pebbleR - 0.002f);
        return Mathf.Clamp(x, -limit, limit);
    }

    // Takes the energy and lets a stone go at `x` (world units, clamped to the
    // shaft). The price is paid here, once, whatever the stone goes on to do — one
    // that clears three crystals costs what one click did, and so does one that
    // hits nothing. No energy, no stone.
    bool Drop(float x)
    {
        if (finished || energy <= 0 || physicsRoot == null) return false;

        energy--;
        var at = new Vector3(ClampAim(x), dropY, 0f);
        SpawnFloater(at, "-1", COST_TEXT);
        SpawnPebble(at);
        RefreshHud();
        return true;
    }

    // For driving the game from outside (tests, the editor): drop a stone at this
    // x, in world units. Same rules as a finger: it costs energy and needs some.
    public bool DropAt(float x) => Drop(x);

    void SpawnPebble(Vector3 world)
    {
        var local = new Vector2(world.x, world.y - scrollY);     // board coords: see the top of the file

        var go = new GameObject("Pebble");
        go.transform.SetParent(physicsRoot, false);
        go.transform.localPosition = local;

        var body = go.AddComponent<Rigidbody2D>();
        body.gravityScale = pebbleGravity;
        body.interpolation = RigidbodyInterpolation2D.Interpolate;
        // a stone falling the length of the screen covers most of its own width per
        // step; discrete detection would let it through a thin crystal now and then
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        body.velocity = Vector2.down * pebbleThrowSpeed;
        // Box2D has no rolling resistance: a ball that rolls into the dip between
        // two crystal tops rocks there for as long as you care to watch, and never
        // counts as lying still. Damping the spin is what brings it to rest.
        body.angularDrag = pebbleSpinDamping;

        // read on every throw, so the inspector is live while you play
        pebbleMaterial.bounciness = pebbleBounciness;
        pebbleMaterial.friction = pebbleFriction;
        var col = go.AddComponent<CircleCollider2D>();
        col.radius = pebbleR;
        col.sharedMaterial = pebbleMaterial;

        var sr = MakeSprite(boardRoot, "Stone", pebbleSprite, Z_PEBBLE);
        FitSprite(sr, pebbleSprite, 2f * pebbleR, 2f * pebbleR);
        sr.transform.localPosition = local;

        var p = new Pebble { body = body, sr = sr, size = sr.transform.localScale };
        p.probe = go.AddComponent<MarbleDownPebble>();
        p.probe.hit = contact => OnPebbleContact(p, contact);
        pebbles.Add(p);
    }

    // The ghost hangs in the lane at the finger's x; the line runs from it down to
    // where the stone would first touch a crystal if let go now, and a fainter
    // ghost sits at that spot. The probe is the real thing — a CircleCast of the
    // stone's own radius against the colliders it will meet — so the line can never
    // promise a gap the stone does not fit through.
    void DrawAim()
    {
        if (aimGhost == null) return;

        bool show = aiming && !finished;
        aimGhost.enabled = aimLine.enabled = show;
        aimImpact.enabled = false;
        if (!show) return;

        var from = new Vector2(aimX, dropY - scrollY);           // board coords
        float reach = from.y - (boardTopY - (boardHeight + 1) * cellH);
        bool hit;
        float travel = CastDrop(from, reach, out hit);
        float impactY = dropY - travel;                         // world

        aimGhost.transform.position = new Vector3(aimX, dropY, 0f);

        aimLine.enabled = travel > 1e-3f;
        aimLine.transform.position = new Vector3(aimX, (dropY + impactY) * 0.5f, 0f);
        FitSprite(aimLine, blankSprite, pebbleR * 0.32f, Mathf.Max(1e-3f, travel));

        // overlapping the ghost it only muddies it
        aimImpact.enabled = hit && travel > 2.2f * pebbleR;
        aimImpact.transform.position = new Vector3(aimX, impactY, 0f);
    }

    // How far a stone dropped from `from` falls before it first touches a crystal;
    // `reach` if it falls clean out of the shaft. Walls and other stones do not
    // count: the line is about what the stone will hit, not what it rubs past.
    float CastDrop(Vector2 from, float reach, out bool hit)
    {
        hit = false;
        Vector2 origin = physicsRoot.TransformPoint(from);
        int n = Physics2D.CircleCast(origin, pebbleR, Vector2.down, new ContactFilter2D().NoFilter(), castHits, reach);

        float best = reach;
        for (int i = 0; i < n; i++)
        {
            if (!cellOfSolid.ContainsKey(castHits[i].collider)) continue;
            if (castHits[i].distance >= best) continue;
            best = castHits[i].distance;
            hit = true;
        }
        return best;
    }

    // ---- stones -----------------------------------------------------------

    // A stone goes once it has lain still for a moment, has been around too long
    // (wedged somewhere), or has fallen out through the bottom of the shaft. It
    // fades rather than vanishing, and while it fades it can no longer hurt anything.
    void UpdatePebbles(float dt)
    {
        if (physicsRoot == null) return;
        float floor = boardTopY - boardHeight * cellH - pebbleR;   // out past the last row

        for (int i = pebbles.Count - 1; i >= 0; i--)
        {
            var p = pebbles[i];
            if (p.body == null) { pebbles.RemoveAt(i); continue; }

            if (p.fade < 0f)
            {
                p.age += dt;
                bool still = p.body.IsSleeping() || p.body.velocity.sqrMagnitude < pebbleRestSpeed * pebbleRestSpeed;
                p.still = still ? p.still + dt : 0f;
                float y = physicsRoot.InverseTransformPoint(p.body.position).y;
                if (p.still >= pebbleRestTime || p.age >= pebbleLifetime || y < floor) p.fade = 0f;
                continue;
            }

            p.fade += dt / Mathf.Max(0.05f, pebbleFadeTime);
            if (p.fade < 1f) continue;

            if (p.probe != null) p.probe.hit = null;
            Destroy(p.body.gameObject);
            if (p.sr != null) Destroy(p.sr.gameObject);
            pebbles.RemoveAt(i);
        }
    }

    // The sprite takes the body's interpolated position but never its rotation: the
    // light is baked into it, and a highlight rolling round with the stone reads as
    // the sun spinning, not as a stone rolling.
    void DrawPebbles()
    {
        if (physicsRoot == null) return;
        foreach (var p in pebbles)
        {
            if (p.body == null || p.sr == null) continue;
            Vector3 at = physicsRoot.InverseTransformPoint(p.body.transform.position);
            p.sr.transform.localPosition = new Vector3(at.x, at.y, 0f);

            float f = p.fade < 0f ? 0f : Mathf.Clamp01(p.fade);
            p.sr.color = new Color(1f, 1f, 1f, 1f - Mathf.SmoothStep(0f, 1f, f));
            p.sr.transform.localScale = p.size * Mathf.Lerp(1f, 0.6f, f);
        }
    }

    // ---- impact -----------------------------------------------------------

    // Every new contact between a stone and a crystal is one hit — the drop itself
    // and every ricochet after it. Two things are filtered out. The same contact
    // reported again while the solver settles it (same stone, same crystal, inside
    // the cooldown) is the same hit. And a slow contact is no hit at all, measured
    // along the contact normal: a stone rolling across a crystal or resting on it
    // never chips it however long it stays, while a glancing blow still counts for
    // exactly as hard as it struck.
    void OnPebbleContact(Pebble p, Collision2D contact)
    {
        if (finished || p.fade >= 0f || contact.contactCount == 0) return;

        Cell cell;
        if (!cellOfSolid.TryGetValue(contact.collider, out cell)) return;     // a wall, or another stone

        var point = contact.GetContact(0);
        float speed = Mathf.Abs(Vector2.Dot(contact.relativeVelocity, point.normal));
        if (speed < pebbleMinHitSpeed) return;

        var owner = Owner(cell);
        float last;
        if (p.lastHit.TryGetValue(owner, out last) && Time.time - last < pebbleHitCooldown) return;
        p.lastHit[owner] = Time.time;

        Vector2 at = (Vector2)physicsRoot.InverseTransformPoint(point.point) - (Vector2)cell.root.localPosition;
        Hit(cell, at);
    }

    // ---- cracks -----------------------------------------------------------

    // Drawn, not textured: a few tapered strokes running out from where the stone
    // landed, bending a little at every step and stopping short of the outline,
    // with the odd short branch. Dark and fairly thick on purpose — they have to
    // read on all five colours and on grey stone, at a glance, as "one more and it
    // goes". Every hit adds its own star, so under a bigger hitsToBreak a crystal
    // shows each blow it has taken.
    void DrawCracks(Cell cell)
    {
        var mr = cell.crackMesh;
        if (mr == null || cell.inner == null) return;
        var filter = mr.GetComponent<MeshFilter>();
        var mesh = filter.sharedMesh;
        if (mesh == null) { mesh = new Mesh { name = "Cracks" }; filter.sharedMesh = mesh; }

        crackVerts.Clear();
        crackTris.Clear();

        var poly = cell.inner;
        Vector2 mid = Vector2.zero;
        foreach (var v in poly) mid += v;
        mid /= poly.Length;
        float reach = 0f;
        foreach (var v in poly) reach = Mathf.Max(reach, (v - mid).magnitude);

        var bound = Shrink(poly, 0.9f);       // stop short of the outline, or they read as seams
        float w0 = cellSize * 0.055f;

        for (int i = 0; i < cell.cracks.Count; i++)
        {
            // Its own generator, seeded off the cell and the hit: the same crack comes
            // back every time the mesh is rebuilt, and Unity's Random — which picks the
            // jars — is never touched by a cosmetic.
            var rng = new System.Random(activeSeed ^ (cell.col * 73856093) ^ (cell.row * 19349663)
                                      ^ (i * 83492791) ^ (cell.nest.Count * 2654435));

            // a contact sits ON the outline: start a little way in, or half of every
            // stroke is clipped away before it is drawn
            Vector2 at = Vector2.Lerp(cell.cracks[i], mid, 0.3f);
            if (!PointInPoly(at, bound)) at = Vector2.Lerp(at, mid, 0.6f);

            int rays = 4 + rng.Next(2);
            float spin = (float)rng.NextDouble() * Mathf.PI * 2f;
            for (int k = 0; k < rays; k++)
            {
                float ang = spin + k * Mathf.PI * 2f / rays + Jitter(rng, 0.35f);
                Vector2 pos = at;
                float w = w0;
                for (int seg = 0; seg < 3; seg++)
                {
                    ang += Jitter(rng, 0.45f);
                    var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    Vector2 next = pos + dir * reach * Mathf.Lerp(0.22f, 0.36f, (float)rng.NextDouble());
                    bool edge = !PointInPoly(next, bound);
                    if (edge) next = LastInside(pos, next, bound);
                    CrackStroke(pos, next, w, w * 0.6f);

                    if (seg == 0 && rng.NextDouble() < 0.5)
                    {
                        float side = ang + (rng.NextDouble() < 0.5 ? -1f : 1f) * Mathf.Lerp(0.6f, 1.1f, (float)rng.NextDouble());
                        Vector2 twig = next + new Vector2(Mathf.Cos(side), Mathf.Sin(side)) * reach * 0.2f;
                        if (!PointInPoly(twig, bound)) twig = LastInside(next, twig, bound);
                        CrackStroke(next, twig, w * 0.6f, w * 0.25f);
                    }

                    w *= 0.6f;
                    pos = next;
                    if (edge) break;
                }
            }
        }

        mesh.Clear();
        mesh.SetVertices(crackVerts);
        mesh.SetTriangles(crackTris, 0);
        mesh.RecalculateBounds();
        PolyColor(mr, CRACK_COLOR);
    }

    static float Jitter(System.Random rng, float amount) => ((float)rng.NextDouble() * 2f - 1f) * amount;

    // Bisect a stroke for the last point still inside the outline.
    static Vector2 LastInside(Vector2 inside, Vector2 outside, Vector2[] poly)
    {
        for (int i = 0; i < 10; i++)
        {
            Vector2 m = (inside + outside) * 0.5f;
            if (PointInPoly(m, poly)) inside = m; else outside = m;
        }
        return inside;
    }

    // One tapered quad from a to b. The joints are left square: at this width a
    // rounded one is not worth the vertices.
    void CrackStroke(Vector2 a, Vector2 b, float wa, float wb)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 1e-8f) return;
        Vector2 n = new Vector2(-d.y, d.x).normalized;

        int i = crackVerts.Count;
        crackVerts.Add(a + n * (wa * 0.5f));
        crackVerts.Add(a - n * (wa * 0.5f));
        crackVerts.Add(b - n * (wb * 0.5f));
        crackVerts.Add(b + n * (wb * 0.5f));
        crackTris.Add(i); crackTris.Add(i + 1); crackTris.Add(i + 2);
        crackTris.Add(i); crackTris.Add(i + 2); crackTris.Add(i + 3);
    }

    // ---- art --------------------------------------------------------------

    // A stone, drawn in code like every other sprite here: lit from the upper left,
    // a cool grey with some grain so it reads as rock and not as a steel ball, a
    // darker rim to hold its edge against the dark shaft, and one hard highlight.
    Sprite MakePebbleSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        var px = new Color[size * size];
        float R = size * 0.5f;
        var light = new Vector3(-0.5f, 0.62f, 0.6f).normalized;
        var spot = new Vector2(-0.36f, 0.40f);

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - R) / R, dy = (y + 0.5f - R) / R;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01((1f - d) * R + 0.5f);
                if (alpha <= 0f) { px[y * size + x] = Color.clear; continue; }

                float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Min(1f, d * d)));
                float lambert = Mathf.Clamp01(Vector3.Dot(new Vector3(dx, dy, nz), light));
                float grain = (Mathf.PerlinNoise(x * 0.08f, y * 0.08f) - 0.5f) * 0.12f
                            + (Mathf.PerlinNoise(x * 0.27f + 31.7f, y * 0.27f + 11.3f) - 0.5f) * 0.07f;
                float v = Mathf.Lerp(0.22f, 0.70f, lambert) + grain;
                v *= Mathf.Lerp(1f, 0.62f, Mathf.InverseLerp(0.80f, 1f, d));
                float spec = Mathf.Pow(Mathf.Clamp01(1f - Vector2.Distance(new Vector2(dx, dy), spot) / 0.28f), 2f) * 0.5f;
                px[y * size + x] = new Color(v * 0.96f + spec, v * 0.97f + spec, v * 1.03f + spec, alpha);
            }

        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
