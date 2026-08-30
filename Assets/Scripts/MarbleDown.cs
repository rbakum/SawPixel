using System.Collections.Generic;
using UnityEngine;

// "Marble Down" — a 5-wide, 25-deep shaft you dig from the top down to a door.
//
// Every click costs one energy and you start with eight, so digging alone runs
// you dry. Energy comes back only from filled jars: each block you break throws
// six pieces of its color onto the conveyor loop, jars pull matching pieces off
// it, and a filled jar pays out. The question is never just "where do I dig" but
// "what color do I dig next".
//
// Cell types beyond a plain block:
//   * DOUBLE  — a block inside a block. The first click strips the outer shell and
//               the inner one spreads to full size. Two clicks, two blocks' worth
//               of pieces.
//   * ICE     — not clickable until two of its neighbours are broken. The number
//               on it counts down as they fall.
//   * ENERGY  — free to click, and pays energy instead of costing it.
//   * JAR     — opens a fourth, temporary jar slot that vanishes once filled. The
//               way out when the conveyor is clogged with a color nobody wants.
//   * MISSING — no block at all, a hole in the shaft. Nothing to break and no way
//               through: the dig has to go around it.
//
// Anything further than `revealRadius` from a broken cell hides behind a question
// mark: you see the shape of the shaft, not what it is made of.
[DisallowMultipleComponent]
public class MarbleDown : MonoBehaviour
{
    [Header("Sprites")]
    public Sprite[] blockSprites;              // one per color, index == color id
    public Sprite iceSprite;
    public Sprite fogSprite;
    public Sprite energySprite;
    public Sprite jarSprite;

    [Header("Board")]
    public int boardWidth = 5;
    public int boardHeight = 25;
    public int visibleRows = 9;
    public int revealRadius = 5;

    [Header("Economy")]
    public int startEnergy = 8;
    public int jarCapacity = 3;
    public int jarReward = 1;                  // energy paid for one filled jar
    public int piecesPerBlock = 6;
    public int jarSlots = 3;

    [Header("Board mix")]
    [Range(0f, 0.4f)] public float missingChance = 0.09f;
    [Range(1f, 4f)] public float missingEdgeBias = 2.2f;   // holes cluster along the walls
    [Range(0f, 0.5f)] public float doubleChance = 0.16f;
    [Range(0f, 0.4f)] public float iceChance = 0.10f;
    [Range(0f, 0.2f)] public float energyChance = 0.07f;
    [Range(0f, 0.2f)] public float jarCellChance = 0.05f;
    public int plainTopRows = 2;               // no specials this close to the entrance

    [Header("Conveyor")]
    public float beltSpeed = 0.07f;            // loops per second
    public float flightTime = 0.45f;
    // A piece always lands on the belt and rides it for this long before a jar
    // may take it. Flying straight from the block into the jar reads as noise.
    public float beltDwell = 0.5f;

    [Header("Seed")]
    public bool useFixedSeed;
    public int seed;

    const float ORTHO_SIZE = 5f;
    const int Z_BODY = 0, Z_INNER = 1, Z_ICON = 2, Z_ICE = 3, Z_FOG = 4, Z_CELL_TEXT = 5;
    const int Z_HUD_BG = 20, Z_HUD = 21, Z_PIECE = 24, Z_HUD_TEXT = 25;

    static readonly Color BG_COLOR = new Color(0.42f, 0.04f, 0.20f);
    static readonly Color HUD_SHELF = new Color(0.62f, 0.06f, 0.30f);
    static readonly Color BELT_COLOR = new Color(0.33f, 0.03f, 0.16f);
    static readonly Color ENERGY_PANEL = new Color(0.78f, 0.93f, 0.55f);
    static readonly Color BONUS_JAR = new Color(1f, 0.84f, 0.25f);
    static readonly Color DOOR_LOCKED = new Color(0.35f, 0.10f, 0.22f);
    static readonly Color DOOR_OPEN = new Color(0.55f, 1f, 0.35f);

    static readonly Color[] BLOCK_COLORS =
    {
        new Color(0.443f, 1f, 0f),        // green
        new Color(1f, 0.216f, 0.851f),    // pink
        new Color(0.718f, 0.098f, 1f),    // purple
        new Color(1f, 0.780f, 0.102f),    // yellow
    };

    enum Kind { Block, Energy, JarCell, Missing }

    class Cell
    {
        public int col, row;
        public Kind kind;
        public int color;
        public int inner = -1;             // >= 0 => double, this hides under `color`
        public bool ice;
        public int freedNeighbours;        // broken neighbours so far; ice melts at 2
        public bool broken;
        public int bonus;

