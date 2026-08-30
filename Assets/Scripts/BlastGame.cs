using System.Collections.Generic;
using UnityEngine;

// Variation 2 of the pixel-sorting prototype.
// Same machine as SliceGame underneath (picture -> funnel -> tube -> jars), but
// the picture is taken apart with placed EXPLOSIVES instead of finger swipes:
//   * Target spots sit on the picture. They are planned once, up front, so that
//     bomb blasts on all of them would cover the whole picture — enough, not
//     more. Half of them are shown; the rest wait in a hidden pool.
//   * Using a spot removes it and reveals the next one from the pool, so the
//     board keeps roughly the same number of targets while they last.
//   * Ammo count == spot count, split 2/3 bombs / 1/3 rockets.
//   * BOMB   erases a disc around the spot.
//   * ROCKET erases a horizontal band across the whole picture at the spot.
//   * Only the erased pixels leave the picture. Whatever a blast cuts loose keeps
//     hanging where it is — falling-off chunks are SliceGame's rule, not this one.
// The skill is still ORDER: blow up the colors the active jars are asking for.
// Wrong order feeds the tube colors nobody wants and it CLOGs.
public class BlastGame : SliceGame
{
    [Header("Blast")]
    // Bomb size is NOT fixed in pixels: a 24x24 picture and a 100x100 one should
    // both give the player a decent number of targets, so the radius is solved
    // from how many targets a level wants.
    public int targetSpots = 8;            // roughly how many spots a fresh plan aims for
    public float bombRadiusMinPixels = 1.6f;
    public int minVisibleSpots = 3;        // never show fewer than this while there is picture left
    public float spotMarkerPixels = 1.2f;  // marker radius, in picture pixels
    public int rocketHeightPixels = 3;     // rocket erases a band this many pixels tall
    public float blastImpulse = 5f;
    public float spotSpacing = 1.3f;       // spot lattice step = bomb radius * this
    [Range(0f, 1f)] public float bombShare = 0.667f;
    [Range(0f, 1f)] public float visibleShare = 0.5f;

    const int SPOT_ORDER = 120;
    const int BAR_ORDER = 118;

    static readonly Color SPOT_RING = new Color(0.05f, 0.05f, 0.16f);
    static readonly Color SPOT_HALO = Color.white;
    static readonly Color SPOT_DOT = new Color(0.90f, 0.15f, 0.20f);
    static readonly Color SPOT_HOT = new Color(1f, 0.78f, 0.10f);
    static readonly Color BAR_COLOR = new Color(0.72f, 0.55f, 0.92f);
    static readonly Color BOMB_COLOR = new Color(0.16f, 0.16f, 0.20f);
    static readonly Color ROCKET_COLOR = new Color(0.35f, 0.45f, 0.85f);
    static readonly Color BLAST_PREVIEW = new Color(1f, 0.25f, 0.25f);

    enum Weapon { None = -1, Bomb = 0, Rocket = 1 }

    class Spot
    {
        public int gx, gy;
        public Vector3 pos;
        public LineRenderer ring, halo, dot;
    }

    readonly List<Spot> active = new List<Spot>();
    readonly Queue<Vector2Int> hidden = new Queue<Vector2Int>();
    readonly int[] ammo = new int[2];

    float ammoBarY, ammoBarHalfH, ammoBarHalfW, slotHalf;
    readonly float[] slotX = new float[2];
    LineRenderer barVisual, blastPreview;
    readonly LineRenderer[] slotIcon = new LineRenderer[2];
    readonly TextMesh[] slotText = new TextMesh[2];
    TextMesh hintText;

    Weapon dragging = Weapon.None;
    Spot hovered;
    int visibleTarget;
    float bombRadius;                      // in picture pixels, solved once per level

    // ---- layout ---------------------------------------------------------

