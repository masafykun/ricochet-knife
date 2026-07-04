using System.Collections.Generic;
using UnityEngine;

// RICOCHET — a one-tap 3D knife game (Knife Hit family), the studio's DISTINCT 4th core.
// The three earlier knife games all spin the TARGET and keep the strike point fixed:
//   * blade-circus: STICK knives into a spinning wheel, avoid your own stuck blades.
//   * spin-gate:    THREAD a knife through a moving GAP to shatter a core behind it.
//   * orbit-slice:  SLICE fruit riding a spinning rotor past a fixed strike window.
// RICOCHET inverts that: the TARGETS are STATIONARY on the rim, and the AIM spins.
// A chrome DEFLECTOR blade rotates at the hub. Your only control is FLICK (tap / click / Space / Up):
// the knife jabs up into the hub and BANKS off the blade, flying out along the blade's angle at the
// instant you tapped (WYSIWYG — the locked aim ray flashes so you see your commitment) into whatever
// stationary rim GEM sits there. Clean banks climb a pentatonic COMBO; dead-centre = PERFECT bonus.
// Clear every gem on the rim = BOARD CLEAR -> a faster, wobblier, denser board. Black SPIKE-BOMBS also
// ride the rim: bank into one and it DETONATES = GAME OVER (the only death — reading the sweep is the
// game). Empty air = WHIFF (combo resets, no death). Combo>=8 lights FEVER (score x2 + gold bloom +
// a wider, more forgiving bank window).
//
// Built entirely in code (CreatePrimitive + procedural placement) so it renders reliably in WebGL with
// engine-code stripping disabled. NO Rigidbody/colliders: the deflector is pure Transform rotation and
// every hit is an angular test. Coexists with the permanent Juice & AutoShot helpers.
public class Ricochet : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__Ricochet");
        go.AddComponent<Ricochet>();
        DontDestroyOnLoad(go);
    }

    // ---------------------------------------------------------------- tuning
    const float WHEEL_Y = 1.9f;                            // hub world Y
    static readonly Vector3 HUB = new Vector3(0f, WHEEL_Y, 0f);
    const float DEFL_R = 1.55f;                            // deflector blade half-length
    const float RIM_R = 2.72f;                             // radius at which gems sit (out past the blade)
    const float ITEM_Z = -0.30f;                           // gems sit slightly in front of the hub plane
    const float KNIFE_Z = -0.55f;                          // knife plane (in front of gems)
    const int SLOTS = 16;                                  // rim mount points, 22.5 deg apart
    const float SLOT_DEG = 360f / SLOTS;

    const float LAUNCH_Y = WHEEL_Y - (RIM_R + 1.55f);      // ready-knife rest Y (below the rim)
    const float RISE_SPEED = 30f;                          // jab-up speed to the hub
    const float FLY_SPEED = 26f;                           // outward bank speed
    const float KNIFE_TIP = 1.02f;

    const float TOL = 8.6f;                                // bank tolerance (deg from a slot centre)
    const float PERFECT = 3.2f;                            // dead-centre bonus window
    const int FEVER_COMBO = 8;

    // ---------------------------------------------------------------- scene refs
    Transform camT; Camera camComp;
    Transform deflT;                  // spins about Z (the chrome blade)
    Transform flyKnifeT;              // the single in-flight / ready knife
    Transform hotTipT;                // glowing firing tip on the blade
    Transform aimRayT;                // flashes along the locked bank direction
    Transform launchGlowT, hubGlowT;
    Material aimRayMat, hotTipMat, hubMat;

    TextMesh hudScore, hudBest, hudLevel, comboText, bannerText, hintText, feverText, dbg;
    Transform chargeFill, chargeBg;   // top FEVER charge strip

    Material steelMat, handleMat, boltMat, deflBody, deflEdge,
             bombCore, bombSpike, bombFuse, bombRing, goldMat, glowMat, stripBg, stripFill, stemMat;
    Material[] gemMats;
    Color[] gemCols;

    // ---------------------------------------------------------------- run state
    enum State { Playing, Dead }
    State state = State.Playing;
    enum Fly { Ready, Rising, Banking, Reload }
    Fly fly = Fly.Ready;

    int score, best, combo, bestCombo, level, gemsCleared;
    bool attract = true;              // auto-demo until first real input
    bool showDbg, fever;
    float feverTint;

    // deflector spin model: angVel(t) = dir*(base + amp*sin(t*freq))
    float defAngle, spinBase, spinAmp, spinFreq, spinDir = 1f, spinT;

    float flyPos;                     // Rising: world Y ; Banking: distance out from hub
    float bankDir;                    // captured aim angle (deg)
    float reloadTimer, deathTimer, comboFlash, whiffFlash, bannerTimer, aimRayFlash;
    string lastGrade = "";

    // ---- stationary rim items ----
    // kind: 0 empty, 1 gem, 2 gold, 3 bomb
    class Slot
    {
        public Transform root;        // fixed on the rim
        public Transform gemPivot, bombPivot;
        public MeshRenderer gemMr;
        public float ang;             // fixed rim angle (deg)
        public int kind, colorIdx;
        public float bombPass;        // (unused churn hook) kept for parity/debug
    }
    readonly Slot[] slots = new Slot[SLOTS];

    // ---- gem-shatter halves fx pool ----
    class Half { public Transform t; public MeshRenderer mr; public Vector3 vel, spin; public float life; public bool alive; }
    readonly List<Half> halves = new List<Half>();
    const int HALF_POOL = 24;
    int halfCursor;

    // HUD layout (aspect-adaptive)
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f, hudPf = 1f;
    bool portraitHud;
    const float HUD_Z = 6.5f;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);
        foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            if (mr.gameObject.name == "Cube" || mr.gameObject.name == "Sphere" || mr.gameObject.name == "Plane")
                Destroy(mr.gameObject);

        best = PlayerPrefs.GetInt("ricochet_best", 0);
        bestCombo = PlayerPrefs.GetInt("ricochet_bestcombo", 0);

        BuildMaterials();
        BuildEnvironment();
        BuildCamera();
        BuildDeflector();
        BuildSlots();
        BuildKnife();
        BuildHalves();
        BuildHud();

        NewGame(true);
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.3f, bool emissive = false, float emi = 0.8f, float alpha = 1f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        c.a = alpha;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * emi); }
        if (alpha < 1f) SetTransparent(m, c);
        return m;
    }

    static void SetTransparent(Material m, Color c)
    {
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    void BuildMaterials()
    {
        steelMat  = Mat(new Color(0.90f, 0.94f, 1.00f), 0.35f, 0.72f);
        handleMat = Mat(new Color(0.16f, 0.30f, 0.56f), 0.10f, 0.45f);
        boltMat   = Mat(new Color(0.98f, 0.84f, 0.36f), 0.70f, 0.80f, true, 0.5f);
        deflBody  = Mat(new Color(0.78f, 0.85f, 0.95f), 0.85f, 0.72f);
        deflEdge  = Mat(new Color(0.55f, 0.80f, 1.00f), 0.30f, 0.6f, true, 1.3f);
        hubMat    = Mat(new Color(0.30f, 0.85f, 0.98f), 0.20f, 0.6f, true, 1.1f);
        hotTipMat = Mat(new Color(1f, 0.55f, 0.20f), 0.20f, 0.6f, true, 2.0f);

        goldMat   = Mat(new Color(1f, 0.80f, 0.22f), 0.35f, 0.75f, true, 1.4f);
        glowMat   = Mat(new Color(1f, 0.92f, 0.55f, 0.16f), 0f, 0.2f, true, 1.0f, 0.16f);
        stemMat   = Mat(new Color(0.30f, 0.70f, 0.35f), 0.1f, 0.4f);

        bombCore  = Mat(new Color(0.06f, 0.06f, 0.09f), 0.35f, 0.55f);
        bombSpike = Mat(new Color(0.10f, 0.10f, 0.14f), 0.45f, 0.6f);
        bombFuse  = Mat(new Color(1f, 0.24f, 0.16f), 0.1f, 0.5f, true, 2.2f);
        bombRing  = Mat(new Color(1f, 0.18f, 0.12f, 0.5f), 0f, 0.3f, true, 1.4f, 0.5f);

        aimRayMat = Mat(new Color(1f, 0.85f, 0.45f, 0.5f), 0f, 0.2f, true, 1.8f, 0.5f);

        stripBg   = Mat(new Color(0.10f, 0.12f, 0.20f, 0.55f), 0f, 0.2f, false, 0f, 0.55f);
        stripFill = Mat(new Color(1f, 0.62f, 0.22f), 0f, 0.4f, true, 1.6f);

        gemCols = new Color[] {
            new Color(0.20f, 0.95f, 0.85f),   // teal
            new Color(0.55f, 1.00f, 0.35f),   // lime
            new Color(1.00f, 0.35f, 0.72f),   // pink
            new Color(0.65f, 0.45f, 1.00f),   // violet
            new Color(1.00f, 0.55f, 0.20f),   // orange
        };
        gemMats = new Material[gemCols.Length];
        for (int i = 0; i < gemCols.Length; i++) gemMats[i] = Mat(gemCols[i], 0.20f, 0.6f, true, 1.15f);
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Material shared)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared;
        return g;
    }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.96f, 0.90f);
        sun.intensity = 1.05f;
        sun.transform.rotation = Quaternion.Euler(40f, -18f, 0f);
        sun.shadows = LightShadows.None;

        var spot = new GameObject("Spot").AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.color = new Color(0.85f, 0.92f, 1f);
        spot.intensity = 1.7f;
        spot.range = 30f; spot.spotAngle = 60f;
        spot.transform.position = new Vector3(0.2f, 8.0f, -7.2f);
        spot.transform.rotation = Quaternion.Euler(46f, -1.5f, 0f);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.26f, 0.30f, 0.48f);
        RenderSettings.ambientEquatorColor = new Color(0.18f, 0.20f, 0.32f);
        RenderSettings.ambientGroundColor = new Color(0.06f, 0.07f, 0.12f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.05f, 0.06f, 0.12f);
        RenderSettings.fogStartDistance = 16f;
        RenderSettings.fogEndDistance = 54f;

        var back = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(80f, 50f, 0.6f),
            Mat(new Color(0.08f, 0.09f, 0.17f), 0.0f, 0.1f));
        back.transform.position = new Vector3(0, 5f, 9f);

        // lit rim border (a slightly larger disk peeking behind the dark dial plate = a clean ring edge)
        var rimAccent = Prim(PrimitiveType.Cylinder, null, Vector3.zero, new Vector3((RIM_R + 0.62f) * 2f, 0.02f, (RIM_R + 0.62f) * 2f),
            Mat(new Color(0.22f, 0.36f, 0.62f), 0.1f, 0.5f, true, 0.7f));
        rimAccent.transform.position = new Vector3(0, WHEEL_Y, 0.42f);
        rimAccent.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // solid dark dial plate behind the gems — gives the board a defined face so gems/blade pop
        var plate = Prim(PrimitiveType.Cylinder, null, Vector3.zero, new Vector3((RIM_R + 0.48f) * 2f, 0.02f, (RIM_R + 0.48f) * 2f),
            Mat(new Color(0.10f, 0.13f, 0.22f), 0.15f, 0.35f));
        plate.transform.position = new Vector3(0, WHEEL_Y, 0.28f);
        plate.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        // a faint inset ring for depth on the plate face
        var inset = Prim(PrimitiveType.Cylinder, null, Vector3.zero, new Vector3(RIM_R * 1.5f, 0.02f, RIM_R * 1.5f),
            Mat(new Color(0.13f, 0.17f, 0.28f), 0.1f, 0.3f));
        inset.transform.position = new Vector3(0, WHEEL_Y, 0.24f);
        inset.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        var floor = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(60f, 0.3f, 24f),
            Mat(new Color(0.07f, 0.09f, 0.16f), 0.0f, 0.15f));
        floor.transform.position = new Vector3(0, -5.6f, 5f);
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.04f, 0.05f, 0.11f);
        camComp.fieldOfView = 48f;
        camComp.farClipPlane = 130f;
        cgo.AddComponent<AudioListener>();
        camT = cgo.transform;
        camT.rotation = Quaternion.Euler(2f, 0f, 0f);
        UpdateCameraRig();
    }

    // Pull back so the whole rim + launcher fit on any aspect (tall phones don't clip the sides).
    void UpdateCameraRig()
    {
        if (camComp == null || camT == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        float halfVtan = Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        const float TARGET_HALF_W = 3.55f;
        float dist = TARGET_HALF_W / Mathf.Max(0.05f, halfVtan * aspect);
        dist = Mathf.Clamp(dist, 9.2f, 15.5f);
        camT.position = new Vector3(0f, WHEEL_Y - 0.85f, -dist);
    }

    // ===================================================================== deflector (spinning blade)
    void BuildDeflector()
    {
        deflT = new GameObject("Deflector").transform;
        deflT.position = HUB;

        // central chrome bar (points +X at defAngle 0). A sleek double-taper reads as a spinning blade.
        var bar = Prim(PrimitiveType.Cube, deflT, Vector3.zero, new Vector3(DEFL_R * 2f, 0.34f, 0.30f), deflBody);
        bar.transform.localPosition = new Vector3(0, 0, ITEM_Z + 0.05f);
        // leading edges (thin bright rails top/bottom of the bar)
        Prim(PrimitiveType.Cube, deflT, new Vector3(0, 0.20f, ITEM_Z + 0.02f), new Vector3(DEFL_R * 2f, 0.06f, 0.10f), deflEdge);
        Prim(PrimitiveType.Cube, deflT, new Vector3(0, -0.20f, ITEM_Z + 0.02f), new Vector3(DEFL_R * 2f, 0.06f, 0.10f), deflEdge);

        // FIRING tip: a glowing wedge at +X so the player can see which way it will bank.
        var tip = new GameObject("HotTip").transform; tip.SetParent(deflT, false);
        var wedge = Prim(PrimitiveType.Cube, tip, new Vector3(DEFL_R + 0.10f, 0f, ITEM_Z), new Vector3(0.42f, 0.42f, 0.30f), hotTipMat);
        wedge.transform.localRotation = Quaternion.Euler(0, 0, 45f);
        Prim(PrimitiveType.Cube, tip, new Vector3(DEFL_R - 0.35f, 0f, ITEM_Z + 0.02f), new Vector3(0.55f, 0.14f, 0.12f), hotTipMat);
        hotTipT = tip;

        // hub
        var hub = Prim(PrimitiveType.Sphere, deflT, new Vector3(0, 0, ITEM_Z - 0.10f), new Vector3(0.66f, 0.66f, 0.5f), hubMat);
        hubGlowT = hub.transform;

        // static aim ray (activated during a throw) — a long thin quad from hub outward
        aimRayT = Prim(PrimitiveType.Quad, null, Vector3.zero, new Vector3(0.10f, RIM_R * 2f, 1f), aimRayMat).transform;
        aimRayT.gameObject.SetActive(false);
    }

    // ===================================================================== slots (stationary rim mounts)
    void BuildSlots()
    {
        for (int i = 0; i < SLOTS; i++)
        {
            float ang = i * SLOT_DEG;
            var root = new GameObject("Slot" + i).transform;
            root.position = HUB + LocalDir(ang) * RIM_R + new Vector3(0, 0, ITEM_Z);

            // dim socket marker so the dial's mount points read even when the slot is empty
            Prim(PrimitiveType.Sphere, root, new Vector3(0, 0, 0.34f), Vector3.one * 0.16f,
                Mat(new Color(0.24f, 0.34f, 0.52f), 0.2f, 0.4f, true, 0.5f));

            var gp = new GameObject("gem").transform; gp.SetParent(root, false);
            var gem = Prim(PrimitiveType.Cube, gp, Vector3.zero, new Vector3(0.46f, 0.46f, 0.46f), gemMats[0]);
            gem.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
            var gemMr = gem.GetComponent<MeshRenderer>();
            Prim(PrimitiveType.Cube, gp, new Vector3(0f, 0.30f, 0f), new Vector3(0.06f, 0.18f, 0.06f), stemMat);

            var bp = new GameObject("bomb").transform; bp.SetParent(root, false);
            Prim(PrimitiveType.Sphere, bp, Vector3.zero, Vector3.one * 0.48f, bombCore);
            for (int s = 0; s < 8; s++)
            {
                float sa = s * 45f * Mathf.Deg2Rad;
                var spk = Prim(PrimitiveType.Cube, bp, new Vector3(Mathf.Cos(sa) * 0.30f, Mathf.Sin(sa) * 0.30f, 0f),
                    new Vector3(0.12f, 0.26f, 0.12f), bombSpike);
                spk.transform.localRotation = Quaternion.Euler(0, 0, s * 45f);
            }
            Prim(PrimitiveType.Sphere, bp, new Vector3(0, 0, -0.16f), Vector3.one * 0.22f, bombFuse);
            Prim(PrimitiveType.Quad, bp, new Vector3(0, 0, 0.24f), Vector3.one * 1.15f, bombRing);
            bp.gameObject.SetActive(false);

            slots[i] = new Slot { root = root, gemPivot = gp, bombPivot = bp, gemMr = gemMr, ang = ang, kind = 0 };
            SetSlotKind(slots[i], 0, 0);
        }
    }

    void SetSlotKind(Slot s, int kind, int colorIdx)
    {
        s.kind = kind; s.colorIdx = colorIdx;
        bool gem = kind == 1 || kind == 2;
        s.gemPivot.gameObject.SetActive(gem);
        s.bombPivot.gameObject.SetActive(kind == 3);
        if (kind == 1) { s.gemMr.sharedMaterial = gemMats[colorIdx]; s.gemPivot.localScale = Vector3.one; }
        else if (kind == 2) { s.gemMr.sharedMaterial = goldMat; s.gemPivot.localScale = Vector3.one * 1.18f; }
    }

    // ===================================================================== knife
    void BuildKnife()
    {
        var root = new GameObject("FlyKnife").transform;
        Prim(PrimitiveType.Cube, root, new Vector3(0, 0.55f, 0), new Vector3(0.15f, 0.80f, 0.08f), steelMat);
        Prim(PrimitiveType.Cube, root, new Vector3(0, KNIFE_TIP, 0), new Vector3(0.045f, 0.24f, 0.045f), steelMat);
        Prim(PrimitiveType.Cube, root, new Vector3(0, 0.10f, 0), new Vector3(0.34f, 0.10f, 0.16f), boltMat);
        Prim(PrimitiveType.Cube, root, new Vector3(0, -0.45f, 0), new Vector3(0.19f, 0.85f, 0.19f), handleMat);
        Prim(PrimitiveType.Sphere, root, new Vector3(0, -0.92f, 0), Vector3.one * 0.21f, boltMat);
        flyKnifeT = root;
        flyKnifeT.position = new Vector3(0, LAUNCH_Y, KNIFE_Z);

        launchGlowT = Prim(PrimitiveType.Quad, null, Vector3.zero, new Vector3(0.7f, 1.9f, 1f),
            Mat(new Color(0.4f, 0.8f, 1f, 0.14f), 0f, 0.2f, true, 1.0f, 0.14f)).transform;
        launchGlowT.position = new Vector3(0, LAUNCH_Y, KNIFE_Z + 0.1f);
    }

    void BuildHalves()
    {
        for (int i = 0; i < HALF_POOL; i++)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
            g.name = "Half";
            g.transform.localScale = new Vector3(0.24f, 0.30f, 0.16f);
            var mr = g.GetComponent<MeshRenderer>();
            mr.sharedMaterial = gemMats[0];
            g.SetActive(false);
            halves.Add(new Half { t = g.transform, mr = mr, alive = false });
        }
    }

    // ===================================================================== game lifecycle
    void NewGame(bool boot)
    {
        state = State.Playing;
        fly = Fly.Ready;
        score = 0; combo = 0; level = 1; gemsCleared = 0;
        fever = false; feverTint = 0f;
        defAngle = 0f; spinT = 0f; spinDir = 1f;
        ApplyLevel(1);
        BuildBoard(1);

        flyPos = LAUNCH_Y;
        flyKnifeT.gameObject.SetActive(true);
        flyKnifeT.position = new Vector3(0, LAUNCH_Y, KNIFE_Z);
        flyKnifeT.rotation = Quaternion.identity;
        aimRayT.gameObject.SetActive(false);
        reloadTimer = 0f;

        hudScore.gameObject.SetActive(true);
        hudBest.gameObject.SetActive(true);
        hudLevel.gameObject.SetActive(true);
        comboText.text = ""; feverText.text = "";
        RefreshHud();
        if (boot) Banner("RICOCHET", new Color(1f, 0.7f, 0.35f), 1.2f);
        else Banner("LEVEL 1", new Color(0.4f, 0.95f, 1f), 1.0f);
    }

    void ApplyLevel(int lv)
    {
        level = lv;
        float t = Mathf.Clamp01((lv - 1) / 11f);
        spinBase = Mathf.Lerp(42f, 138f, t);
        spinAmp = (lv >= 3) ? Mathf.Lerp(8f, 54f, t) : 0f;
        spinFreq = Random.Range(0.55f, 1.2f);
        if (lv <= 2) spinDir = 1f;
        else if (Random.value < 0.4f) spinDir = -spinDir;
    }

    // Lay out gems + bombs for the level. Gems and bombs never share a slot, so every gem is always
    // reachable by aiming at it — the board is always clearable. Bombs stay a level or two behind gems
    // so the go/no-go read grows with skill.
    void BuildBoard(int lv)
    {
        for (int i = 0; i < SLOTS; i++) SetSlotKind(slots[i], 0, 0);

        int gemCount = Mathf.Clamp(5 + lv, 6, 12);
        int bombCount = lv <= 1 ? 0 : Mathf.Clamp(lv - 1, 1, 5);

        // shuffle slot indices
        var idx = new List<int>();
        for (int i = 0; i < SLOTS; i++) idx.Add(i);
        for (int i = idx.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); (idx[i], idx[j]) = (idx[j], idx[i]); }

        int p = 0;
        for (int g = 0; g < gemCount && p < idx.Count; g++, p++)
        {
            bool gold = lv >= 2 && Random.value < 0.14f;
            SetSlotKind(slots[idx[p]], gold ? 2 : 1, Random.Range(0, gemCols.Length));
        }
        for (int b = 0; b < bombCount && p < idx.Count; b++, p++)
            SetSlotKind(slots[idx[p]], 3, 0);
    }

    int GemsLeft()
    {
        int n = 0;
        for (int i = 0; i < SLOTS; i++) if (slots[i].kind == 1 || slots[i].kind == 2) n++;
        return n;
    }

    // ===================================================================== input
    bool FlickPressed()
    {
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) return true;
        if (Input.GetMouseButtonDown(0)) return true;
        for (int i = 0; i < Input.touchCount; i++)
            if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
        return false;
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        bool pressed = FlickPressed();
        if (pressed && attract) { attract = false; pressed = false; }

        // spin the deflector
        spinT += dt;
        float angVel = spinDir * (spinBase + spinAmp * Mathf.Sin(spinT * spinFreq));
        defAngle += angVel * dt;
        deflT.localRotation = Quaternion.Euler(0, 0, defAngle);

        // sparkle-spin gems + pulse bombs
        for (int i = 0; i < SLOTS; i++)
        {
            var s = slots[i];
            if (s.kind == 1 || s.kind == 2) s.gemPivot.Rotate(0f, 60f * dt, 0f, Space.Self);
            else if (s.kind == 3) s.bombPivot.localScale = Vector3.one * (0.85f + 0.15f * Mathf.Sin(Time.time * 9f));
        }

        switch (state)
        {
            case State.Playing: TickPlaying(dt, pressed, angVel); break;
            case State.Dead: TickDead(dt, pressed); break;
        }

        UpdateHalves(dt);
        if (comboFlash > 0f) comboFlash -= dt * 2.2f;
        if (whiffFlash > 0f) whiffFlash -= dt * 3f;
        if (aimRayFlash > 0f) aimRayFlash -= dt * 2.6f;
        UpdateAimRay();
        feverTint = Mathf.MoveTowards(feverTint, fever ? 1f : 0f, dt * 3f);
        ApplyFeverVisual();
        TickBanner(dt);
        UpdateCameraRig();
        AdjustHud();
        RefreshChargeStrip();
        if (showDbg) UpdateDbg(angVel);
    }

    void TickPlaying(float dt, bool pressed, float angVel)
    {
        bool fire = pressed;
        if (attract && fly == Fly.Ready) fire = AutoShouldFlick(angVel);

        if (fly == Fly.Ready)
        {
            reloadTimer -= dt;
            flyPos = LAUNCH_Y + Mathf.Sin(Time.time * 5f) * 0.04f;
            flyKnifeT.position = new Vector3(0, flyPos, KNIFE_Z);
            flyKnifeT.rotation = Quaternion.identity;
            launchGlowT.gameObject.SetActive(true);
            float pulse = 1f + Mathf.Sin(Time.time * 7f) * 0.12f;
            launchGlowT.localScale = new Vector3(0.7f, 1.9f * pulse, 1f);
            if (fire && reloadTimer <= 0f)
            {
                // WYSIWYG: lock the bank direction to the blade's angle at the instant of the tap.
                bankDir = Norm(defAngle);
                aimRayFlash = 1f;
                fly = Fly.Rising;
                flyPos = LAUNCH_Y;
                Juice.Blip(560f, 0.045f, 0.26f);
            }
        }
        else if (fly == Fly.Rising)
        {
            launchGlowT.gameObject.SetActive(false);
            flyPos += RISE_SPEED * dt;
            flyKnifeT.position = new Vector3(0, flyPos, KNIFE_Z);
            flyKnifeT.rotation = Quaternion.identity;
            if (flyPos >= WHEEL_Y)
            {
                // BANK off the blade at the hub
                fly = Fly.Banking;
                flyPos = 0f;
                Juice.Blip(720f, 0.04f, 0.22f);
                Juice.Pop(new Vector3(0, WHEEL_Y, KNIFE_Z), new Color(1f, 0.8f, 0.4f), 6);
            }
        }
        else if (fly == Fly.Banking)
        {
            flyPos += FLY_SPEED * dt;
            Vector3 d = LocalDir(bankDir);
            flyKnifeT.position = HUB + d * flyPos + new Vector3(0, 0, KNIFE_Z - HUB.z);
            flyKnifeT.rotation = Quaternion.Euler(0, 0, bankDir - 90f);
            if (flyPos >= RIM_R) Resolve();
        }
        else // Reload
        {
            reloadTimer -= dt;
            if (reloadTimer <= 0f)
            {
                fly = Fly.Ready;
                flyPos = LAUNCH_Y;
                flyKnifeT.position = new Vector3(0, LAUNCH_Y, KNIFE_Z);
                flyKnifeT.rotation = Quaternion.identity;
            }
        }
    }

    // Bank window widens for a forgiving onboarding and tightens as levels rise (mastery ramp).
    // Stays well under the 22.5 deg slot spacing so a bank never catches the adjacent slot's bomb.
    float BankWindow() => Mathf.Lerp(12f, 7.5f, Mathf.Clamp01((level - 1) / 11f));

    void Resolve()
    {
        float win = BankWindow();
        if (fever) win += 3.2f;

        // DANGER FIRST: a bomb inside the bank window detonates.
        int bombHit = -1; float bombErr = 999f;
        for (int i = 0; i < SLOTS; i++)
        {
            if (slots[i].kind != 3) continue;
            float e = AngDiff(slots[i].ang, bankDir);
            if (e < bombErr) { bombErr = e; bombHit = i; }
        }
        if (bombHit >= 0 && bombErr <= win) { Detonate(slots[bombHit].root.position); return; }

        // otherwise strike the nearest gem in the window
        int hit = -1; float hitErr = 999f;
        for (int i = 0; i < SLOTS; i++)
        {
            if (slots[i].kind != 1 && slots[i].kind != 2) continue;
            float e = AngDiff(slots[i].ang, bankDir);
            if (e < hitErr) { hitErr = e; hit = i; }
        }

        if (hit < 0 || hitErr > win) { Whiff(); return; }

        var s = slots[hit];
        Vector3 wp = s.root.position;
        bool gold = s.kind == 2;
        bool perfect = hitErr <= PERFECT;
        // GRAZE: a bomb sitting just outside the window, cleared cleanly, is a read-the-risk bonus.
        bool graze = bombHit >= 0 && bombErr <= win + 10f;
        Color col = gold ? new Color(1f, 0.82f, 0.25f) : gemCols[s.colorIdx];
        SpawnHalves(wp, col, gold);
        SetSlotKind(s, 0, 0);

        combo++;
        if (combo > bestCombo) { bestCombo = combo; PlayerPrefs.SetInt("ricochet_bestcombo", bestCombo); }
        int basePts = gold ? 50 : 10;
        if (perfect) basePts += gold ? 30 : 12;
        if (graze) basePts += 8;
        int mult = 1 + Mathf.Min(combo, 10) / 2;
        if (fever) mult *= 2;
        int gain = basePts * mult;
        score += gain;
        if (score > best) { best = score; PlayerPrefs.SetInt("ricochet_best", best); }

        int step = Mathf.Min(combo - 1, 12);
        float baseF = gold ? 700f : 540f;
        Juice.Blip(baseF * Mathf.Pow(1.05946f, PentaSemitone(step)), 0.075f, 0.4f);
        Juice.Score(wp);
        Juice.Pop(wp, col, gold ? 16 : 10);
        Juice.Shake(perfect ? 0.12f : 0.07f);

        lastGrade = perfect ? "PERFECT" : "BANK";
        string big = (gold ? "GOLD! " : "") + (perfect ? "PERFECT " : (graze ? "GRAZE " : ""));
        comboText.text = portraitHud ? ("x" + combo + "  +" + gain) : (big + "x" + combo + "   +" + gain);
        comboFlash = 1f;

        if (!fever && combo >= FEVER_COMBO) StartFever();

        gemsCleared++;
        fly = Fly.Reload; reloadTimer = 0.12f;
        flyKnifeT.position = new Vector3(0, LAUNCH_Y, KNIFE_Z);
        flyKnifeT.rotation = Quaternion.identity;

        if (GemsLeft() == 0) BoardClear();
        RefreshHud();
    }

    void Whiff()
    {
        combo = 0;
        if (fever) EndFever();
        whiffFlash = 1f;
        comboText.text = "MISS";
        comboFlash = 0.6f;
        lastGrade = "WHIFF";
        Juice.Blip(150f, 0.10f, 0.32f);
        Juice.Shake(0.06f);
        fly = Fly.Reload; reloadTimer = 0.14f;
        flyKnifeT.position = new Vector3(0, LAUNCH_Y, KNIFE_Z);
        RefreshHud();
    }

    void Detonate(Vector3 wp)
    {
        if (state == State.Dead) return;
        state = State.Dead;
        deathTimer = 0f;
        if (fever) EndFever();
        combo = 0;
        Juice.Lose();
        Juice.Blip(70f, 0.3f, 0.55f);
        Juice.Pop(wp, new Color(1f, 0.5f, 0.15f), 20);
        Juice.Pop(wp, new Color(1f, 0.85f, 0.3f), 14);
        Juice.Shake(0.7f);
        if (score > best) best = score;
        PlayerPrefs.SetInt("ricochet_best", Mathf.Max(best, PlayerPrefs.GetInt("ricochet_best", 0)));
        PlayerPrefs.Save();
        Banner("BOOM!   GAME OVER\nLevel " + level + "    Score " + score + "\nTAP / R to retry", new Color(1f, 0.7f, 0.4f), 999f);
        comboText.text = "";
        hudScore.gameObject.SetActive(false);
        hudBest.gameObject.SetActive(false);
        hudLevel.gameObject.SetActive(false);
        feverText.text = "";
        flyKnifeT.gameObject.SetActive(false);
        launchGlowT.gameObject.SetActive(false);
        aimRayT.gameObject.SetActive(false);
    }

    void BoardClear()
    {
        int bonus = 100 * level * (fever ? 2 : 1);
        score += bonus;
        if (score > best) { best = score; PlayerPrefs.SetInt("ricochet_best", best); }
        Juice.Blip(660f, 0.1f, 0.42f); Juice.Blip(990f, 0.12f, 0.42f); Juice.Blip(1320f, 0.12f, 0.4f);
        Juice.Shake(0.2f);
        ApplyLevel(level + 1);
        BuildBoard(level);
        Banner("BOARD CLEAR  +" + bonus + "\nLEVEL " + level, new Color(0.5f, 1f, 0.7f), 1.1f);
    }

    void StartFever()
    {
        fever = true;
        Banner("FEVER!  SCORE x2", new Color(1f, 0.8f, 0.3f), 1.1f);
        feverText.text = "FEVER";
        Juice.Blip(880f, 0.1f, 0.4f); Juice.Blip(1180f, 0.12f, 0.42f);
    }
    void EndFever() { fever = false; feverText.text = ""; }

    void TickDead(float dt, bool pressed)
    {
        deathTimer += dt;
        if (deathTimer > 0.45f && (Input.GetKeyDown(KeyCode.R) || pressed))
        {
            NewGame(false);
            attract = false;
        }
        else if (deathTimer > 6f)
        {
            NewGame(true);
            attract = true;
        }
    }

    // ===================================================================== aim ray
    void UpdateAimRay()
    {
        bool show = state == State.Playing && (fly == Fly.Rising || fly == Fly.Banking);
        aimRayT.gameObject.SetActive(show);
        if (!show) return;
        Vector3 d = LocalDir(bankDir);
        Vector3 mid = HUB + d * (RIM_R * 0.5f) + new Vector3(0, 0, ITEM_Z + 0.14f);
        aimRayT.position = mid;
        aimRayT.rotation = Quaternion.Euler(0, 0, bankDir - 90f);
        float w = 0.09f + 0.04f * Mathf.Max(0f, aimRayFlash);
        aimRayT.localScale = new Vector3(w, RIM_R, 1f);
        if (aimRayMat != null && aimRayMat.HasProperty("_EmissionColor"))
        {
            Color c = new Color(1f, 0.85f, 0.45f);
            aimRayMat.SetColor("_EmissionColor", c * (1.4f + Mathf.Max(0f, aimRayFlash) * 1.4f));
        }
    }

    // ===================================================================== gem-shatter fx
    void SpawnHalves(Vector3 wp, Color col, bool gold)
    {
        for (int k = 0; k < 2; k++)
        {
            var h = halves[halfCursor % halves.Count]; halfCursor++;
            h.alive = true;
            h.t.gameObject.SetActive(true);
            h.t.position = wp;
            h.t.rotation = Random.rotation;
            h.t.localScale = new Vector3(0.24f, 0.30f, 0.16f) * (gold ? 1.25f : 1f);
            h.mr.sharedMaterial = gold ? goldMat : gemMats[Mathf.Clamp(System.Array.IndexOf(gemCols, col), 0, gemCols.Length - 1)];
            float dir = (k == 0) ? -1f : 1f;
            h.vel = new Vector3(dir * Random.Range(2.4f, 3.6f), Random.Range(2.2f, 3.8f), Random.Range(-1f, -2.4f));
            h.spin = new Vector3(Random.Range(-360f, 360f), Random.Range(-360f, 360f), Random.Range(-360f, 360f));
            h.life = 0.85f;
        }
    }

    void UpdateHalves(float dt)
    {
        for (int i = 0; i < halves.Count; i++)
        {
            var h = halves[i];
            if (!h.alive) continue;
            h.life -= dt;
            h.vel += Vector3.down * 11f * dt;
            h.t.position += h.vel * dt;
            h.t.Rotate(h.spin * dt, Space.Self);
            h.t.localScale *= (1f - dt * 0.6f);
            if (h.life <= 0f) { h.alive = false; h.t.gameObject.SetActive(false); }
        }
    }

    // ===================================================================== auto-demo brain
    float autoCooldown;
    bool AutoShouldFlick(float angVel)
    {
        autoCooldown -= Time.deltaTime;
        if (autoCooldown > 0f) return false;

        // WYSIWYG: bank direction == current defAngle. Fire when a gem is dead-centre and no bomb is near.
        float aim = Norm(defAngle);
        int gemHit = -1; float gemErr = 999f, bombErr = 999f;
        for (int i = 0; i < SLOTS; i++)
        {
            if (slots[i].kind == 0) continue;
            float e = AngDiff(slots[i].ang, aim);
            if (slots[i].kind == 3) bombErr = Mathf.Min(bombErr, e);
            else if (e < gemErr) { gemErr = e; gemHit = i; }
        }
        // fire on a near dead-centre gem, as long as no bomb is close enough for spin drift to reach the
        // (fever-widened) window. Margin sits under the 22.5 deg slot spacing so a bomb neighbour won't stall it.
        if (gemHit >= 0 && gemErr < 3.4f && bombErr > BankWindow() + 9f)
        {
            autoCooldown = Random.Range(0.18f, 0.36f);
            return true;
        }
        return false;
    }

    // ===================================================================== fever visual
    void ApplyFeverVisual()
    {
        if (hubMat != null && hubMat.HasProperty("_EmissionColor"))
        {
            Color baseC = new Color(0.30f, 0.85f, 0.98f);
            Color hot = new Color(1f, 0.78f, 0.30f);
            hubMat.SetColor("_EmissionColor", Color.Lerp(baseC, hot, feverTint) * (1.0f + feverTint * 0.6f));
        }
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(camT, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudScore = MakeText(0.062f, new Color(1f, 0.95f, 0.7f), TextAnchor.UpperLeft);
        hudLevel = MakeText(0.050f, new Color(0.7f, 0.92f, 1f), TextAnchor.UpperLeft);
        hudBest = MakeText(0.050f, new Color(0.8f, 0.92f, 1f), TextAnchor.UpperRight);
        comboText = MakeText(0.072f, new Color(1f, 0.7f, 0.35f), TextAnchor.MiddleCenter);
        feverText = MakeText(0.070f, new Color(1f, 0.82f, 0.3f), TextAnchor.UpperCenter);
        bannerText = MakeText(0.10f, Color.white, TextAnchor.MiddleCenter);
        hintText = MakeText(0.046f, new Color(0.8f, 0.9f, 1f), TextAnchor.LowerCenter);
        dbg = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; bannerText.text = ""; feverText.text = "";
        hintText.text = "FLICK to bank  ·  clear gems, dodge bombs";

        chargeBg = Prim(PrimitiveType.Quad, camT, new Vector3(0, 0, HUD_Z), new Vector3(4f, 0.12f, 1f), stripBg).transform;
        chargeFill = Prim(PrimitiveType.Quad, camT, new Vector3(0, 0, HUD_Z - 0.01f), new Vector3(0.01f, 0.12f, 1f), stripFill).transform;

        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 5.4f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.5f, 1.3f);
        bool portrait = aspect < 0.85f;
        portraitHud = portrait;
        hudPf = portrait ? 0.62f : 1f;
        float pf = hudPf;
        float ix = halfW * 0.94f, iy = halfH * 0.92f;

        float topStrip = iy + 0.30f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudScore.characterSize = 0.062f * hudScale * pf;
        hudLevel.transform.localPosition = new Vector3(-ix, iy - 0.58f * hudScale * pf, HUD_Z); hudLevel.characterSize = 0.050f * hudScale * pf;
        hudBest.transform.localPosition = new Vector3(ix, iy, HUD_Z); hudBest.characterSize = 0.050f * hudScale * pf;
        feverText.transform.localPosition = new Vector3(0, iy - (portrait ? 0.52f : 0f) * hudScale, HUD_Z); feverText.characterSize = 0.060f * hudScale * pf;

        comboText.transform.localPosition = new Vector3(0, -halfH * 0.34f, HUD_Z);
        float cs = 0.072f * hudScale * pf;
        comboText.characterSize = (comboFlash <= 0f) ? cs : cs * (1f + Mathf.Max(0f, comboFlash) * 0.4f);

        hintText.transform.localPosition = new Vector3(0, -iy, HUD_Z); hintText.characterSize = 0.044f * hudScale * pf;
        dbg.transform.localPosition = new Vector3(-ix, -iy * 0.30f, HUD_Z); dbg.characterSize = 0.040f * hudScale;

        if (chargeBg)
        {
            float stripW = halfW * 1.86f;
            chargeBg.localPosition = new Vector3(0, topStrip, HUD_Z);
            chargeBg.localScale = new Vector3(stripW, 0.10f * hudScale, 1f);
            chargeFill.localPosition = new Vector3(0, topStrip, HUD_Z - 0.01f);
        }
    }

    void RefreshChargeStrip()
    {
        if (chargeFill == null || chargeBg == null) return;
        float frac = fever ? 1f : Mathf.Clamp01(combo / (float)FEVER_COMBO);
        float fullW = chargeBg.localScale.x;
        chargeFill.localScale = new Vector3(Mathf.Max(0.001f, fullW * frac), chargeBg.localScale.y, 1f);
        chargeFill.localPosition = new Vector3(-fullW * 0.5f + fullW * frac * 0.5f, chargeBg.localPosition.y, HUD_Z - 0.01f);
        if (stripFill != null && stripFill.HasProperty("_EmissionColor"))
        {
            Color c = fever ? new Color(1f, 0.82f, 0.3f) : new Color(1f, 0.55f, 0.22f);
            stripFill.SetColor("_BaseColor", c);
            stripFill.SetColor("_EmissionColor", c * (fever ? 2.2f : 1.4f));
        }
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE " + score;
        if (hudLevel) hudLevel.text = portraitHud ? ("LV " + level + "  ·  " + GemsLeft()) : ("LEVEL " + level + "   GEMS " + GemsLeft() + (combo > 1 ? "   COMBO " + combo : ""));
        if (hudBest) hudBest.text = "BEST " + best + (bestCombo > 1 ? (portraitHud ? "\nx" + bestCombo : "\nMAX x" + bestCombo) : "");
    }

    // ===================================================================== banners
    void Banner(string s, Color c, float dur)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.42f, HUD_Z);
        bannerText.characterSize = 0.085f * hudScale * hudPf;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }
    void TickBanner(float dt)
    {
        if (bannerTimer > 0f && bannerTimer < 900f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
    }

    // ===================================================================== helpers
    static Vector3 LocalDir(float deg)
    {
        float r = deg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(r), Mathf.Sin(r), 0f);
    }
    static float Norm(float deg) { deg %= 360f; if (deg < 0f) deg += 360f; return deg; }
    static float AngDiff(float a, float b)
    {
        float d = Mathf.Abs(Norm(a) - Norm(b)) % 360f;
        return d > 180f ? 360f - d : d;
    }
    static int PentaSemitone(int step)
    {
        int[] p = { 0, 2, 4, 7, 9 };
        return p[step % 5] + 12 * (step / 5);
    }

    void UpdateDbg(float angVel)
    {
        int gems = GemsLeft(), bombs = 0;
        for (int i = 0; i < SLOTS; i++) if (slots[i].kind == 3) bombs++;
        dbg.text = string.Format(
            "state {0}/{1} attract {2}\nlvl {3} spinBase {4:0} amp {5:0} dir {6:0}\ndefAng {7:0.0} angVel {8:0} bankDir {9:0.0}\ngems {10} bombs {11} combo {12} fever {13}\nscore {14} grade {15} fps {16:0}",
            state, fly, attract, level, spinBase, spinAmp, spinDir,
            Norm(defAngle), angVel, bankDir, gems, bombs, combo, fever, score, lastGrade,
            1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
    }
}