        public Transform root;
        public SpriteRenderer body, innerBody, icon, iceLayer, fog;
        public TextMesh label;
    }

    class Jar
    {
        public int color;
        public int filled;
        public int incoming;               // pieces already flying to this jar
        public bool bonus;
        public Transform root;
        public SpriteRenderer glass, lid, frame;
        public TextMesh text;
    }

    class Piece
    {
        public int color;
        public SpriteRenderer sr;
        public Vector3 pos, from;
        public Vector3 baseScale;
        public float fly;                  // 0..1 while travelling
        public bool flying;                // still on its way to the belt
        public float dwell;                // seconds spent riding the belt
        public Jar jar;                    // set once it has been claimed
    }

    Camera cam;
    Font uiFont;
    Sprite blankSprite;

    Cell[,] board;
    readonly List<Jar> jars = new List<Jar>();
    readonly List<Piece> belt = new List<Piece>();     // order == slot on the loop
    readonly List<Piece> toJar = new List<Piece>();
    Transform boardRoot, hudRoot, pieceRoot;

    SpriteRenderer door;
    TextMesh doorText, energyText, statusText;

    float halfW, halfH, cellSize;
    float windowTop, windowBottom, boardTopY;
    float scrollY, scrollTarget;

    Vector3 beltCenter;
    float beltHalfLen, beltRadius, beltPhase, pieceSize;

    int energy;
    int deepestBroken = -1;
    bool finished;
    int activeSeed;

    public int CurrentSeed => activeSeed;
    public int Energy => energy;

    // ---- lifecycle -------------------------------------------------------

    void Start()
    {
        activeSeed = useFixedSeed ? seed : new System.Random().Next();
        Random.InitState(activeSeed);
        Build();
    }

    public void Restart(int? withSeed = null)
    {
        if (withSeed.HasValue) { useFixedSeed = true; seed = withSeed.Value; }
        for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
        jars.Clear(); belt.Clear(); toJar.Clear();
        boardRoot = hudRoot = pieceRoot = null;
        finished = false;
        deepestBroken = -1;
        scrollY = scrollTarget = 0f;

        activeSeed = useFixedSeed ? seed : new System.Random().Next();
        Random.InitState(activeSeed);
        Build();
    }