    // Squeeze the machine up a bit so the ammo bar fits under the jar preview.
    protected override void ConfigureBands()
    {
        funnelTopY = 0.36f * H;
        tubeTopY = 0.17f * H;
        tubeBotY = 0.01f * H;
        jarTopY = -0.13f * H;
        jarBottomY = -0.53f * H;
        previewY = -0.67f * H;
        pictureCenterY = 0.66f * H;

        pictureZoneH = 0.46f * H;
        pictureZoneW = 1.6f * W;

        ammoBarY = -0.87f * H;
        ammoBarHalfH = 0.09f * H;
        ammoBarHalfW = 0.42f * W;
        slotX[0] = -0.17f * W;
        slotX[1] = 0.17f * W;
        slotHalf = Mathf.Min(ammoBarHalfH * 0.62f, 0.055f * W);
    }

    // ---- build / teardown -----------------------------------------------

    protected override void Build()
    {
        base.Build();
        BuildSpots();
        BuildAmmoBar();
    }

    // no swipe cutting in this mode
    protected override void BuildCutPreviewVisual() { }

    protected override void Teardown()
    {
        base.Teardown();
        active.Clear();
        hidden.Clear();
        dragging = Weapon.None;
        hovered = null;
        barVisual = blastPreview = null;
        hintText = null;
        for (int i = 0; i < 2; i++) { slotIcon[i] = null; slotText[i] = null; ammo[i] = 0; }
        visibleTarget = 0;
        bombRadius = 0f;
    }

    // ---- spots ------------------------------------------------------------

    void BuildSpots()
    {
        ComputeBombRadius();
        var coords = PlanSpotCoords();
        StockUp(coords);
        visibleTarget = Mathf.Max(minVisibleSpots, Mathf.CeilToInt(coords.Count * visibleShare));
        TopUpVisible();
    }

    // Solve the hex lattice backwards: a step of r * spotSpacing tiles the picture
    // into cells of step^2 * 0.87, and we want targetSpots of them. Done once on
    // the full picture so the bomb keeps the same size all level.
    void ComputeBombRadius()
    {
        float cells = Mathf.Max(1, targetSpots) * spotSpacing * spotSpacing * 0.87f;
        bombRadius = Mathf.Max(bombRadiusMinPixels, Mathf.Sqrt(hanging.Count / cells));
    }

    // Pool a planned wave of spots and hand out the ammo that goes with it:
    // one charge per spot, two thirds of them bombs.
    void StockUp(List<Vector2Int> coords)
    {
        if (coords.Count == 0) return;
        int bombs = Mathf.RoundToInt(coords.Count * bombShare);
        ammo[(int)Weapon.Bomb] += bombs;
        ammo[(int)Weapon.Rocket] += coords.Count - bombs;
        foreach (var c in coords) hidden.Enqueue(c);
    }

    // Keep the board stocked with visible targets. Targets never run out: as long
    // as there is picture left, a dry pool is refilled with a fresh wave planned
    // over whatever survived (rockets clear differently than bombs, so one plan
    // up front is never enough to finish the level).
    void TopUpVisible()
    {
        while (active.Count < visibleTarget)
        {
            if (hidden.Count == 0 && hanging.Count > 0) StockUp(PlanSpotCoords());
            if (hidden.Count == 0) break;
            ActivateSpot(hidden.Dequeue());
        }
        RefreshAmmo();
    }

