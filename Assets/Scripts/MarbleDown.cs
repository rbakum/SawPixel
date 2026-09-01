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
    public Sprite iceCrackedSprite;            // one neighbour left to go
    public Sprite fogSprite;
    public Sprite energySprite;
    public Sprite jarSprite;

    [Header("Board")]
    public int boardWidth = 5;
    public int boardHeight = 25;
    public int visibleRows = 9;
    public int scrollLead = 1;                 // rows kept below the dig before the board moves
    public int revealRadius = 6;
    // How much a block shrinks inside its cell, so neighbours don't touch. This is
    // an ABSOLUTE margin, not a scale: a 2x1 loses the same edge as a 1x1, or the
    // gap around long blocks would come out twice as wide.
    [Range(0f, 0.3f)] public float blockGap = 0.08f;

    [Header("Economy")]
    public int startEnergy = 8;
    // Each jar asks for its own amount and pays exactly that back, so a block
    // (one piece, one click) is worth one energy — the economy breaks even and
    // everything you waste on a color nobody wants is a real loss.
    public int jarCapacityMin = 2;
    public int jarCapacityMax = 3;
    public int piecesPerBlock = 1;
    public int jarSlots = 3;

    [Header("Board mix")]
    [Range(0f, 0.4f)] public float missingChance = 0.09f;
    [Range(1f, 4f)] public float missingEdgeBias = 2.2f;   // holes cluster along the walls
    [Range(0f, 0.5f)] public float doubleChance = 0.16f;
    [Range(0f, 0.3f)] public float tripleChance = 0.10f;   // chance a double goes one layer deeper
    [Range(0f, 0.5f)] public float crustChance = 0.08f;    // block sealed under a stone shell
    [Range(0f, 0.3f)] public float rockChance = 0.03f;     // solid rock: one energy, nothing back
    [Range(0f, 0.3f)] public float pairChance = 0.04f;     // 2x1 block: one click clears both halves
    [Range(0f, 0.4f)] public float iceChance = 0.10f;
    [Range(0f, 0.2f)] public float energyChance = 0.035f;
    public int jarCellsPerLevel = 2;           // exactly this many, placed after the board is rolled
    public int plainTopRows = 2;               // no specials this close to the entrance

    [Header("Conveyor")]
    public float beltSpeed = 0.07f;            // loops per second
    public float flightTime = 0.45f;
    // A piece always lands on the belt and rides it for this long before a jar
    // may take it. Flying straight from the block into the jar reads as noise.
    public float beltDwell = 0.5f;

    [Header("Feel")]
    public float jarSwapTime = 0.28f;          // a jar shrinks out / pops in over this
    public float moteTime = 0.55f;             // energy flying from a jar to the counter
    public float floaterTime = 0.75f;          // the -1 / +N that rises off a click

    [Header("Seed")]
    public bool useFixedSeed;
    public int seed;

    const float ORTHO_SIZE = 5f;
    const int Z_FLOOR = -2, Z_BODY = 0, Z_INNER = 1, Z_INNER2 = 2, Z_ICON = 3, Z_ICE = 4, Z_FOG = 5, Z_CELL_TEXT = 6;
    // The jar frame used to sit ABOVE the glass, so a bonus jar was painted over
    // as a solid gold block and its real color was invisible.
    const int Z_HUD_BG = 20, Z_HUD_PANEL = 21, Z_JAR_FRAME = 22, Z_HUD = 23, Z_HUD_TOP = 24;
    const int Z_PIECE = 26, Z_HUD_TEXT = 27;

    static readonly Color BG_COLOR = new Color(0.42f, 0.04f, 0.20f);
    static readonly Color HUD_SHELF = new Color(0.62f, 0.06f, 0.30f);
    static readonly Color BELT_COLOR = new Color(0.33f, 0.03f, 0.16f);
    static readonly Color ENERGY_PANEL = new Color(0.78f, 0.93f, 0.55f);
    static readonly Color ENERGY_WARN = new Color(1f, 0.86f, 0.34f);          // three left
    static readonly Color ENERGY_LOW = new Color(0.94f, 0.28f, 0.28f);        // two or less
    static readonly Color ENERGY_LOW_FLASH = new Color(1f, 0.70f, 0.70f);
    static readonly Color ENERGY_DIGITS = new Color(0.16f, 0.13f, 0.10f);     // readable on all three
    static readonly Color BONUS_JAR = new Color(1f, 0.84f, 0.25f);
    static readonly Color COST_TEXT = new Color(1f, 0.55f, 0.55f);
    static readonly Color GAIN_TEXT = new Color(0.65f, 1f, 0.45f);
    static readonly Color SOLID_ROCK = new Color(0.55f, 0.54f, 0.58f);
    static readonly Color OPEN_TILE = new Color(0.52f, 0.09f, 0.27f);   // dug out, but still floor
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

    // Sits in `color` when the outermost layer is stone instead of a real block:
    // it costs a click to chip off and pays nothing back. Two clicks, one piece,
    // so every petrified stone is one energy gone for good.
    const int STONE = -1;

    class Cell
    {
        public int col, row;
        public Kind kind;
        public int color;
        // colors hiding under `color`, outermost first. One entry = a double cell,
        // two = a rare triple. Each click peels exactly one layer.
        public readonly List<int> nest = new List<int>();
        public bool ice;
        public Cell twin;                  // other half of a 2x1 block, null for a normal cell
        public bool twinHead;              // the half that draws the stretched sprite
        public int freedNeighbours;        // broken neighbours so far; ice melts at 2
        public bool broken;

        public Transform root;
        public SpriteRenderer floor, body, innerBody, innerBody2, icon, iceLayer, fog;
    }

    class Jar
    {
        public int color;
        public int capacity;
        public int filled;
        public int incoming;               // pieces already flying to this jar
        public bool bonus;
        public float appear;                   // 0..1 pop-in, so jars never swap in one frame
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

    class Fading { public Transform root; public float t; }
    class Floater { public TextMesh tm; public Vector3 from; public float t; }
    class Mote { public SpriteRenderer sr; public Vector3 from, pos; public float t; }
    readonly List<Fading> fadingJars = new List<Fading>();
    readonly List<Mote> motes = new List<Mote>();
    readonly List<Floater> floaters = new List<Floater>();
    Vector3 energyIconPos;
    Sprite roundedSprite;

    SpriteRenderer door;
    TextMesh doorText, energyText, statusText;
    SpriteRenderer energyPanel;

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
        fadingJars.Clear(); motes.Clear(); floaters.Clear();
        boardRoot = hudRoot = pieceRoot = null;
        energyPanel = null;
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
        roundedSprite = MakeRoundedSprite(64, 0.22f);
    }

    // A rounded square, drawn in code so the corner radius is ours to pick and no
    // extra asset has to exist. Used for cells the player has dug out: they are
    // floor you can see, not the void a missing cell leaves behind.
    Sprite MakeRoundedSprite(int size, float cornerFraction)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        float radius = size * cornerFraction;
        var px = new Color[size * size];

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float px0 = x + 0.5f, py0 = y + 0.5f;
                float dx = Mathf.Max(radius - px0, px0 - (size - radius), 0f);
                float dy = Mathf.Max(radius - py0, py0 - (size - radius), 0f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f));
            }

        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
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

            if (PathExists()) { PlacePairs(); PlaceJarCells(); PlaceIce(); return; }
        }
        CarveEscape();                                   // pathological seed: cut one clean column
        PlacePairs();
        PlaceJarCells();
        PlaceIce();
    }

    // A 2x1 block spans two cells and falls to a single click, so it clears twice
    // the ground for the same energy. Both halves still pay out their piece, which
    // is what makes finding one worth a detour.
    void PlacePairs()
    {
        for (int r = plainTopRows; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
            {
                var head = board[c, r];
                if (!PlainBlock(head) || Random.value >= pairChance) continue;

                // prefer the rolled direction, fall back to the other one
                bool sideways = Random.value < 0.5f;
                Cell mate = sideways ? Sideways(c, r) : Downwards(c, r);
                if (mate == null) mate = sideways ? Downwards(c, r) : Sideways(c, r);
                if (mate == null) continue;

                mate.color = head.color;
                head.twin = mate;
                mate.twin = head;
                head.twinHead = true;
            }
    }

    Cell Sideways(int c, int r) => c + 1 < boardWidth && PlainBlock(board[c + 1, r]) ? board[c + 1, r] : null;
    Cell Downwards(int c, int r) => r + 1 < boardHeight && PlainBlock(board[c, r + 1]) ? board[c, r + 1] : null;

    // Only a plain, unclaimed block may join a pair — no stone shell, no nesting,
    // no ice, and nothing already paired.
    bool PlainBlock(Cell cell)
        => cell.kind == Kind.Block && cell.twin == null && !cell.ice
        && cell.nest.Count == 0 && cell.color != STONE;

    // A fixed budget of jar cells per level rather than a per-cell chance, so the
    // fourth slot is a rare, findable thing instead of random weather.
    void PlaceJarCells()
    {
        var spots = new List<Vector2Int>();
        for (int r = plainTopRows; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
                if (board[c, r].kind == Kind.Block && board[c, r].twin == null) spots.Add(new Vector2Int(c, r));

        for (int i = spots.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (spots[i], spots[j]) = (spots[j], spots[i]);
        }

        int placed = Mathf.Min(jarCellsPerLevel, spots.Count);
        for (int i = 0; i < placed; i++)
        {
            var cell = board[spots[i].x, spots[i].y];
            cell.kind = Kind.JarCell;
            cell.nest.Clear();
            cell.color = RandomColor();
        }
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
                if (cell.kind != Kind.Block || cell.ice || cell.color == STONE || cell.twin != null) continue;
                if (Random.value >= iceChance) continue;
                if (OpenableNeighbours(c, r) >= 2) cell.ice = true;
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
                    if (!cell.ice || OpenableNeighbours(c, r) >= 2) continue;
                    cell.ice = false;
                    changed = true;
                }
        }
    }

    // Only the sides and the cell above count. Ice reachable solely from above and
    // below has to be attacked from a side that isn't there, which kept locking
    // boards up: you dig past it and can never come back at it.
    int OpenableNeighbours(int c, int r)
    {
        int n = 0;
        if (Breakable(c - 1, r)) n++;
        if (Breakable(c + 1, r)) n++;
        if (Breakable(c, r - 1)) n++;
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
            return cell;
        }
        if (Random.value < rockChance)
        {
            cell.color = STONE;             // nothing underneath: pure dead weight
        }
        else if (Random.value < crustChance)
        {
            cell.nest.Add(cell.color);      // the real block hides under the shell
            cell.color = STONE;
        }
        else if (Random.value < doubleChance)
        {
            cell.nest.Add(RandomColor());
            if (Random.value < tripleChance) cell.nest.Add(RandomColor());
        }
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

        // the tile under everything: a cell that exists always shows floor, so the
        // shape of the shaft reads even where the contents are still hidden
        cell.floor = MakeSprite(cell.root, "Floor", roundedSprite, Z_FLOOR);
        FitSprite(cell.floor, roundedSprite, BlockSize, BlockSize);
        cell.floor.color = OPEN_TILE;

        cell.body = MakeSprite(cell.root, "Body", null, Z_BODY);
        cell.innerBody = MakeSprite(cell.root, "Inner", null, Z_INNER);
        cell.innerBody2 = MakeSprite(cell.root, "Inner2", null, Z_INNER2);
        cell.icon = MakeSprite(cell.root, "Icon", null, Z_ICON);
        cell.iceLayer = MakeSprite(cell.root, "Ice", iceSprite, Z_ICE);
        cell.fog = MakeSprite(cell.root, "Fog", fogSprite, Z_FOG);
        FitSprite(cell.fog, fogSprite, BlockSize, BlockSize);
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

    float BlockSize => cellSize * (1f - blockGap);          // one cell, minus the gap
    float PairSize => cellSize * (2f - blockGap);           // two cells, minus the SAME gap

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
        // down to boardTopY, not windowTop: the gap between them was exactly where
        // the row you already dug through stayed visible as a sliver
        var band = MakeSprite(hudRoot, "HudBand", blankSprite, Z_HUD_BG);
        band.transform.localPosition = new Vector3(0f, (halfH + boardTopY) * 0.5f, 0f);
        FitSprite(band, blankSprite, halfW * 2f, halfH - boardTopY);
        band.color = BG_COLOR;

        var shelf = MakeSprite(hudRoot, "Shelf", blankSprite, Z_HUD_PANEL);
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
        var bar = MakeSprite(hudRoot, "Belt", blankSprite, Z_HUD_PANEL);
        bar.transform.localPosition = beltCenter;
        FitSprite(bar, blankSprite, beltHalfLen * 2f, beltRadius * 2.55f);
        bar.color = BELT_COLOR;

        for (int side = -1; side <= 1; side += 2)
        {
            var cap = MakeSprite(hudRoot, "BeltCap" + side, blankSprite, Z_HUD_PANEL);
            cap.transform.localPosition = beltCenter + new Vector3(side * beltHalfLen, 0f, 0f);
            FitSprite(cap, blankSprite, beltRadius * 1.6f, beltRadius * 2.55f);
            cap.color = BELT_COLOR;
        }
    }

    void BuildEnergyVisual()
    {
        float y = 0.38f * halfH;
        energyPanel = MakeSprite(hudRoot, "EnergyPanel", blankSprite, Z_HUD_PANEL);
        energyPanel.transform.localPosition = new Vector3(0f, y, 0f);
        FitSprite(energyPanel, blankSprite, halfW * 0.44f, 0.085f * halfH);
        energyPanel.color = ENERGY_PANEL;

        // same sorting order as the panel meant the panel could cover it
        var bolt = MakeSprite(hudRoot, "EnergyIcon", energySprite, Z_HUD_TOP);
        bolt.transform.localPosition = new Vector3(-halfW * 0.11f, y, 0f);
        FitSprite(bolt, energySprite, 0.075f * halfH, 0.075f * halfH);
        energyIconPos = bolt.transform.localPosition;

        energyText = MakeText(hudRoot, "EnergyText", new Vector3(halfW * 0.05f, y, 0f), Z_HUD_TEXT, halfH * 0.019f);
        energyText.color = ENERGY_DIGITS;
    }

    // ---- jars -------------------------------------------------------------

    void AddJar(bool bonus)
    {
        var jar = new Jar
        {
            color = PickJarColor(),
            capacity = Random.Range(Mathf.Min(jarCapacityMin, jarCapacityMax), Mathf.Max(jarCapacityMin, jarCapacityMax) + 1),
            bonus = bonus,
        };
        jar.root = new GameObject(bonus ? "BonusJar" : "Jar").transform;
        jar.root.SetParent(hudRoot, false);

        jar.frame = MakeSprite(jar.root, "Frame", roundedSprite, Z_JAR_FRAME);
        jar.frame.color = BONUS_JAR;
        jar.glass = MakeSprite(jar.root, "Glass", jarSprite, Z_HUD);
        jar.lid = MakeSprite(jar.root, "Lid", blankSprite, Z_HUD_TOP);
        jar.text = MakeText(jar.root, "Count", Vector3.zero, Z_HUD_TEXT, halfH * 0.018f);

        jar.root.localScale = Vector3.one * 0.2f;      // grows in via UpdateFeel
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
            FitSprite(jar.frame, roundedSprite, w * 1.10f, w * 1.20f);
            FitSprite(jar.lid, blankSprite, w * 0.74f, w * 0.24f);
            jar.lid.transform.localPosition = new Vector3(0f, w * 0.38f, 0f);
            jar.text.transform.localPosition = new Vector3(0f, w * 0.38f, 0f);
            jar.text.characterSize = w * 0.048f;
        }
        RefreshJars();
    }

    // Flat random. The jar used to be weighted towards what was stuck on the belt
    // and what was still buried, which quietly refunded every wrong dig — pieces
    // nobody wanted always got a matching jar eventually. Straight random means a
    // wrong colour is a real loss.
    int PickJarColor() => Random.Range(0, Mathf.Min(BLOCK_COLORS.Length, blockSprites.Length));

    void RefreshJars()
    {
        foreach (var jar in jars)
        {
            // tint the whole jar, not just the lid — a thin coloured strip was
            // not enough to tell what the jar is asking for
            jar.lid.color = BLOCK_COLORS[jar.color];
            jar.glass.color = Color.Lerp(BLOCK_COLORS[jar.color], Color.white, 0.35f);
            jar.frame.enabled = jar.bonus;
            jar.text.text = (jar.capacity - jar.filled).ToString();
            jar.text.color = new Color(0.15f, 0.05f, 0.10f);
        }
    }

    // The energy is not credited here — it flies to the counter and is added as
    // each mote lands, so the number climbing matches what the eye is following.
    void CompleteJar(Jar jar)
    {
        bool wasBonus = jar.bonus;
        jars.Remove(jar);
        SpawnMotes(jar.root.position, jar.capacity);
        fadingJars.Add(new Fading { root = jar.root, t = 0f });

        if (!wasBonus) AddJar(false);      // permanent slots refill; the bonus one is spent
        else LayoutJars();

        RefreshHud();
        AssignPieces();
    }

    // What a click just cost or paid, rising off the cell it happened on.
    void SpawnFloater(Vector3 worldPos, string text, Color color)
    {
        var tm = MakeText(transform, "Floater", Vector3.zero, Z_HUD_TEXT + 2, cellSize * 0.075f);
        tm.transform.position = worldPos;
        tm.text = text;
        tm.color = color;
        floaters.Add(new Floater { tm = tm, from = worldPos, t = 0f });
    }

    void SpawnMotes(Vector3 from, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("EnergyMote");
            go.transform.SetParent(hudRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = energySprite;
            sr.sortingOrder = Z_HUD_TEXT + 1;   // motes fly over everything
            FitSprite(sr, energySprite, 0.05f * halfH, 0.05f * halfH);

            var m = new Mote { sr = sr, t = -0.09f * i };      // stagger so they read as several
            m.from = from + (Vector3)(Random.insideUnitCircle * 0.04f * halfH);
            m.pos = m.from;
            go.transform.position = m.pos;
            motes.Add(m);
        }
    }

    void UpdateFeel(float dt)
    {
        for (int i = 0; i < jars.Count; i++)
        {
            var jar = jars[i];
            if (jar.appear >= 1f) continue;
            jar.appear = Mathf.Min(1f, jar.appear + dt / Mathf.Max(0.05f, jarSwapTime));
            jar.root.localScale = Vector3.one * Mathf.SmoothStep(0.2f, 1f, jar.appear);
        }

        // the answer changes as pieces land, so it is asked every frame now
        CheckLoss();
        RefreshEnergyColor();

        for (int i = fadingJars.Count - 1; i >= 0; i--)
        {
            var f = fadingJars[i];
            f.t += dt / Mathf.Max(0.05f, jarSwapTime);
            if (f.root != null) f.root.localScale = Vector3.one * Mathf.SmoothStep(1f, 0f, f.t);
            if (f.t < 1f) continue;
            if (f.root != null) Destroy(f.root.gameObject);
            fadingJars.RemoveAt(i);
        }

        for (int i = floaters.Count - 1; i >= 0; i--)
        {
            var f = floaters[i];
            f.t += dt / Mathf.Max(0.05f, floaterTime);
            if (f.tm == null) { floaters.RemoveAt(i); continue; }

            f.tm.transform.position = f.from + Vector3.up * (cellSize * 0.8f * Mathf.SmoothStep(0f, 1f, f.t));
            var c = f.tm.color;
            c.a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 1f, f.t));
            f.tm.color = c;

            if (f.t < 1f) continue;
            Destroy(f.tm.gameObject);
            floaters.RemoveAt(i);
        }

        Vector3 target = hudRoot != null ? hudRoot.TransformPoint(energyIconPos) : Vector3.zero;
        for (int i = motes.Count - 1; i >= 0; i--)
        {
            var m = motes[i];
            m.t += dt / Mathf.Max(0.05f, moteTime);
            if (m.t < 0f) { m.sr.enabled = false; continue; }
            m.sr.enabled = true;

            float e = 1f - (1f - Mathf.Clamp01(m.t)) * (1f - Mathf.Clamp01(m.t));
            m.pos = Vector3.Lerp(m.from, target, e);
            m.sr.transform.position = m.pos;

            if (m.t < 1f) continue;
            Destroy(m.sr.gameObject);
            motes.RemoveAt(i);
            energy++;
            RefreshHud();
        }
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
            int need = jar.capacity - jar.filled - jar.incoming;
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
            if (jar.filled >= jar.capacity) CompleteJar(jar);
        }

        AssignPieces();
    }

    // ---- input ------------------------------------------------------------

    void Update()
    {
        HandleInput();
        UpdatePieces(Time.deltaTime);
        UpdateFeel(Time.deltaTime);
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

        // ignore anything still tucked under the HUD, including mid-scroll
        if (boardTopY - row * cellSize + scrollY > windowTop + 0.001f) return;
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

    // Touching the dug-out area. Iced cells count: they are part of the front even
    // though they cannot be clicked yet, and dimming them made them look broken.
    bool Reachable(Cell cell)
    {
        if (cell.broken || cell.kind == Kind.Missing) return false;
        if (Touches(cell)) return true;
        return cell.twin != null && !cell.twin.broken && Touches(cell.twin);   // either half opens the pair
    }

    bool Touches(Cell cell)
    {
        if (cell.row == 0) return true;                  // the entrance is always open
        return IsBroken(cell.col - 1, cell.row) || IsBroken(cell.col + 1, cell.row)
            || IsBroken(cell.col, cell.row - 1) || IsBroken(cell.col, cell.row + 1);
    }

    // Both bonus cells are gifts, so neither charges for the click.
    bool IsFree(Cell cell) => cell.kind == Kind.Energy || cell.kind == Kind.JarCell;

    bool Clickable(Cell cell)
    {
        if (!Reachable(cell)) return false;
        return !cell.ice || cell.freedNeighbours >= 2;
    }

    bool IsBroken(int c, int r)
    {
        if (c < 0 || c >= boardWidth || r < 0 || r >= boardHeight) return false;
        return board[c, r].broken;
    }

    void TryBreak(Cell cell)
    {
        if (!Clickable(cell)) return;

        bool free = IsFree(cell);
        if (!free && energy <= 0) return;
        if (!free) energy--;

        // only say something when the energy actually moved: a jar cell is a gift,
        // and the new slot appearing in the HUD is its own receipt
        if (cell.kind == Kind.Energy) SpawnFloater(cell.root.position, "+1", GAIN_TEXT);
        else if (!free) SpawnFloater(cell.root.position, "-1", COST_TEXT);

        switch (cell.kind)
        {
            case Kind.Energy:
                energy++;
                cell.broken = true;
                break;

            case Kind.JarCell:
                cell.broken = true;
                AddJar(true);
                break;

            default:
                // chipping the shell off a petrified block gives you nothing
                if (cell.color != STONE) SpawnPieces(cell, cell.color, piecesPerBlock);
                if (cell.nest.Count > 0)
                {
                    // strip one shell; the next layer spreads to full size and stays
                    cell.color = cell.nest[0];
                    cell.nest.RemoveAt(0);
                }
                else
                {
                    cell.broken = true;
                    var twin = cell.twin;
                    if (twin != null && !twin.broken)
                    {
                        SpawnPieces(twin, twin.color, piecesPerBlock);   // the far half pays too
                        twin.broken = true;
                        Opened(twin);
                    }
                }
                break;
        }

        if (cell.broken) Opened(cell);

        RefreshBoard();
        RefreshHud();
        CheckLoss();
    }

    void Opened(Cell cell)
    {
        deepestBroken = Mathf.Max(deepestBroken, cell.row);
        Bump(cell.col - 1, cell.row);
        Bump(cell.col + 1, cell.row);
        Bump(cell.col, cell.row - 1);
        Bump(cell.col, cell.row + 1);
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
        if (finished) return;

        // Walled in by holes with the door still shut. Without this the game just
        // stops responding and never says why.
        if (!DoorOpen() && !AnyClickable(false))
        {
            finished = true;
            statusText.text = "DEAD END";
            statusText.color = new Color(1f, 0.55f, 0.35f);
            return;
        }

        if (energy > 0) return;
        if (EnergyPending()) return;                     // the jars have not finished paying
        if (AnyClickable(true)) return;                  // a free bonus cell is still in reach

        finished = true;
        statusText.text = "OUT OF ENERGY";
        statusText.color = new Color(1f, 0.45f, 0.45f);
    }

    // Anything still owed: pieces in the air, energy in the air, or a jar the belt
    // can already finish. Declaring a loss before all that settles was calling the
    // game over while the payout was literally still flying across the screen.
    bool EnergyPending()
    {
        if (motes.Count > 0 || toJar.Count > 0) return true;

        foreach (var jar in jars)
        {
            int onBelt = 0;
            foreach (var piece in belt)
                if (piece.color == jar.color) onBelt++;
            if (jar.filled + jar.incoming + onBelt >= jar.capacity) return true;
        }
        return false;
    }

    bool AnyClickable(bool freeOnly)
    {
        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
            {
                var cell = board[c, r];
                if (freeOnly && !IsFree(cell)) continue;
                if (Clickable(cell)) return true;
            }
        return false;
    }

    // ---- refresh ----------------------------------------------------------

    void RefreshBoard()
    {
        var dist = FogDistances();

        for (int r = 0; r < boardHeight; r++)
            for (int c = 0; c < boardWidth; c++)
            {
                var cell = board[c, r];
                int d = dist[c, r];
                if (cell.twin != null && !cell.twin.broken)
                    d = Mathf.Min(d, dist[cell.twin.col, cell.twin.row]);
                RefreshCell(cell, d);
            }

        bool open = DoorOpen();
        door.color = open ? DOOR_OPEN : DOOR_LOCKED;
        doorText.text = open ? "EXIT" : "";
        doorText.color = new Color(0.1f, 0.25f, 0.1f);

        scrollTarget = ScrollFor(deepestBroken + scrollLead);
    }

    // Neutral grey on purpose. The old tint had more blue than green in it, so it
    // did not just darken — it dragged pink towards purple.
    static readonly Color DIM = new Color(0.66f, 0.66f, 0.66f, 1f);

    void RefreshCell(Cell cell, int distance)
    {
        // a hole really is nothing: no floor either, the background is the abyss
        if (cell.kind == Kind.Missing)
        {
            cell.floor.enabled = false;
            cell.body.enabled = cell.innerBody.enabled = cell.innerBody2.enabled = false;
            cell.icon.enabled = cell.iceLayer.enabled = cell.fog.enabled = false;
            return;
        }

        // Every real cell keeps its tile whatever else is going on — dug out, still
        // buried, or hidden behind a question mark. That is what makes the shaft
        // read as a board instead of a few floating blocks.
        cell.floor.enabled = true;

        if (cell.broken)
        {
            cell.body.enabled = cell.innerBody.enabled = cell.innerBody2.enabled = false;
            cell.icon.enabled = cell.iceLayer.enabled = cell.fog.enabled = false;
            return;
        }

        Color tint = Reachable(cell) ? Color.white : DIM;

        bool hidden = distance > revealRadius;
        cell.fog.enabled = hidden;
        cell.fog.color = tint;
        if (hidden)
        {
            cell.body.enabled = cell.innerBody.enabled = cell.innerBody2.enabled = false;
            cell.icon.enabled = cell.iceLayer.enabled = false;
            return;
        }

        // a 2x1 is drawn once, by its head, stretched across both cells
        bool isBlock = cell.kind == Kind.Block;
        bool tail = cell.twin != null && !cell.twinHead && !cell.twin.broken;
        cell.body.enabled = isBlock && !tail;
        if (cell.body.enabled)
        {
            // a stone shell reuses the grey square, with the real block showing
            // through inside it — same read as a double, so it is obvious that it
            // takes one click to crack and a second one to collect
            bool rock = cell.color == STONE && cell.nest.Count == 0;
            cell.body.sprite = cell.color != STONE ? blockSprites[cell.color]
                             : rock ? roundedSprite      // solid slab, nothing inside
                                    : fogSprite;         // shell with a block under it
            cell.body.color = rock ? SOLID_ROCK * tint : tint;
            ShapeBody(cell);
        }

        DrawNested(cell.innerBody, cell, 0, cellSize * 0.62f, tint);
        DrawNested(cell.innerBody2, cell, 1, cellSize * 0.34f, tint);

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
            // the cracked sprite says "one more neighbour" better than a digit did
            var sprite = cell.freedNeighbours >= 1 && iceCrackedSprite != null ? iceCrackedSprite : iceSprite;
            cell.iceLayer.sprite = sprite;
            FitSprite(cell.iceLayer, sprite, BlockSize, BlockSize);
            cell.iceLayer.color = tint;
        }

    }

    // A pair's head stretches over both cells and shifts half a cell towards its
    // partner, so the two read as one long block rather than two squares.
    void ShapeBody(Cell cell)
    {
        var twin = cell.twin;
        if (twin == null || twin.broken || !cell.twinHead)
        {
            cell.body.transform.localPosition = Vector3.zero;
            FitSprite(cell.body, cell.body.sprite, BlockSize, BlockSize);
            return;
        }

        bool sideways = twin.row == cell.row;
        cell.body.transform.localPosition = sideways
            ? new Vector3(cellSize * 0.5f, 0f, 0f)
            : new Vector3(0f, -cellSize * 0.5f, 0f);
        FitSprite(cell.body, cell.body.sprite,
            sideways ? PairSize : BlockSize,
            sideways ? BlockSize : PairSize);
    }

    // One buried layer of a double / triple cell, drawn smaller inside the last one.
    void DrawNested(SpriteRenderer sr, Cell cell, int index, float size, Color tint)
    {
        bool show = cell.kind == Kind.Block && cell.nest.Count > index;
        sr.enabled = show;
        if (!show) return;

        sr.sprite = blockSprites[cell.nest[index]];
        sr.color = tint;
        FitSprite(sr, sr.sprite, size, size);
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

    // The whole panel carries the warning: green while you are fine, yellow at
    // three, flashing red at two or less. The digit itself stays dark so it keeps
    // reading against all three.
    void RefreshEnergyColor()
    {
        if (energyPanel == null) return;

        if (energy <= 2)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 9f);
            energyPanel.color = Color.Lerp(ENERGY_LOW, ENERGY_LOW_FLASH, pulse);
        }
        else energyPanel.color = energy == 3 ? ENERGY_WARN : ENERGY_PANEL;

        if (energyText != null) energyText.color = ENERGY_DIGITS;
    }

    void RefreshHud()
    {
        energyText.text = energy.ToString();
        RefreshEnergyColor();
        RefreshJars();
    }

    // ---- scrolling --------------------------------------------------------

    float ScrollFor(int focusRow)
    {
        float rowY = boardTopY - (focusRow + 0.5f) * cellSize;
        float want = windowBottom + (boardTopY - windowBottom) * 0.5f;
        float offset = want - rowY;

        // Snap to whole cells. A free-floating offset left the top row sliced in
        // half under the HUD, and half a cell still invited a click.
        float lowest = boardTopY - (boardHeight + 1.5f) * cellSize;
        float maxOffset = Mathf.Ceil(Mathf.Max(0f, windowBottom - lowest) / cellSize) * cellSize;
        return Mathf.Clamp(Mathf.Round(offset / cellSize) * cellSize, 0f, maxOffset);
    }

    void ScrollBoard()
    {
        if (boardRoot == null) return;
        scrollY = Mathf.Lerp(scrollY, scrollTarget, 1f - Mathf.Exp(-8f * Time.deltaTime));
        boardRoot.localPosition = new Vector3(0f, scrollY, 0f);
    }
}