    void Build()
    {
        if (blockSprites == null || blockSprites.Length == 0)
        {
            Debug.LogError("[MarbleDown] assign Block Sprites (one per color) on the component", this);
            enabled = false;
            return;
        }

        SetupCamera();
        MakeBlank();
        LoadFont();
        ComputeLayout();
        GenerateBoard();
        BuildBoardVisuals();
        BuildHud();

        energy = startEnergy;
        pieceRoot = new GameObject("Pieces").transform;
        pieceRoot.SetParent(transform, false);
        for (int i = 0; i < jarSlots; i++) AddJar(false);

        RefreshBoard();
        RefreshHud();
    }

    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            cam = go.AddComponent<Camera>();
        }
        cam.orthographic = true;
        cam.orthographicSize = ORTHO_SIZE;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = BG_COLOR;
    }

    void LoadFont()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (uiFont == null) uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    void MakeBlank()
    {
        blankSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
    }

    void ComputeLayout()
    {
        halfH = ORTHO_SIZE;
        halfW = ORTHO_SIZE * cam.aspect;

        windowTop = 0.30f * halfH;
        windowBottom = -0.99f * halfH;
        boardTopY = windowTop - 0.03f * halfH;          // breathing room under the HUD

        float byWidth = 1.86f * halfW / Mathf.Max(1, boardWidth);
        float byHeight = (boardTopY - windowBottom) / Mathf.Max(1, visibleRows);
        cellSize = Mathf.Min(byWidth, byHeight);

        beltCenter = new Vector3(0f, 0.55f * halfH, 0f);
        beltRadius = 0.055f * halfH;
        beltHalfLen = Mathf.Max(0.1f, halfW * 0.80f - beltRadius);
        pieceSize = beltRadius * 1.15f;
    }

    // ---- board generation -------------------------------------------------

    // Roll a board, then make sure it can actually be finished: holes must never
    // wall the shaft off completely. A blocked layout is simply re-rolled.
    void GenerateBoard()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            board = new Cell[boardWidth, boardHeight];
            for (int r = 0; r < boardHeight; r++)
                for (int c = 0; c < boardWidth; c++)
                    board[c, r] = MakeCell(c, r);

            if (PathExists()) { PlaceIce(); return; }
        }
        CarveEscape();                                   // pathological seed: cut one clean column
        PlaceIce();
    }

    // Ice melts only once two neighbours have been broken, so it may only sit
    // where two neighbours can actually be broken: in bounds, not a hole, and not
    // iced over themselves. Anywhere else it would be a cell nobody can ever open.
    void PlaceIce()
    {
        for (int r = plainTopRows; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
            {
                var cell = board[c, r];
                if (cell.kind != Kind.Block || cell.ice) continue;
                if (Random.value >= iceChance) continue;
                if (FreeNeighbours(c, r) >= 2) cell.ice = true;
            }

        // Placing ice steals a free neighbour from whoever was iced earlier, so
        // sweep until every remaining ice cell still has two ways to be opened.
        // Un-icing only ever helps the others, so this settles quickly.
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int r = 0; r < boardHeight; r++)
                for (int c = 0; c < boardWidth; c++)
                {
                    var cell = board[c, r];
                    if (!cell.ice || FreeNeighbours(c, r) >= 2) continue;
                    cell.ice = false;
                    changed = true;
                }
        }
    }

    int FreeNeighbours(int c, int r)
    {
        int n = 0;
        if (Breakable(c - 1, r)) n++;
        if (Breakable(c + 1, r)) n++;
        if (Breakable(c, r - 1)) n++;
        if (Breakable(c, r + 1)) n++;
        return n;
    }

    bool Breakable(int c, int r)
    {
        if (c < 0 || c >= boardWidth || r < 0 || r >= boardHeight) return false;
        var n = board[c, r];
        return n.kind != Kind.Missing && !n.ice;
    }

    Cell MakeCell(int c, int r)
    {
        var cell = new Cell { col = c, row = r, kind = Kind.Block, color = RandomColor() };
        if (r < plainTopRows) return cell;                // keep the entrance simple

        // holes favour the walls, but can open up anywhere
        bool edge = c == 0 || c == boardWidth - 1;
        if (Random.value < missingChance * (edge ? missingEdgeBias : 1f))
        {
            cell.kind = Kind.Missing;
            return cell;
        }

        float roll = Random.value;
        if (roll < energyChance)
        {
            cell.kind = Kind.Energy;
            cell.bonus = RollBonus();
            return cell;
        }
        roll -= energyChance;
        if (roll < jarCellChance) { cell.kind = Kind.JarCell; return cell; }

        if (Random.value < doubleChance) cell.inner = RandomColor();
        return cell;
    }

    bool Solid(int c, int r)
    {
        if (c < 0 || c >= boardWidth || r < 0 || r >= boardHeight) return false;
        return board[c, r].kind != Kind.Missing;
    }

    // Can the dig reach the bottom row at all, walking only over real blocks?
    bool PathExists()
    {
        var seen = new bool[boardWidth, boardHeight];
        var queue = new Queue<Vector2Int>();
        for (int c = 0; c < boardWidth; c++)
            if (Solid(c, 0)) { seen[c, 0] = true; queue.Enqueue(new Vector2Int(c, 0)); }

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (p.y == boardHeight - 1) return true;
            TryVisit(p.x - 1, p.y, seen, queue);
            TryVisit(p.x + 1, p.y, seen, queue);
            TryVisit(p.x, p.y - 1, seen, queue);
            TryVisit(p.x, p.y + 1, seen, queue);
        }
        return false;
    }

    void TryVisit(int c, int r, bool[,] seen, Queue<Vector2Int> queue)
    {
        if (!Solid(c, r) || seen[c, r]) return;
        seen[c, r] = true;
        queue.Enqueue(new Vector2Int(c, r));
    }

    void CarveEscape()
    {
        int col = boardWidth / 2;
        for (int r = 0; r < boardHeight; r++)
            if (board[col, r].kind == Kind.Missing)
                board[col, r] = new Cell { col = col, row = r, kind = Kind.Block, color = RandomColor() };
    }

    int RandomColor() => Random.Range(0, Mathf.Min(BLOCK_COLORS.Length, blockSprites.Length));

    int RollBonus()
    {
        int roll = Random.Range(0, 15);
        if (roll < 8) return 1;
        if (roll < 12) return 2;
        return roll < 14 ? 3 : 4;
    }

    // ---- board visuals ----------------------------------------------------

    void BuildBoardVisuals()
    {
        boardRoot = new GameObject("Board").transform;
        boardRoot.SetParent(transform, false);

        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
                BuildCellVisual(board[c, r]);

        BuildDoor();
    }

    void BuildCellVisual(Cell cell)
    {
        cell.root = new GameObject("Cell" + cell.col + "_" + cell.row).transform;
        cell.root.SetParent(boardRoot, false);
        cell.root.localPosition = CellLocalPos(cell.col, cell.row);

        cell.body = MakeSprite(cell.root, "Body", null, Z_BODY);
        cell.innerBody = MakeSprite(cell.root, "Inner", null, Z_INNER);
        cell.icon = MakeSprite(cell.root, "Icon", null, Z_ICON);
        cell.iceLayer = MakeSprite(cell.root, "Ice", iceSprite, Z_ICE);
        cell.fog = MakeSprite(cell.root, "Fog", fogSprite, Z_FOG);
        cell.label = MakeText(cell.root, "Label", Vector3.zero, Z_CELL_TEXT, cellSize * 0.055f);
    }

    void BuildDoor()
    {
        var root = new GameObject("Door").transform;
        root.SetParent(boardRoot, false);
        root.localPosition = CellLocalPos((boardWidth - 1) * 0.5f, boardHeight);

        door = MakeSprite(root, "DoorBody", blankSprite, Z_BODY);
        FitSprite(door, blankSprite, cellSize * (boardWidth * 0.6f), cellSize * 0.9f);
        door.color = DOOR_LOCKED;

        doorText = MakeText(root, "DoorText", Vector3.zero, Z_CELL_TEXT, cellSize * 0.05f);
        doorText.text = "EXIT";
    }

    Vector3 CellLocalPos(float col, float row)
    {
        float x = (col - (boardWidth - 1) * 0.5f) * cellSize;
        float y = boardTopY - (row + 0.5f) * cellSize;
        return new Vector3(x, y, 0f);
    }

    SpriteRenderer MakeSprite(Transform parent, string name, Sprite sprite, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        if (sprite != null) FitSprite(sr, sprite, cellSize, cellSize);
        sr.enabled = sprite != null;
        return sr;
    }

    // sprites arrive at different pixel sizes, so each is scaled to the box it
    // should fill rather than trusting pixels-per-unit
    void FitSprite(SpriteRenderer sr, Sprite sprite, float width, float height)
    {
        if (sprite == null) return;
        float sx = width / (sprite.rect.width / sprite.pixelsPerUnit);
        float sy = height / (sprite.rect.height / sprite.pixelsPerUnit);
        sr.transform.localScale = new Vector3(sx, sy, 1f);
    }

    TextMesh MakeText(Transform parent, string name, Vector3 pos, int order, float size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var tm = go.AddComponent<TextMesh>();
        tm.font = uiFont;
        tm.GetComponent<MeshRenderer>().sharedMaterial = uiFont.material;
        tm.GetComponent<MeshRenderer>().sortingOrder = order;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.fontSize = 64;
        tm.characterSize = size;
        tm.color = Color.white;
        tm.text = "";
        return tm;
    }

    // ---- HUD --------------------------------------------------------------

    void BuildHud()
    {
        hudRoot = new GameObject("Hud").transform;
        hudRoot.SetParent(transform, false);

        // opaque band so rows scrolling up slide behind the jars instead of over them
        var band = MakeSprite(hudRoot, "HudBand", blankSprite, Z_HUD_BG);
        band.transform.localPosition = new Vector3(0f, (halfH + windowTop) * 0.5f, 0f);
        FitSprite(band, blankSprite, halfW * 2f, halfH - windowTop);
        band.color = BG_COLOR;

        var shelf = MakeSprite(hudRoot, "Shelf", blankSprite, Z_HUD_BG + 1);
        shelf.transform.localPosition = new Vector3(0f, 0.735f * halfH, 0f);
        FitSprite(shelf, blankSprite, halfW * 1.45f, 0.07f * halfH);
        shelf.color = HUD_SHELF;

        BuildBeltVisual();
        BuildEnergyVisual();

        statusText = MakeText(hudRoot, "Status", new Vector3(0f, 0.16f * halfH, 0f), Z_HUD_TEXT, halfH * 0.026f);
    }

    // The belt is a stadium loop: two straight runs joined by half-circle caps.
    void BuildBeltVisual()
    {
        var bar = MakeSprite(hudRoot, "Belt", blankSprite, Z_HUD_BG + 1);
        bar.transform.localPosition = beltCenter;
        FitSprite(bar, blankSprite, beltHalfLen * 2f, beltRadius * 2.55f);
        bar.color = BELT_COLOR;

        for (int side = -1; side <= 1; side += 2)
        {
            var cap = MakeSprite(hudRoot, "BeltCap" + side, blankSprite, Z_HUD_BG + 1);
            cap.transform.localPosition = beltCenter + new Vector3(side * beltHalfLen, 0f, 0f);
            FitSprite(cap, blankSprite, beltRadius * 1.6f, beltRadius * 2.55f);
            cap.color = BELT_COLOR;
        }
    }

    void BuildEnergyVisual()
    {
        float y = 0.38f * halfH;
        var panel = MakeSprite(hudRoot, "EnergyPanel", blankSprite, Z_HUD_BG + 1);
        panel.transform.localPosition = new Vector3(0f, y, 0f);
        FitSprite(panel, blankSprite, halfW * 0.44f, 0.085f * halfH);
        panel.color = ENERGY_PANEL;

        var bolt = MakeSprite(hudRoot, "EnergyIcon", energySprite, Z_HUD);
        bolt.transform.localPosition = new Vector3(-halfW * 0.11f, y, 0f);
        FitSprite(bolt, energySprite, 0.065f * halfH, 0.065f * halfH);

        energyText = MakeText(hudRoot, "EnergyText", new Vector3(halfW * 0.05f, y, 0f), Z_HUD_TEXT, halfH * 0.024f);
        energyText.color = new Color(0.10f, 0.35f, 0.10f);
    }

    // ---- jars -------------------------------------------------------------

    void AddJar(bool bonus)
    {
        var jar = new Jar { color = PickJarColor(), bonus = bonus };
        jar.root = new GameObject(bonus ? "BonusJar" : "Jar").transform;
        jar.root.SetParent(hudRoot, false);

        jar.frame = MakeSprite(jar.root, "Frame", blankSprite, Z_HUD_BG + 2);
        jar.frame.color = BONUS_JAR;
        jar.glass = MakeSprite(jar.root, "Glass", jarSprite, Z_HUD);
        jar.lid = MakeSprite(jar.root, "Lid", blankSprite, Z_HUD + 1);
        jar.text = MakeText(jar.root, "Count", Vector3.zero, Z_HUD_TEXT, halfH * 0.018f);

        jars.Add(jar);
        LayoutJars();
    }

    void LayoutJars()
    {
        float y = 0.82f * halfH;
        float w = Mathf.Min(0.20f * halfH, 1.5f * halfW / Mathf.Max(1, jars.Count));
        float step = w * 1.25f;
        float x0 = -step * (jars.Count - 1) * 0.5f;

        for (int i = 0; i < jars.Count; i++)
        {
            var jar = jars[i];
            jar.root.localPosition = new Vector3(x0 + i * step, y, 0f);

            FitSprite(jar.glass, jarSprite, w * 0.82f, w * 0.90f);
            FitSprite(jar.frame, blankSprite, w * 1.02f, w * 1.12f);
            FitSprite(jar.lid, blankSprite, w * 0.74f, w * 0.24f);
            jar.lid.transform.localPosition = new Vector3(0f, w * 0.38f, 0f);
            jar.text.transform.localPosition = new Vector3(0f, w * 0.38f, 0f);
            jar.text.characterSize = w * 0.048f;
        }
        RefreshJars();
    }

    // Jars ask for what the shaft can actually pay: whatever is stuck on the belt
    // first, then whatever colors are still buried.
    int PickJarColor()
    {
        int n = Mathf.Min(BLOCK_COLORS.Length, blockSprites.Length);
        var weight = new float[n];
        foreach (var p in belt) weight[p.color] += 3f;

        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
            {
                var cell = board[c, r];
                if (cell.broken || cell.kind != Kind.Block) continue;
                weight[cell.color] += 1f;
                if (cell.inner >= 0) weight[cell.inner] += 1f;
            }

        float total = 0f;
        foreach (float w in weight) total += w;
        if (total <= 0f) return Random.Range(0, n);

        float roll = Random.value * total;
        for (int i = 0; i < n; i++)
        {
            roll -= weight[i];
            if (roll <= 0f) return i;
        }
        return n - 1;
    }

    void RefreshJars()
    {
        foreach (var jar in jars)
        {
            jar.lid.color = BLOCK_COLORS[jar.color];
            jar.frame.enabled = jar.bonus;
            jar.text.text = (jarCapacity - jar.filled).ToString();
            jar.text.color = new Color(0.15f, 0.05f, 0.10f);
        }
    }

    void CompleteJar(Jar jar)
    {
        energy += Mathf.Max(0, jarReward);
        bool wasBonus = jar.bonus;
        jars.Remove(jar);
        Destroy(jar.root.gameObject);

        if (!wasBonus) AddJar(false);      // permanent slots refill; the bonus one is spent
        else LayoutJars();

        RefreshHud();
        AssignPieces();
    }

    // ---- pieces & belt ----------------------------------------------------

    void SpawnPieces(Cell cell, int color, int count)
    {
        Vector3 origin = cell.root.position;
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Piece");
            go.transform.SetParent(pieceRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = blockSprites[color];
            sr.sortingOrder = Z_PIECE;
            FitSprite(sr, sr.sprite, pieceSize, pieceSize);

            var p = new Piece
            {
                color = color,
                sr = sr,
                baseScale = sr.transform.localScale,
                from = origin + (Vector3)(Random.insideUnitCircle * cellSize * 0.35f),
                fly = 0f,
                flying = true,
            };
            p.pos = p.from;
            go.transform.position = p.pos;
            belt.Add(p);
        }
    }

    // Hand belt pieces to any jar that still has room for their color — but only
    // pieces that have actually landed and done their lap time.
    void AssignPieces()
    {
        foreach (var jar in jars)
        {
            int need = jarCapacity - jar.filled - jar.incoming;
            for (int k = 0; k < need; k++)
            {
                int idx = -1;
                for (int i = 0; i < belt.Count; i++)
                    if (belt[i].color == jar.color && !belt[i].flying && belt[i].dwell >= beltDwell) { idx = i; break; }
                if (idx < 0) break;

                var p = belt[idx];
                belt.RemoveAt(idx);
                p.jar = jar;
                p.from = p.pos;
                p.fly = 0f;
                p.flying = false;
                jar.incoming++;
                toJar.Add(p);
            }
        }
    }

    // Position on the loop for t in [0,1): bottom run, right cap, top run, left cap.
    Vector3 BeltPoint(float t)
    {
        t = Mathf.Repeat(t, 1f);
        float straight = 2f * beltHalfLen;
        float arc = Mathf.PI * beltRadius;
        float s = t * (2f * straight + 2f * arc);

        if (s < straight)
            return beltCenter + new Vector3(-beltHalfLen + s, -beltRadius, 0f);
        s -= straight;

        if (s < arc)
        {
            float a = -Mathf.PI * 0.5f + (s / arc) * Mathf.PI;
            return beltCenter + new Vector3(beltHalfLen + Mathf.Cos(a) * beltRadius, Mathf.Sin(a) * beltRadius, 0f);
        }
        s -= arc;

        if (s < straight)
            return beltCenter + new Vector3(beltHalfLen - s, beltRadius, 0f);
        s -= straight;

        float b = Mathf.PI * 0.5f + (s / arc) * Mathf.PI;
        return beltCenter + new Vector3(-beltHalfLen + Mathf.Cos(b) * beltRadius, Mathf.Sin(b) * beltRadius, 0f);
    }

    void UpdatePieces(float dt)
    {
        beltPhase = Mathf.Repeat(beltPhase + beltSpeed * dt, 1f);

        // evenly spaced around the loop, so the spacing re-settles as pieces come and go
        for (int i = 0; i < belt.Count; i++)
        {
            var p = belt[i];
            Vector3 slot = BeltPoint(beltPhase + (float)i / belt.Count);

            if (p.flying)
            {
                p.fly = Mathf.Min(1f, p.fly + dt / Mathf.Max(0.05f, flightTime));
                float e = 1f - (1f - p.fly) * (1f - p.fly);
                p.pos = Vector3.Lerp(p.from, slot, e);
                if (p.fly >= 1f) { p.flying = false; p.dwell = 0f; }
            }
            else
            {
                p.dwell += dt;
                p.pos = Vector3.Lerp(p.pos, slot, 1f - Mathf.Exp(-14f * dt));
            }

            p.sr.transform.position = p.pos;
        }

        for (int i = toJar.Count - 1; i >= 0; i--)
        {
            var p = toJar[i];
            p.fly = Mathf.Min(1f, p.fly + dt / Mathf.Max(0.05f, flightTime));
            float e = 1f - (1f - p.fly) * (1f - p.fly);

            Vector3 dst = p.jar != null && p.jar.root != null ? p.jar.root.position : beltCenter;
            p.pos = Vector3.Lerp(p.from, dst, e);
            p.sr.transform.position = p.pos;
            p.sr.transform.localScale = p.baseScale * Mathf.Lerp(1f, 0.35f, e);

            if (p.fly < 1f) continue;

            toJar.RemoveAt(i);
            var jar = p.jar;
            Destroy(p.sr.gameObject);
            if (jar == null) continue;

            jar.incoming--;
            jar.filled++;
            RefreshJars();
            if (jar.filled >= jarCapacity) CompleteJar(jar);
        }

        AssignPieces();
    }

    // ---- input ------------------------------------------------------------

    void Update()
    {
        HandleInput();
        UpdatePieces(Time.deltaTime);
        ScrollBoard();
    }

    void HandleInput()
    {
        if (finished) return;

        bool pressed = Input.GetMouseButtonDown(0);
        Vector3 screen = Input.mousePosition;
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            pressed = t.phase == TouchPhase.Began;
            screen = t.position;
        }
        if (!pressed) return;

        screen.z = -cam.transform.position.z;
        Vector3 world = cam.ScreenToWorldPoint(screen);
        if (world.y > windowTop) return;                 // that's the HUD, not the board

        float localY = world.y - scrollY;
        int col = Mathf.RoundToInt(world.x / cellSize + (boardWidth - 1) * 0.5f);
        int row = Mathf.FloorToInt((boardTopY - localY) / cellSize);

        if (row == boardHeight) { TryExit(); return; }
        if (col < 0 || col >= boardWidth || row < 0 || row >= boardHeight) return;
        TryBreak(board[col, row]);
    }

    void TryExit()
    {
        if (!DoorOpen()) return;
        finished = true;
        statusText.text = "YOU'RE OUT!";
        statusText.color = DOOR_OPEN;
    }

    bool DoorOpen()
    {
        for (int c = 0; c < boardWidth; c++)
            if (board[c, boardHeight - 1].broken) return true;
        return false;
    }

    // ---- breaking ---------------------------------------------------------

    bool Clickable(Cell cell)
    {
        if (cell.broken || cell.kind == Kind.Missing) return false;
        if (cell.ice && cell.freedNeighbours < 2) return false;
        if (cell.row == 0) return true;                  // the entrance is always open
        return IsBroken(cell.col - 1, cell.row) || IsBroken(cell.col + 1, cell.row)
            || IsBroken(cell.col, cell.row - 1) || IsBroken(cell.col, cell.row + 1);
    }

    bool IsBroken(int c, int r)
    {
        if (c < 0 || c >= boardWidth || r < 0 || r >= boardHeight) return false;
        return board[c, r].broken;
    }

    void TryBreak(Cell cell)
    {
        if (!Clickable(cell)) return;

        bool free = cell.kind == Kind.Energy;             // energy cells pay, they don't charge
        if (!free && energy <= 0) return;
        if (!free) energy--;

        switch (cell.kind)
        {
            case Kind.Energy:
                energy += cell.bonus;
                cell.broken = true;
                break;

            case Kind.JarCell:
                cell.broken = true;
                AddJar(true);
                break;

            default:
                if (cell.inner >= 0)
                {
                    // strip the shell; the inner block spreads to full size and stays
                    SpawnPieces(cell, cell.color, piecesPerBlock);
                    cell.color = cell.inner;
                    cell.inner = -1;
                }
                else
                {
                    SpawnPieces(cell, cell.color, piecesPerBlock);
                    cell.broken = true;
                }
                break;
        }

        if (cell.broken)
        {
            deepestBroken = Mathf.Max(deepestBroken, cell.row);
            Bump(cell.col - 1, cell.row);
            Bump(cell.col + 1, cell.row);
            Bump(cell.col, cell.row - 1);
            Bump(cell.col, cell.row + 1);
        }

        RefreshBoard();
        RefreshHud();
        CheckLoss();
    }

    void Bump(int c, int r)
    {
        if (c < 0 || c >= boardWidth || r < 0 || r >= boardHeight) return;
        var n = board[c, r];
        if (!n.ice || n.broken) return;
        n.freedNeighbours++;
        if (n.freedNeighbours >= 2) n.ice = false;
    }

    // You are only dead when you cannot pay for a move AND no free energy cell is
    // in reach — clicking one of those costs nothing.
    void CheckLoss()
    {
        if (finished || energy > 0) return;

        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
                if (board[c, r].kind == Kind.Energy && Clickable(board[c, r])) return;

        finished = true;
        statusText.text = "OUT OF ENERGY";
        statusText.color = new Color(1f, 0.45f, 0.45f);
    }

    // ---- refresh ----------------------------------------------------------

    void RefreshBoard()
    {
        var dist = FogDistances();

        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
                RefreshCell(board[c, r], dist[c, r]);

        bool open = DoorOpen();
        door.color = open ? DOOR_OPEN : DOOR_LOCKED;
        doorText.text = open ? "EXIT" : "";
        doorText.color = new Color(0.1f, 0.25f, 0.1f);

        scrollTarget = ScrollFor(deepestBroken + 3);
    }

    static readonly Color DIM = new Color(0.42f, 0.36f, 0.44f, 1f);

    void RefreshCell(Cell cell, int distance)
    {
        Color tint = Clickable(cell) ? Color.white : DIM;

        // a hole is drawn as nothing at all — the background is the abyss
        if (cell.broken || cell.kind == Kind.Missing)
        {
            cell.body.enabled = cell.innerBody.enabled = cell.icon.enabled = false;
            cell.iceLayer.enabled = cell.fog.enabled = false;
            cell.label.text = "";
            return;
        }

        bool hidden = distance > revealRadius;
        cell.fog.enabled = hidden;
        cell.fog.color = tint;
        if (hidden)
        {
            cell.body.enabled = cell.innerBody.enabled = cell.icon.enabled = false;
            cell.iceLayer.enabled = false;
            cell.label.text = "";
            return;
        }

        bool isBlock = cell.kind == Kind.Block;
        cell.body.enabled = isBlock;
        if (isBlock)
        {
            cell.body.sprite = blockSprites[cell.color];
            cell.body.color = tint;
            FitSprite(cell.body, cell.body.sprite, cellSize, cellSize);
        }

        cell.innerBody.enabled = isBlock && cell.inner >= 0;
        if (cell.innerBody.enabled)
        {
            cell.innerBody.sprite = blockSprites[cell.inner];
            cell.innerBody.color = tint;
            FitSprite(cell.innerBody, cell.innerBody.sprite, cellSize * 0.62f, cellSize * 0.62f);
        }

        cell.icon.enabled = !isBlock;
        if (cell.icon.enabled)
        {
            cell.icon.sprite = cell.kind == Kind.Energy ? energySprite : jarSprite;
            cell.icon.color = tint;
            FitSprite(cell.icon, cell.icon.sprite, cellSize * 0.8f, cellSize * 0.8f);
        }

        cell.iceLayer.enabled = cell.ice;
        if (cell.ice)
        {
            FitSprite(cell.iceLayer, iceSprite, cellSize, cellSize);
            // one neighbour down: the ice thins out, one more break and it's gone
            var ice = cell.freedNeighbours >= 1 ? new Color(1f, 1f, 1f, 0.55f) : Color.white;
            cell.iceLayer.color = ice * tint;
        }

        cell.label.text = cell.ice ? (2 - cell.freedNeighbours).ToString()
                        : cell.kind == Kind.Energy ? "+" + cell.bonus
                        : "";
        cell.label.color = cell.ice ? new Color(0.10f, 0.30f, 0.45f) : new Color(0.35f, 0.20f, 0f);
    }

    // Chebyshev distance to the nearest broken cell, with the entrance row counted
    // as open so the top of the shaft is visible before the first click.
    int[,] FogDistances()
    {
        var dist = new int[boardWidth, boardHeight];
        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
                dist[c, r] = int.MaxValue;

        var queue = new Queue<Vector2Int>();
        for (int c = 0; c < boardWidth; c++)
        {
            dist[c, 0] = 1;
            queue.Enqueue(new Vector2Int(c, 0));
        }
        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
                if (board[c, r].broken) { dist[c, r] = 0; queue.Enqueue(new Vector2Int(c, r)); }

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nc = p.x + dx, nr = p.y + dy;
                    if (nc < 0 || nc >= boardWidth || nr < 0 || nr >= boardHeight) continue;
                    if (dist[nc, nr] <= dist[p.x, p.y] + 1) continue;
                    dist[nc, nr] = dist[p.x, p.y] + 1;
                    queue.Enqueue(new Vector2Int(nc, nr));
                }
        }
        return dist;
    }

    void RefreshHud()
    {
        energyText.text = energy.ToString();
        RefreshJars();
    }

    // ---- scrolling --------------------------------------------------------

    float ScrollFor(int focusRow)
    {
        float rowY = boardTopY - (focusRow + 0.5f) * cellSize;
        float want = windowBottom + (boardTopY - windowBottom) * 0.5f;
        float offset = want - rowY;

        float lowest = boardTopY - (boardHeight + 1.5f) * cellSize;
        float maxOffset = windowBottom - lowest;
        return Mathf.Clamp(offset, 0f, Mathf.Max(0f, maxOffset));
    }

    void ScrollBoard()
    {
        if (boardRoot == null) return;
        scrollY = Mathf.Lerp(scrollY, scrollTarget, 1f - Mathf.Exp(-8f * Time.deltaTime));
        boardRoot.localPosition = new Vector3(0f, scrollY, 0f);
    }
}