    // Enough spots to cover every pixel with a bomb blast and not a lot more:
    // an offset ("hex") lattice at ~blast spacing first, then a few extra for
    // whatever the lattice missed (thin limbs, holes, edges).
    List<Vector2Int> PlanSpotCoords()
    {
        var spots = new List<Vector2Int>();
        if (hanging.Count == 0) return spots;

        bool[,] occupied = BuildHangingGrid();
        var covered = new bool[texW, texH];
        float r = bombRadius;
        float step = Mathf.Max(2f, r * spotSpacing);

        int row = 0;
        for (float fy = step * 0.5f; fy < texH; fy += step * 0.87f, row++)
        {
            float x0 = step * 0.5f + (row % 2 == 0 ? 0f : step * 0.5f);
            for (float fx = x0; fx < texW; fx += step)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt(fx), 0, texW - 1);
                int gy = Mathf.Clamp(Mathf.RoundToInt(fy), 0, texH - 1);
                // pull the lattice point onto the picture so no marker floats in the void
                if (!NearestOccupied(gx, gy, occupied, r * 0.8f, out var c)) continue;
                spots.Add(c);
                MarkCovered(c.x, c.y, covered, r);
            }
        }

        var leftovers = new List<Vector2Int>();
        for (int y = 0; y < texH; y++)
            for (int x = 0; x < texW; x++)
                if (occupied[x, y] && !covered[x, y]) leftovers.Add(new Vector2Int(x, y));
        Shuffle(leftovers);
        foreach (var c in leftovers)
        {
            if (covered[c.x, c.y]) continue;
            spots.Add(c);
            MarkCovered(c.x, c.y, covered, r);
        }

        Shuffle(spots);   // positions are fixed; only the reveal order is rolled
        return spots;
    }

    bool NearestOccupied(int cx, int cy, bool[,] occupied, float r, out Vector2Int found)
    {
        found = default;
        int ri = Mathf.CeilToInt(r);
        float best = float.MaxValue;
        for (int y = cy - ri; y <= cy + ri; y++)
        {
            if (y < 0 || y >= texH) continue;
            for (int x = cx - ri; x <= cx + ri; x++)
            {
                if (x < 0 || x >= texW || !occupied[x, y]) continue;
                float d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d2 > r * r || d2 >= best) continue;
                best = d2;
                found = new Vector2Int(x, y);
            }
        }
        return best < float.MaxValue;
    }

    void MarkCovered(int cx, int cy, bool[,] covered, float r)
    {
        int ri = Mathf.CeilToInt(r);
        for (int y = cy - ri; y <= cy + ri; y++)
        {
            if (y < 0 || y >= texH) continue;
            for (int x = cx - ri; x <= cx + ri; x++)
            {
                if (x < 0 || x >= texW) continue;
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) covered[x, y] = true;
            }
        }
    }

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    void ActivateSpot(Vector2Int c)
    {
        var s = new Spot { gx = c.x, gy = c.y, pos = PixelToWorld(c.x, c.y) };
        float rr = Mathf.Max(pixel * spotMarkerPixels, 0.045f);

        s.ring = MakeLine("SpotRing", SPOT_RING, rr * 0.42f, true, Vector3.zero);
        s.halo = MakeLine("SpotHalo", SPOT_HALO, rr * 0.30f, true, Vector3.zero);
        s.dot = MakeLine("SpotDot", SPOT_DOT, rr * 0.44f, true, Vector3.zero);
        UpdateCircle(s.ring, s.pos, rr * 0.80f);
        UpdateCircle(s.halo, s.pos, rr * 0.52f);
        UpdateCircle(s.dot, s.pos, rr * 0.22f);
        s.ring.sortingOrder = SPOT_ORDER;
        s.halo.sortingOrder = SPOT_ORDER + 1;
        s.dot.sortingOrder = SPOT_ORDER + 2;

        active.Add(s);
    }

    void ConsumeSpot(Spot s)
    {
        DropSpot(s);
        PruneDeadSpots();
        TopUpVisible();
    }

    void DropSpot(Spot s)
    {
        if (hovered == s) hovered = null;
        active.Remove(s);
        if (s.ring != null) Destroy(s.ring.gameObject);
        if (s.halo != null) Destroy(s.halo.gameObject);
        if (s.dot != null) Destroy(s.dot.gameObject);
    }

    // A marker left standing over an erased area is a dead target — a bomb there
    // would hit nothing. Clear those out so the board only shows usable spots.
    void PruneDeadSpots()
    {
        if (active.Count == 0) return;
        bool[,] occupied = BuildHangingGrid();
        float r = bombRadius;
        for (int i = active.Count - 1; i >= 0; i--)
            if (!NearestOccupied(active[i].gx, active[i].gy, occupied, r, out _))
                DropSpot(active[i]);
    }

    void SetSpotHot(Spot s, bool hot)
    {
        if (s == null || s.ring == null) return;
        s.ring.startColor = s.ring.endColor = hot ? SPOT_HOT : SPOT_RING;
    }

    // ---- ammo bar ---------------------------------------------------------

    void BuildAmmoBar()
    {
        barVisual = MakeLine("AmmoBar", BAR_COLOR, ammoBarHalfH * 2f, false,
            new Vector3(-ammoBarHalfW, ammoBarY, 0f), new Vector3(ammoBarHalfW, ammoBarY, 0f));
        barVisual.numCapVertices = 8;
        barVisual.sortingOrder = BAR_ORDER;

        for (int i = 0; i < 2; i++)
        {
            var c = new Vector3(slotX[i], ammoBarY, 0f);
            if (i == (int)Weapon.Bomb)
            {
                // circle of radius R drawn with width R renders as a filled disc
                slotIcon[i] = MakeLine("SlotBomb", BOMB_COLOR, slotHalf, true, Vector3.zero);
                UpdateCircle(slotIcon[i], c, slotHalf * 0.5f);
            }
            else
            {
                slotIcon[i] = MakeLine("SlotRocket", ROCKET_COLOR, slotHalf * 1.5f, false,
                    new Vector3(c.x - slotHalf * 0.55f, c.y, 0f), new Vector3(c.x + slotHalf * 0.55f, c.y, 0f));
            }
            slotIcon[i].sortingOrder = BAR_ORDER + 1;
            slotText[i] = MakeText("SlotNum" + i, c + new Vector3(slotHalf * 1.5f, -slotHalf * 0.6f, 0f), 0.26f, Color.white);
        }
        RefreshAmmo();

        hintText = MakeText("Hint", new Vector3(0f, ammoBarY + ammoBarHalfH + 0.045f * H, 0f), 0.18f,
            new Color(0.4f, 0.4f, 0.45f));
        hintText.text = "drag a bomb / rocket onto a target";

        blastPreview = MakeLine("BlastPreview", BLAST_PREVIEW, Mathf.Max(pixel * 0.25f, 0.02f), true, Vector3.zero);
        blastPreview.sortingOrder = SPOT_ORDER + 5;
        blastPreview.enabled = false;
    }

    void RefreshAmmo()
    {
        for (int i = 0; i < 2; i++)
        {
            if (slotText[i] != null) slotText[i].text = ammo[i].ToString();
            SetSlotHot(i, false);
        }
    }

    void SetSlotHot(int i, bool hot)
    {
        if (slotIcon[i] == null) return;
        Color b = i == (int)Weapon.Bomb ? BOMB_COLOR : ROCKET_COLOR;
        if (ammo[i] <= 0) b = Color.Lerp(b, BAR_COLOR, 0.65f);          // spent = washed out
        else if (hot) b = Color.Lerp(b, Color.white, 0.45f);
        slotIcon[i].startColor = slotIcon[i].endColor = b;
    }

    // ---- input ------------------------------------------------------------

    protected override void HandleInput()
    {
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            Vector3 w = ScreenToWorld(t.position);
            if (t.phase == TouchPhase.Began) BeginDrag(w);
            else if (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary) MoveDrag(w);
            else EndDrag(w);
            return;
        }

        if (Input.GetMouseButtonDown(0)) BeginDrag(ScreenToWorld(Input.mousePosition));
        else if (Input.GetMouseButton(0)) MoveDrag(ScreenToWorld(Input.mousePosition));
        else if (Input.GetMouseButtonUp(0)) EndDrag(ScreenToWorld(Input.mousePosition));
    }

    void BeginDrag(Vector3 world)
    {
        dragging = SlotAt(world);
        if (dragging != Weapon.None) SetSlotHot((int)dragging, true);
        MoveDrag(world);
    }

    void MoveDrag(Vector3 world)
    {
        if (dragging == Weapon.None) return;
        var next = NearestSpot(world);
        if (next != hovered)
        {
            SetSpotHot(hovered, false);
            SetSpotHot(next, true);
            hovered = next;
        }
        UpdateBlastPreview();
    }

    void EndDrag(Vector3 world)
    {
        if (dragging != Weapon.None)
        {
            MoveDrag(world);
            if (hovered != null) Detonate(hovered, dragging);
            SetSlotHot((int)dragging, false);
        }
        SetSpotHot(hovered, false);
        hovered = null;
        dragging = Weapon.None;
        if (blastPreview != null) blastPreview.enabled = false;
    }

    Weapon SlotAt(Vector3 world)
    {
        if (Mathf.Abs(world.y - ammoBarY) > ammoBarHalfH) return Weapon.None;
        for (int i = 0; i < 2; i++)
            if (ammo[i] > 0 && Mathf.Abs(world.x - slotX[i]) <= slotHalf * 2f)
                return (Weapon)i;
        return Weapon.None;
    }

    Spot NearestSpot(Vector3 world)
    {
        float snap = Mathf.Max(pixel * 8f, 0.45f);
        Spot best = null;
        float bd = snap * snap;
        foreach (var s in active)
        {
            float d2 = (s.pos - world).sqrMagnitude;
            if (d2 < bd) { bd = d2; best = s; }
        }
        return best;
    }

    void UpdateBlastPreview()
    {
        if (blastPreview == null) return;
        if (dragging == Weapon.None || hovered == null) { blastPreview.enabled = false; return; }

        blastPreview.enabled = true;
        if (dragging == Weapon.Bomb)
        {
            UpdateCircle(blastPreview, hovered.pos, bombRadius * pixel);
        }
        else
        {
            float hw = texW * pixel * 0.5f + pixel;
            float hh = rocketHeightPixels * pixel * 0.5f;
            float y = hovered.pos.y;
            blastPreview.positionCount = 4;
            blastPreview.SetPosition(0, new Vector3(-hw, y - hh, 0f));
            blastPreview.SetPosition(1, new Vector3(hw, y - hh, 0f));
            blastPreview.SetPosition(2, new Vector3(hw, y + hh, 0f));
            blastPreview.SetPosition(3, new Vector3(-hw, y + hh, 0f));
        }
    }

    // ---- detonation -------------------------------------------------------

    void Detonate(Spot s, Weapon w)
    {
        if (w == Weapon.Bomb) BlastDisc(s.pos, bombRadius * pixel);
        else BlastBand(s.pos, rocketHeightPixels * pixel * 0.5f);

        ammo[(int)w] = Mathf.Max(0, ammo[(int)w] - 1);
        ConsumeSpot(s);
        RefreshAmmo();
    }

    void BlastDisc(Vector3 center, float radius)
    {
        var remove = new List<int>();
        for (int i = 0; i < hanging.Count; i++)
        {
            Vector3 d = hanging[i].pos - center;
            if (d.x * d.x + d.y * d.y <= radius * radius) remove.Add(i);
        }
        Explode(remove, center, false);
    }

    void BlastBand(Vector3 center, float halfHeight)
    {
        var remove = new List<int>();
        for (int i = 0; i < hanging.Count; i++)
            if (Mathf.Abs(hanging[i].pos.y - center.y) <= halfHeight) remove.Add(i);
        Explode(remove, center, true);
    }

    // `remove` is ascending, so pop it back-to-front.
    void Explode(List<int> remove, Vector3 center, bool horizontal)
    {
        if (remove.Count == 0) return;

        foreach (int idx in remove)
        {
            var p = hanging[idx];
            Vector2 dir = horizontal
                ? new Vector2(p.pos.x >= center.x ? 1f : -1f, Random.Range(-0.3f, 0.3f))
                : new Vector2(p.pos.x - center.x, p.pos.y - center.y);
            ShatterPixel(p, dir, blastImpulse * Random.Range(0.7f, 1.15f));
        }
        for (int k = remove.Count - 1; k >= 0; k--) hanging.RemoveAt(remove[k]);

        // NOTE: no DetachSeparatedPieces here on purpose. Pieces cut loose by a
        // blast stay hanging in place — dropping them is slicing logic, and here
        // it would dump half the picture into the tube in one go.
        UploadHanging();
    }

    // ---- status -----------------------------------------------------------

    protected override void Update()
    {
        base.Update();
        UpdateStatus();
    }

    // base Update rewrites the status line every frame, so ours goes after it
    void UpdateStatus()
    {
        if (statusText == null) return;
        if (clogged) { statusText.color = new Color(1f, 0.4f, 0.4f); return; }

        if (hanging.Count == 0 && fallers.Count == 0)
        {
            statusText.text = "CLEAR!";
            statusText.color = new Color(0.30f, 0.72f, 0.35f);
        }
        else if (hanging.Count > 0 && ammo[0] + ammo[1] == 0)
        {
            statusText.text = "OUT OF AMMO";
            statusText.color = new Color(0.92f, 0.62f, 0.20f);
        }
    }
}
